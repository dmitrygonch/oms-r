using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Services.Client;
using Microsoft.VisualStudio.Services.Search.WebApi;
using Microsoft.VisualStudio.Services.Search.WebApi.Contracts.Code;
using Microsoft.VisualStudio.Services.WebApi;
using OmniSharp.Models;
using OmniSharp.Options;
using OmniSharp.Roslyn;
using OmniSharp.Utilities;

namespace OmniSharp
{
    [Export, Shared]
    public class OmniSharpWorkspace : Workspace
    {
        public ConcurrentQueue<string> ProjectFilesToLoad { get; } = new ConcurrentQueue<string>();

        // HACK data
        private string _repoRoot;
        private string _repoUriString;
        private string _repoName;
        private string _projectName;
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

        public void InitCodeSearch()
        {
            // Fill in HACK data
            _projectName = Environment.GetEnvironmentVariable("HACK_PROJECT_NAME");
            _repoName = Environment.GetEnvironmentVariable("HACK_REPO_NAME");
            _repoRoot = Environment.GetEnvironmentVariable("HACK_REPO_ROOT");
            _repoUriString = Environment.GetEnvironmentVariable("HACK_REPO_URI");

            _searchFilters = new Dictionary<string, IEnumerable<string>>
            {
                ["Project"] = new[] { _projectName },
                ["Repository"] = new[] { _repoName },
                ["Path"] = new[] { "" },
                ["Branch"] = new[] { "master" },
            };

            GetSearchClientAsync().FireAndForget(_logger);
        }

        public async Task<List<QuickFix>> QueryCodeSearch(string filter, int maxResults, TimeSpan maxDuration)
        {
            List<QuickFix> result = null;
            try
            {
                CodeSearchRequest request = new CodeSearchRequest
                {
                    SearchText = $"def:{filter}*",
                    Skip = 0,
                    Top = maxResults,
                    Filters = _searchFilters,
                    IncludeFacets = false
                };

                _logger.LogDebug($"Querying VSTS Code Search with filter '{filter}'");
                SearchHttpClient client = await GetSearchClientAsync();
                if (client == null)
                {
                    return new List<QuickFix>();
                }

                using (var ct = new CancellationTokenSource(maxDuration))
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    CodeSearchResponse response = await client.FetchAdvancedCodeSearchResultsAsync(request, ct);
                    _logger.LogDebug($"Response from VSTS Code Search for filter '{filter}' completed in {sw.Elapsed.TotalSeconds} seconds and contains {response.Results.Count()} results");

                    if (response != null)
                    {
                        result = response.Results.Select(r => new QuickFix()
                        {
                            Text = Path.GetFileNameWithoutExtension(r.Filename),
                            FileName = Path.Combine(_repoRoot, r.Path.TrimStart('/').Replace('/', '\\')),
                            Line = 0,
                            Column = 0,
                            EndLine = 0,
                            EndColumn = 0,
                            Projects = new List<string>()
                        }).ToList();
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to query VSTS Code Search for filter '{filter}'");
            }

            return result ?? new List<QuickFix>();
        }

        private async Task<SearchHttpClient> GetSearchClientAsync()
        {
            SearchHttpClient searchClient = null;
            if (_searchClient == null)
            {
                try
                {
                    _logger.LogDebug($"Creating Search Client for {_repoUriString}");
                    Uri uri = new Uri(_repoUriString);
                    var connection = new VssConnection(uri, new VssClientCredentials());
                    searchClient = await connection.GetClientAsync<SearchHttpClient>();
                    _logger.LogDebug($"Successfully created Search Client for {_repoUriString}");
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Failed to create Search Client for {_repoUriString}");
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

        public void AddProjectReference(ProjectId projectId, ProjectReference projectReference)
        {
            OnProjectReferenceAdded(projectId, projectReference);
        }

        public void RemoveProjectReference(ProjectId projectId, ProjectReference projectReference)
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
