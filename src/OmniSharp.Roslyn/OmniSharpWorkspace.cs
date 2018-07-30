using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Services.Client;
using Microsoft.VisualStudio.Services.Search.Shared.WebApi.Contracts;
using Microsoft.VisualStudio.Services.Search.WebApi;
using Microsoft.VisualStudio.Services.Search.WebApi.Contracts.Code;
using Microsoft.VisualStudio.Services.WebApi;
using OmniSharp.Models;
using OmniSharp.Models.V2;
using OmniSharp.Options;
using OmniSharp.Roslyn;
using OmniSharp.Utilities;

namespace OmniSharp
{
    public enum CodeSearchQueryType
    {
        FindDefinitions = 1,
        FindReferences = 2,
    };

    [Export, Shared]
    public class OmniSharpWorkspace : Workspace
    {
        public ConcurrentQueue<string> ProjectFilesToLoad { get; } = new ConcurrentQueue<string>();

        // HACK data
        private string _repoRoot;
        private Uri _repoUri;
        private IDictionary<string, IEnumerable<string>> _searchFilters;

        public bool Initialized { get; set; }
        public BufferManager BufferManager { get; private set; }

        private readonly ILogger<OmniSharpWorkspace> _logger;

        private object _searchClientLock = new object();
        private SearchHttpClient _searchClient;

        private int _isLoadingProjects;

        public bool IsLoadingProjects
        {
            get
            {
                return _isLoadingProjects != 0;
            }
            set
            {
                Interlocked.Exchange(ref _isLoadingProjects, value ? 1 : 0);
            }
        }

        public async Task WaitForQueueEmptyAsync()
        {
            if (HackOptions.Enabled)
            {
                while (IsLoadingProjects || ProjectFilesToLoad.Count > 0)
                {
                    await Task.Delay(100);
                }
            }
        }

        [ImportingConstructor]
        public OmniSharpWorkspace(HostServicesAggregator aggregator, ILoggerFactory loggerFactory)
            : base(aggregator.CreateHostServices(), "Custom")
        {
            BufferManager = new BufferManager(this);
            _logger = loggerFactory.CreateLogger<OmniSharpWorkspace>();
        }

        public void QueueProjectLoadForFile(string filePath)
        {
            if (!HackOptions.Enabled)
            {
                return;
            }

            if (filePath == null)
            {
                return;
            }

            string projectDir = Path.GetDirectoryName(filePath);
            while(projectDir != null)
            {
                List<string> csProjFiles = Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly).ToList();
                if (csProjFiles.Count > 0)
                {
                    foreach(string csProjFile in csProjFiles)
                    {
                        _logger.LogDebug($"Queueing project file {csProjFile} for {filePath}");
                        ProjectFilesToLoad.Enqueue(csProjFile);
                    }
                    return;
                }
                projectDir = Path.GetDirectoryName(projectDir);
            }

            _logger.LogWarning($"Couldn't find any C# projects for {filePath}");
        }

        public void InitCodeSearch(string targetDirectory)
        {
            // Fill in HACK data
            _repoRoot = GetRepoRootOrNull(targetDirectory);
            if (_repoRoot != null)
            {
                _searchFilters = GetRepoSearchFilters(_repoRoot, out _repoUri);
            }

            GetSearchClientAsync().FireAndForget(_logger);
        }

