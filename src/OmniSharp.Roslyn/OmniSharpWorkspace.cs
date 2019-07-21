using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using OmniSharp.FileWatching;
using Microsoft.VisualStudio.Services.Client;
using Microsoft.VisualStudio.Services.Search.Shared.WebApi.Contracts;
using Microsoft.VisualStudio.Services.Search.WebApi;
using Microsoft.VisualStudio.Services.Search.WebApi.Contracts.Code;
using Microsoft.VisualStudio.Services.WebApi;
using OmniSharp.Models;
using OmniSharp.Models.V2;
using OmniSharp.Options;
using OmniSharp.Roslyn;
using OmniSharp.Roslyn.Utilities;
using OmniSharp.Utilities;
using Microsoft.VisualStudio.Services.Common;

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
        // HACK data
        private string _repoRoot;
        private Uri _repoUri = null;
        private IDictionary<string, IEnumerable<string>> _searchFilters = null;
        public HackOptions HackOptions { get; set; } = new HackOptions();

        public bool Initialized { get; set; }
        public BufferManager BufferManager { get; private set; }

        private readonly ILogger<OmniSharpWorkspace> _logger;

        private readonly ConcurrentBag<Func<string, Task>> _waitForProjectModelReadyHandlers = new ConcurrentBag<Func<string, Task>>();

        private readonly ConcurrentDictionary<string, ProjectInfo> miscDocumentsProjectInfos = new ConcurrentDictionary<string, ProjectInfo>();

        private object _searchClientLock = new object();
        
        private SearchHttpClient _searchClient;

        [ImportingConstructor]
        public OmniSharpWorkspace(HostServicesAggregator aggregator, ILoggerFactory loggerFactory, IFileSystemWatcher fileSystemWatcher)
            : base(aggregator.CreateHostServices(), "Custom")
        {
            BufferManager = new BufferManager(this, fileSystemWatcher);
            _logger = loggerFactory.CreateLogger<OmniSharpWorkspace>();
        }

        public void InitCodeSearch(string repoRoot)
        {
            _repoRoot = repoRoot;
            _searchFilters = GetRepoSearchFilters(_repoRoot, out _repoUri);
            GetSearchClientAsync().FireAndForget(_logger);
        }

        private static readonly Regex CloneUriRegex =
            new Regex(
                @"(?ix)
                  https://(?<vsoaccount>.+)\.(?<vsodomain>visualstudio\.com|azure.com)
                    /(?<collection>[^/]+)
                    /((?<project>.+(?<!/_git))
                    /)?_git
                    /(?<repo>[^/]+)
                    /?",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);


        public static string GetRepoRootOrNull(string targetDirectory)
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
                string vstsUri = ProcessHelper.RunAndCaptureOutput("git", "config --get remote.origin.url", repoRoot);
                if ((!string.IsNullOrEmpty(vstsUri)) && ParseVstsRepoUrl(
                    vstsUri,
                    out vstsAccountUri,
                    out string vstsProjectName,
                    out string vstsRepoName))
                {
                    searchFilters.Add("Project", new[] { vstsProjectName });
                    searchFilters.Add("Repository", new[] { vstsRepoName });
                    searchFilters.Add("Branch", new[] { "master" });
                    _logger.LogDebug($"Initialized VSTS Code Search filters for {vstsAccountUri.AbsoluteUri}, project {vstsProjectName}, repo {vstsRepoName}");
                }
                else
                {
                    _logger.LogWarning($"Couldn't parse VSTS repo URI {vstsUri}");
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
            string collection = (match.Groups["project"].Captures.Count > 0 && match.Groups["collection"].Captures.Count > 0) ? 
                    match.Groups["collection"].Captures[0].Value : string.Empty;
            string projectName = match.Groups["project"].Captures.Count > 0
                ? match.Groups["project"].Captures[0].Value
                : (match.Groups["collection"].Captures.Count > 0 ? match.Groups["collection"].Captures[0].Value : vstsRepoName);

            vstsAccountUri = new Uri($"https://{vsoAccount}.{vsoDomain}/{collection}");
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
                    CodeSearchResponse response = await client.FetchCodeSearchResultsAsync(request, ct);
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

                return GetQuickFixesFromCodeResult(codeResult, filePath, searchType, searchFilter, wildCardSearch);
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

        private static List<QuickFix> GetQuickFixesFromCodeResult(CodeResult codeResult, string filePath, CodeSearchQueryType searchType,
            string searchFilter, bool wildCardSearch)
        {
            var foundSymbols = new List<QuickFix>();
            if (!codeResult.Matches.TryGetValue("content", out IEnumerable<Hit> contentHitsEnum))
            {
                return foundSymbols;
            }

            List<Hit> contentHits = contentHitsEnum.ToList();
            int lineOffsetWin = 0;
            int lineOffsetUnix = 0;
            int lineNumber = 0;

            foreach (string line in File.ReadLines(filePath))
            {
                foreach (Hit hit in contentHits.OrderBy(h => h.CharOffset))
                {
                    // Hit.CharOffset is calculated based on end-of-lines of the file stored by the service which can be different from local file end-of-lines. 
                    // Try guess what end-of-lines were used by the service and use the first match.
                    if (hit.CharOffset >= lineOffsetUnix && hit.CharOffset < lineOffsetUnix + line.Length)
                    {
                        if (ConsiderMatchCandidate(filePath, searchType, searchFilter, wildCardSearch, hit, lineNumber, lineOffsetUnix, line, foundSymbols))
                        {
                            // Keep only a single match per line per hit - it is already clone enough
                            continue;
                        }
                    }

                    if (hit.CharOffset >= lineOffsetWin && hit.CharOffset < lineOffsetWin + line.Length)
                    {
                        ConsiderMatchCandidate(filePath, searchType, searchFilter, wildCardSearch, hit, lineNumber, lineOffsetWin, line, foundSymbols);
                    }
                }

                if (foundSymbols.Count >= contentHits.Count)
                {
                    // Found matching symbols for all hits - can stop searching the file
                    break;
                }

                lineOffsetWin += line.Length + 2;
                lineOffsetUnix += line.Length + 1;
                lineNumber++;
            }

            return foundSymbols;
        }

        private static bool ConsiderMatchCandidate(string filePath, CodeSearchQueryType searchType, string searchFilter, bool wildCardSearch, Hit hit,
            int lineNumber, int lineOffset, string line, List<QuickFix> foundSymbols)
        {
            if (line.Trim().Length >= 2 && line.Trim().Substring(0, 2) == @"//")
            {
                // ignore obvious mismatches like comments
                return false;
            }

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

            // VSTS Code Search is case-insensitive. Require complete case match when looking for references or the definition
            if (!wildCardSearch && !symbolText.Equals(searchFilter, StringComparison.Ordinal))
            {
                return false;
            }

            // Detect the case when local enlistment and VSTS Code Search index is out of sync and skip such hits.
            // Or the case when the end of lines are different b/w what was used when calculating hit offset of the server vs end of lines in the local file
            if (wildCardSearch && symbolText.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            if (searchType == CodeSearchQueryType.FindReferences)
            {
                symbolLocation.Text = line.Trim();
            }
            else
            {
                symbolLocation.Text = symbolText;
            }

            foundSymbols.Add(symbolLocation);
            return true;
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
                    string pat = HackOptions.AuthPat;
                    _logger.LogDebug($"Creating VSTS Code Search Client for {_repoUri.AbsoluteUri} using {(string.IsNullOrWhiteSpace(pat) ? "client creds" : "PAT")}");

                    VssCredentials creds;
                    if (string.IsNullOrWhiteSpace(pat))
                    {
                        creds = new VssClientCredentials();
                    }
                    else
                    {
                        creds = new VssCredentials(null, new VssBasicCredential("PersonalAccessToken", pat), CredentialPromptType.DoNotPrompt);
                    }
                    _logger.LogDebug($"Created creds of type {creds.GetType().FullName}");

                    var connection = new VssConnection(_repoUri, creds);
                    _logger.LogDebug($"Created VssConnection to {_repoUri}");
                    
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

        public void AddWaitForProjectModelReadyHandler(Func<string, Task> handler)
        {
            _waitForProjectModelReadyHandlers.Add(handler);
        }

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
            // if the file has already been added as a misc file,
            // because of a possible race condition between the updates of the project systems,
            // remove the misc file and add the document as required
            TryRemoveMiscellaneousDocument(documentInfo.FilePath);

            OnDocumentAdded(documentInfo);
        }

        public DocumentId TryAddMiscellaneousDocument(string filePath, string language)
        {
            if (GetDocument(filePath) != null)
                return null; //if the workspace already knows about this document then it is not a miscellaneous document

            var projectInfo = miscDocumentsProjectInfos.GetOrAdd(language, (lang) => CreateMiscFilesProject(lang));
            var documentId = AddDocument(projectInfo.Id, filePath);
            _logger.LogInformation($"Miscellaneous file: {filePath} added to workspace");
            return documentId;
        }

        public bool TryRemoveMiscellaneousDocument(string filePath)
        {
            var documentId = GetDocumentId(filePath);
            if (documentId == null || !IsMiscellaneousDocument(documentId))
                return false;

            RemoveDocument(documentId);
            _logger.LogDebug($"Miscellaneous file: {filePath} removed from workspace");
            return true;
        }

        public void TryPromoteMiscellaneousDocumentsToProject(Project project)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            var miscProjectInfos = miscDocumentsProjectInfos.Values.ToArray();
            for (var i = 0; i < miscProjectInfos.Length; i++)
            {
                var miscProject = CurrentSolution.GetProject(miscProjectInfos[i].Id);
                var documents = miscProject.Documents.ToArray();

                for (var j = 0; j < documents.Length; j++)
                {
                    var document = documents[j];
                    if (FileBelongsToProject(document.FilePath, project))
                    {
                        var textLoader = new DelegatingTextLoader(document);
                        var documentId = DocumentId.CreateNewId(project.Id);
                        var documentInfo = DocumentInfo.Create(
                            documentId,
                            document.FilePath,
                            filePath: document.FilePath,
                            loader: textLoader);

                        // This transitively will remove the document from the misc project.
                        AddDocument(documentInfo);
                    }
                }
            }
        }

        public void UpdateDiagnosticOptionsForProject(ProjectId projectId, ImmutableDictionary<string, ReportDiagnostic> rules)
        {
            var project = this.CurrentSolution.GetProject(projectId);
            OnCompilationOptionsChanged(projectId,  project.CompilationOptions.WithSpecificDiagnosticOptions(rules));
        }

        private ProjectInfo CreateMiscFilesProject(string language)
        {
            string assemblyName = Guid.NewGuid().ToString("N");
            var projectInfo = ProjectInfo.Create(
                   id: ProjectId.CreateNewId(),
                   version: VersionStamp.Create(),
                   name: "MiscellaneousFiles.csproj",
                   metadataReferences: DefaultMetadataReferenceHelper.GetDefaultMetadataReferenceLocations()
                                       .Select(loc => MetadataReference.CreateFromFile(loc)),
                   assemblyName: assemblyName,
                   language: language);

            AddProject(projectInfo);
            return projectInfo;
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
            var documentInfo = DocumentInfo.Create(documentId, Path.GetFileName(filePath), filePath: filePath, loader: loader, sourceCodeKind: sourceCodeKind);

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

        public async Task<IEnumerable<Document>> GetDocumentsFromFullProjectModelAsync(string filePath)
        {
            await OnWaitForProjectModelReadyAsync(filePath);
            return GetDocuments(filePath);
        }

        public async Task<Document> GetDocumentFromFullProjectModelAsync(string filePath)
        {
            await OnWaitForProjectModelReadyAsync(filePath);
            return GetDocument(filePath);
        }

        public override bool CanApplyChange(ApplyChangesKind feature)
        {
            return true;
        }

        internal bool FileBelongsToProject(string fileName, Project project)
        {
            if (string.IsNullOrWhiteSpace(project.FilePath) ||
                string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            var fileDirectory = new FileInfo(fileName).Directory;
            var projectPath = project.FilePath;
            var projectDirectory = new FileInfo(projectPath).Directory.FullName;

            while (fileDirectory != null)
            {
                if (string.Equals(fileDirectory.FullName, projectDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                fileDirectory = fileDirectory.Parent;
            }

            return false;
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
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
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

        public bool IsCapableOfSemanticDiagnostics(Document document)
        {
            return !IsMiscellaneousDocument(document.Id);
        }

        private bool IsMiscellaneousDocument(DocumentId documentId)
        {
            return miscDocumentsProjectInfos.Where(p => p.Value.Id == documentId.ProjectId).Any();
        }

        private class DelegatingTextLoader : TextLoader
        {
            private readonly Document _fromDocument;

            public DelegatingTextLoader(Document fromDocument)
            {
                _fromDocument = fromDocument ?? throw new ArgumentNullException(nameof(fromDocument));
            }

            public override async Task<TextAndVersion> LoadTextAndVersionAsync(
                Workspace workspace,
                DocumentId documentId,
                CancellationToken cancellationToken)
            {
                var sourceText = await _fromDocument.GetTextAsync();
                var version = await _fromDocument.GetTextVersionAsync();
                var textAndVersion = TextAndVersion.Create(sourceText, version);

                return textAndVersion;
            }
        }

        private Task OnWaitForProjectModelReadyAsync(string filePath)
        {
            return Task.WhenAll(_waitForProjectModelReadyHandlers.Select(h => h(filePath)));
        }

        public void SetAnalyzerReferences(ProjectId id, ImmutableArray<AnalyzerFileReference> analyzerReferences)
        {
            var project = this.CurrentSolution.GetProject(id);

            var refsToAdd = analyzerReferences.Where(newRef => project.AnalyzerReferences.All(oldRef => oldRef.Display != newRef.Display));
            var refsToRemove = project.AnalyzerReferences.Where(newRef => analyzerReferences.All(oldRef => oldRef.Display != newRef.Display));

            foreach(var toAdd in refsToAdd)
            {
                _logger.LogInformation($"Adding analyzer reference: {toAdd.FullPath}");
                base.OnAnalyzerReferenceAdded(id, toAdd);
            }

            foreach(var toRemove in refsToRemove)
            {
                _logger.LogInformation($"Removing analyzer reference: {toRemove.FullPath}");
                base.OnAnalyzerReferenceRemoved(id, toRemove);
            }
        }

        public void AddAdditionalDocument(ProjectId projectId, string filePath)
        {
            var documentId = DocumentId.CreateNewId(projectId);
            var loader = new OmniSharpTextLoader(filePath);
            var documentInfo = DocumentInfo.Create(documentId, Path.GetFileName(filePath), filePath: filePath, loader: loader);
            OnAdditionalDocumentAdded(documentInfo);
        }

        public void RemoveAdditionalDocument(DocumentId documentId)
        {
            OnAdditionalDocumentRemoved(documentId);
        }
    }
}
