using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Models.GotoDefinition;
using OmniSharp.Models.Metadata;
using OmniSharp.Options;

namespace OmniSharp.Roslyn.CSharp.Services.Navigation
{
    [OmniSharpHandler(OmniSharpEndpoints.GotoDefinition, LanguageNames.CSharp)]
    public class GotoDefinitionService : IRequestHandler<GotoDefinitionRequest, GotoDefinitionResponse>
    {
        private readonly MetadataHelper _metadataHelper;
        private readonly OmniSharpWorkspace _workspace;
        private readonly ILogger _logger;

        [ImportingConstructor]
        public GotoDefinitionService(OmniSharpWorkspace workspace, MetadataHelper metadataHelper, ILoggerFactory loggerFactory)
        {
            _workspace = workspace;
            _metadataHelper = metadataHelper;
            _logger = loggerFactory.CreateLogger<GotoDefinitionService>();
        }

        public async Task<GotoDefinitionResponse> Handle(GotoDefinitionRequest request)
        {
            var quickFixes = new List<QuickFix>();

            var document = _metadataHelper.FindDocumentInMetadataCache(request.FileName) ??
                _workspace.GetDocument(request.FileName);

            var response = new GotoDefinitionResponse();

            if (HackOptions.Enabled && document == null)
            {
                _logger.LogDebug($"Couldn't get document for {request.FileName}");
                return await GetDefinitionFromCodeSearch(request);
            }

            if (document != null)
            {
                var semanticModel = await document.GetSemanticModelAsync();
                var syntaxTree = semanticModel.SyntaxTree;
                var sourceText = await document.GetTextAsync();
                var position = sourceText.Lines.GetPosition(new LinePosition(request.Line, request.Column));
                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, _workspace);

                if (HackOptions.Enabled && symbol == null)
                {
                    _logger.LogDebug($"Couldn't get symbol [line {request.Line},column {request.Column}] for {request.FileName}");
                    return await GetDefinitionFromCodeSearch(request);
                }

                // go to definition for namespaces is not supported
                if (symbol != null && !(symbol is INamespaceSymbol))
                {
                    // for partial methods, pick the one with body
                    if (symbol is IMethodSymbol method)
                    {
                        // Return an empty response for property accessor symbols like get and set
                        if (method.AssociatedSymbol is IPropertySymbol)
                            return response;

                        symbol = method.PartialImplementationPart ?? symbol;
                    }

                    var location = symbol.Locations.First();

                    if (location.IsInSource)
                    {
                        var lineSpan = symbol.Locations.First().GetMappedLineSpan();
                        response = new GotoDefinitionResponse
                        {
                            FileName = lineSpan.Path,
                            Line = lineSpan.StartLinePosition.Line,
                            Column = lineSpan.StartLinePosition.Character
                        };
                    }
                    else if (location.IsInMetadata && request.WantMetadata)
                    {
                        var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(request.Timeout));
                        var (metadataDocument, _) = await _metadataHelper.GetAndAddDocumentFromMetadata(document.Project, symbol, cancellationSource.Token);
                        if (metadataDocument != null)
                        {
                            cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(request.Timeout));

                            var metadataLocation = await _metadataHelper.GetSymbolLocationFromMetadata(symbol, metadataDocument, cancellationSource.Token);
                            var lineSpan = metadataLocation.GetMappedLineSpan();

                            response = new GotoDefinitionResponse
                            {
                                Line = lineSpan.StartLinePosition.Line,
                                Column = lineSpan.StartLinePosition.Character,
                                MetadataSource = new MetadataSource()
                                {
                                    AssemblyName = symbol.ContainingAssembly.Name,
                                    ProjectName = document.Project.Name,
                                    TypeName = _metadataHelper.GetSymbolName(symbol)
                                },
                            };
                        }
                    }
                }
            }
            return response;
        }

        private async Task<GotoDefinitionResponse> GetDefinitionFromCodeSearch(GotoDefinitionRequest request)
        {
            var response = new GotoDefinitionResponse();
            if (!HackUtils.TryGetSymbolTextForRequest(request, out string symbolText))
            {
                return response;
            }

            List<QuickFix> hits = await _workspace.QueryCodeSearch(symbolText, 1, TimeSpan.FromSeconds(5), false, CodeSearchQueryType.FindDefinitions);
            if (hits.Count != 0)
            {
                response.FileName = hits[0].FileName;
                response.Column = hits[0].Column;
                response.Line = hits[0].Line;
            }
            return response;
        }
    }
}