        private static readonly Regex CloneUriRegex =
            new Regex(
                @"(?ix)
                  https://(?<vsoaccount>.+)\.(?<vsodomain>visualstudio\.com)
                    /(?<collection>[^/]+)
                    /((?<project>.+(?<!/_git))
                    /)?_git
                    /(?<repo>[^/]+)
                    /?",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);


        private static string GetRepoRootOrNull(string targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
            {
                return null;
            }

            string repoRoot = targetDirectory;
            while (repoRoot != null && !Directory.Exists(Path.Combine(repoRoot, ".git")))
            {
                repoRoot = Path.GetDirectoryName(repoRoot);
            }

            return repoRoot;
        }

        private IDictionary<string, IEnumerable<string>> GetRepoSearchFilters(string repoRoot, out Uri vstsAccountUri)
        {
            var searchFilters = new Dictionary<string, IEnumerable<string>>();
            vstsAccountUri = null;
            try
            {
                string vstsUri = ProcessHelper.RunAndCaptureOutput("git.exe", "config --get remote.origin.url", repoRoot);
                if ((!string.IsNullOrEmpty(vstsUri)) && ParseVstsRepoUrl(
                    vstsUri,
                    out vstsAccountUri,
                    out string vstsProjectName,
                    out string vstsRepoName))
                {
                    searchFilters.Add("Project", new[] { vstsProjectName });
                    searchFilters.Add("Repository", new[] { vstsRepoName });
                    searchFilters.Add("Branch", new[] { "master" });
                    searchFilters.Add("Path", new[] { "" });
                    _logger.LogDebug($"Initialized VSTS Code Search filters for {vstsAccountUri.AbsoluteUri}, project {vstsProjectName}, repo {vstsRepoName}");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed initialize VSTS Code Search with filters for repo '{repoRoot}'");
            }

            return searchFilters;
        }

        public static bool ParseVstsRepoUrl(
            string vstsUri,
            out Uri vstsAccountUri,
            out string vstsProjectName,
            out string vstsRepoName)
        {
            vstsAccountUri = null;
            vstsProjectName = null;
            vstsRepoName = null;

            Match match = CloneUriRegex.Match(vstsUri);
            if (!match.Success)
            {
                return false;
            }

            string vsoAccount = match.Groups["vsoaccount"].Captures[0].Value;
            string vsoDomain = match.Groups["vsodomain"].Captures[0].Value;
            vstsRepoName = match.Groups["repo"].Captures[0].Value;
            string projectName = match.Groups["project"].Captures.Count > 0
                ? match.Groups["project"].Captures[0].Value
                : (match.Groups["collection"].Captures.Count > 0 ? match.Groups["collection"].Captures[0].Value : vstsRepoName);

            vstsAccountUri = new Uri($"https://{vsoAccount}.{vsoDomain}");
            vstsProjectName = projectName;
            return true;
        }

        public async Task<List<QuickFix>> QueryCodeSearch(string filter, int maxResults, TimeSpan maxDuration, bool wildCardSearch, CodeSearchQueryType searchType)
        {
            List<QuickFix> result = null;
            string searchTypeString;
            switch(searchType)
            {
                case CodeSearchQueryType.FindDefinitions:
                    searchTypeString = $"def:{filter}";
                    break;
                case CodeSearchQueryType.FindReferences:
                    searchTypeString = $"{filter}"; // Don't use 'ref:' because this removes for example 'new SomeClass' references from the result
                    break;
                default:
                    throw new InvalidOperationException($"Unknown search type {searchType}");
            }

            CodeSearchRequest request = new CodeSearchRequest
            {
                SearchText = $"ext:cs {searchTypeString}{(wildCardSearch ? "*" : string.Empty)}",
                Skip = 0,
                Top = maxResults,
                Filters = _searchFilters,
                IncludeFacets = false
            };

            try
            {
                _logger.LogDebug($"Querying VSTS Code Search with filter '{request.SearchText}'");
                SearchHttpClient client = await GetSearchClientAsync();
                if (client == null)
                {
                    return new List<QuickFix>();
                }

                using (var ct = new CancellationTokenSource(maxDuration))
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    CodeSearchResponse response = await client.FetchAdvancedCodeSearchResultsAsync(request, ct);
                    _logger.LogDebug($"Response from VSTS Code Search for filter '{request.SearchText}' completed in {sw.Elapsed.TotalSeconds} seconds and contains {response.Results.Count()} result(s)");

                    if (response != null)
                    {
                        result = await GetQuickFixesFromCodeResults(response.Results, searchType, filter, wildCardSearch);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to query VSTS Code Search for filter '{request.SearchText}'");
            }

            return result ?? new List<QuickFix>();
        }

        private async Task<List<QuickFix>> GetQuickFixesFromCodeResults(IEnumerable<CodeResult> codeResults,
            CodeSearchQueryType searchType, string searchFilter, bool wildCardSearch)
        {
            var transform = new TransformBlock<CodeResult, List<QuickFix>>(codeResult =>
            {
                string filePath = Path.Combine(_repoRoot, codeResult.Path.TrimStart('/').Replace('/', '\\'));
                if (!File.Exists(filePath))
                {
                    _logger.LogDebug($"File {filePath} from CodeResult doesn't exist");
                    return null;
                }

                return GetQuickFixeFromCodeResult(codeResult, filePath, searchType, searchFilter, wildCardSearch);
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount - 1,
                BoundedCapacity = DataflowBlockOptions.Unbounded
            });

            var buffer = new BufferBlock<List<QuickFix>>();
            transform.LinkTo(buffer, new DataflowLinkOptions { PropagateCompletion = true });

            foreach(CodeResult codeResult in codeResults)
            {
                await transform.SendAsync(codeResult);
            }
            transform.Complete();

            var allFoundSymbols = new List<QuickFix>();
            while (await buffer.OutputAvailableAsync().ConfigureAwait(false))
            {
                foreach (List<QuickFix> symbols in buffer.ReceiveAll().Where(item => item != null))
                {
                    allFoundSymbols.AddRange(symbols);
                }
            }

            // Propagate an exception if it occurred
            await buffer.Completion;
            return allFoundSymbols;
        }

        private List<QuickFix> GetQuickFixeFromCodeResult(CodeResult codeResult, string filePath, CodeSearchQueryType searchType,
            string searchFilter, bool wildCardSearch)
        {
            var foundSymbols = new List<QuickFix>();
            if (codeResult.Matches.TryGetValue("content", out IEnumerable<Hit> contentHits))
            {
                int lineOffset = 0;
                int lineNumber = 0;
                foreach (string line in File.ReadLines(filePath))
                {
                    foreach (Hit hit in contentHits.OrderBy(h => h.CharOffset))
                    {
                        if (hit.CharOffset >= lineOffset && hit.CharOffset < lineOffset + line.Length)
                        {
                            var symbolLocation = new SymbolLocation
                            {
                                Kind = SymbolKinds.Unknown,
                                FileName = filePath,
                                Line = lineNumber,
                                EndLine = lineNumber,
                                Column = hit.CharOffset - lineOffset,
                                EndColumn = hit.CharOffset - lineOffset + hit.Length,
                                Projects = new List<string>()
                            };

                            string symbolText = line.Substring(symbolLocation.Column, Math.Min(hit.Length, line.Length - symbolLocation.Column));
                            if (searchType == CodeSearchQueryType.FindReferences)
                            {
                                if (!symbolText.Equals(searchFilter, StringComparison.Ordinal))
                                {
                                    // VSTS Code Search is case-insensitive. Require complete case match when looking for references
                                    continue;
                                }

                                if (line.Trim().Length >= 2 && line.Trim().Substring(0, 2) == @"//")
                                {
                                    // Since 'ref:' at the moment isn't being used when querying for refs, at least ignore obvious mismatches like comments
                                    continue;
                                }

                                symbolLocation.Text = line.Trim();
                            }
                            else
                            {
                                if (symbolText.IndexOf(searchFilter, wildCardSearch ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) < 0)
                                {
                                    // detect the case when local enlistment and VSTS Code Search index is out of sync and skip such hits.
                                    // for non-wildCardSearch only return exact results since this is what expected by the callers
                                    continue;
                                }

                                symbolLocation.Text = symbolText;
                            }
                            foundSymbols.Add(symbolLocation);
                        }
                    }

                    lineOffset += line.Length + Environment.NewLine.Length;
                    lineNumber++;
                }
            }
            else
            {
                var symbolLocation = new SymbolLocation()
                {
                    Kind = SymbolKinds.Unknown,
                    Text = Path.GetFileNameWithoutExtension(codeResult.Filename),
                    FileName = filePath,
                    Line = 0,
                    Column = 0,
                    EndLine = 0,
                    EndColumn = 0,
                    Projects = new List<string>()

                };
                foundSymbols.Add(symbolLocation);
                _logger.LogDebug($"No content matches found for {filePath}");
            }
            return foundSymbols;
        }

        private async Task<SearchHttpClient> GetSearchClientAsync()
        {
            if (_repoRoot == null || _repoUri == null)
            {
                return null;
            }

            SearchHttpClient searchClient = null;
            if (_searchClient == null)
            {
                try
                {
                    _logger.LogDebug($"Creating VSTS Code Search Client for {_repoUri.AbsoluteUri}");
                    var connection = new VssConnection(_repoUri, new VssClientCredentials());
                    searchClient = await connection.GetClientAsync<SearchHttpClient>();
                    _logger.LogDebug($"Successfully created VSTS Code Search Client for {_repoUri.AbsoluteUri}");
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Failed to create VSTS Code Search Client for {_repoUri.AbsoluteUri}");
                }
            }

            lock (_searchClientLock)
            {
                if (_searchClient == null && searchClient != null)
                {
                    _searchClient = searchClient;
                }
            }

            return _searchClient;
        }

        public override bool CanOpenDocuments => true;

        public override void OpenDocument(DocumentId documentId, bool activate = true)
        {
            var doc = this.CurrentSolution.GetDocument(documentId);
            if (doc != null)
            {
                var text = doc.GetTextAsync(CancellationToken.None).WaitAndGetResult(CancellationToken.None);
                this.OnDocumentOpened(documentId, text.Container, activate);
            }
        }

        public override void CloseDocument(DocumentId documentId)
        {
            var doc = this.CurrentSolution.GetDocument(documentId);
            if (doc != null)
            {
                var text = doc.GetTextAsync(CancellationToken.None).WaitAndGetResult(CancellationToken.None);
                var version = doc.GetTextVersionAsync(CancellationToken.None).WaitAndGetResult(CancellationToken.None);
                var loader = TextLoader.From(TextAndVersion.Create(text, version, doc.FilePath));
                this.OnDocumentClosed(documentId, loader);
            }
        }

        public void AddProject(ProjectInfo projectInfo)
        {
            OnProjectAdded(projectInfo);
        }

        public void AddProjectReference(ProjectId projectId, Microsoft.CodeAnalysis.ProjectReference projectReference)
        {
            OnProjectReferenceAdded(projectId, projectReference);
        }

        public void RemoveProjectReference(ProjectId projectId, Microsoft.CodeAnalysis.ProjectReference projectReference)
        {
            OnProjectReferenceRemoved(projectId, projectReference);
        }

        public void AddMetadataReference(ProjectId projectId, MetadataReference metadataReference)
        {
            OnMetadataReferenceAdded(projectId, metadataReference);
        }

        public void RemoveMetadataReference(ProjectId projectId, MetadataReference metadataReference)
        {
            OnMetadataReferenceRemoved(projectId, metadataReference);
        }

        public void AddDocument(DocumentInfo documentInfo)
        {
            OnDocumentAdded(documentInfo);
        }

        public DocumentId AddDocument(ProjectId projectId, string filePath, SourceCodeKind sourceCodeKind = SourceCodeKind.Regular)
        {
            var documentId = DocumentId.CreateNewId(projectId);
            this.AddDocument(documentId, projectId, filePath, sourceCodeKind);
            return documentId;
        }

        public DocumentId AddDocument(DocumentId documentId, ProjectId projectId, string filePath, SourceCodeKind sourceCodeKind = SourceCodeKind.Regular)
        {
            var loader = new OmniSharpTextLoader(filePath);
            var documentInfo = DocumentInfo.Create(documentId, filePath, filePath: filePath, loader: loader, sourceCodeKind: sourceCodeKind);

            this.AddDocument(documentInfo);

            return documentId;
        }

        public void RemoveDocument(DocumentId documentId)
        {
            OnDocumentRemoved(documentId);
        }

        public void RemoveProject(ProjectId projectId)
        {
            OnProjectRemoved(projectId);
        }

        public void SetCompilationOptions(ProjectId projectId, CompilationOptions options)
        {
            OnCompilationOptionsChanged(projectId, options);
        }

        public void SetParseOptions(ProjectId projectId, ParseOptions parseOptions)
        {
            OnParseOptionsChanged(projectId, parseOptions);
        }

        public void OnDocumentChanged(DocumentId documentId, SourceText text)
        {
            OnDocumentTextChanged(documentId, text, PreservationMode.PreserveIdentity);
        }

        public DocumentId GetDocumentId(string filePath)
        {
            var documentIds = CurrentSolution.GetDocumentIdsWithFilePath(filePath);
            return documentIds.FirstOrDefault();
        }

        public IEnumerable<Document> GetDocuments(string filePath)
        {
            return CurrentSolution
                .GetDocumentIdsWithFilePath(filePath)
                .Select(id => CurrentSolution.GetDocument(id));
        }

        public Document GetDocument(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            var documentId = GetDocumentId(filePath);
            if (documentId == null)
            {
                return null;
            }

            return CurrentSolution.GetDocument(documentId);
        }

        public override bool CanApplyChange(ApplyChangesKind feature)
        {
            return true;
        }

        protected override void ApplyDocumentRemoved(DocumentId documentId)
        {
            var document = this.CurrentSolution.GetDocument(documentId);
            if (document != null)
            {
                DeleteDocumentFile(document.Id, document.FilePath);
                this.OnDocumentRemoved(documentId);
            }
        }

        private void DeleteDocumentFile(DocumentId id, string fullPath)
        {
            try
            {
                File.Delete(fullPath);
            }
            catch (IOException e)
            {
                LogDeletionException(e, fullPath);
            }
            catch (NotSupportedException e)
            {
                LogDeletionException(e, fullPath);
            }
            catch (UnauthorizedAccessException e)
            {
                LogDeletionException(e, fullPath);
            }
        }

        private void LogDeletionException(Exception e, string filePath)
        {
            _logger.LogError(e, $"Error deleting file {filePath}");
        }

        protected override void ApplyDocumentAdded(DocumentInfo info, SourceText text)
        {
            var project = this.CurrentSolution.GetProject(info.Id.ProjectId);
            var fullPath = info.FilePath;

            this.OnDocumentAdded(info);

            if (text != null)
            {
                this.SaveDocumentText(info.Id, fullPath, text, text.Encoding ?? Encoding.UTF8);
            }
        }

        private void SaveDocumentText(DocumentId id, string fullPath, SourceText newText, Encoding encoding)
        {
            try
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var writer = new StreamWriter(fullPath, append: false, encoding: encoding))
                {
                    newText.Write(writer);
                }
            }
            catch (IOException e)
            {
                _logger.LogError(e, $"Error saving document {fullPath}");
            }
        }
    }
}
