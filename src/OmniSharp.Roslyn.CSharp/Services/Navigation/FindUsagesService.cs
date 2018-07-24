using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using OmniSharp.Helpers;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Models.FindUsages;
using OmniSharp.Options;

namespace OmniSharp.Roslyn.CSharp.Services.Navigation
{
    [OmniSharpHandler(OmniSharpEndpoints.FindUsages, LanguageNames.CSharp)]
    public class FindUsagesService : IRequestHandler<FindUsagesRequest, QuickFixResponse>
    {
        private readonly OmniSharpWorkspace _workspace;

        [ImportingConstructor]
        public FindUsagesService(OmniSharpWorkspace workspace)
        {
            _workspace = workspace;
        }

        public async Task<QuickFixResponse> Handle(FindUsagesRequest request)
        {
            var document = _workspace.GetDocument(request.FileName);
            var response = new QuickFixResponse();
            if (document != null)
            {
                var semanticModel = await document.GetSemanticModelAsync();
                var sourceText = await document.GetTextAsync();
                var position = sourceText.Lines.GetPosition(new LinePosition(request.Line, request.Column));
                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, _workspace);

                List<QuickFix> quickFixes = await QueryRoslynForRefs(request, document, symbol);

                List<QuickFix> codeSearchRefs = await QueryCodeSearchForRefs(request, symbol);
                HashSet<string> roslynRefFiles = new HashSet<string>(quickFixes.Select(f => f.FileName), StringComparer.OrdinalIgnoreCase);
                quickFixes.AddRange(codeSearchRefs.Where(r => !roslynRefFiles.Contains(r.FileName)));

                response = new QuickFixResponse(quickFixes.Distinct()
                                                .OrderBy(q => q.FileName)
                                                .ThenBy(q => q.Line)
                                                .ThenBy(q => q.Column));
            }

            return response;
        }

        private async Task<List<QuickFix>> QueryRoslynForRefs(FindUsagesRequest request, Document document, ISymbol symbol)
        {
            var definition = await SymbolFinder.FindSourceDefinitionAsync(symbol, _workspace.CurrentSolution);
            var usages = request.OnlyThisFile
                ? await SymbolFinder.FindReferencesAsync(definition ?? symbol, _workspace.CurrentSolution, ImmutableHashSet.Create(document))
                : await SymbolFinder.FindReferencesAsync(definition ?? symbol, _workspace.CurrentSolution);

            var locations = new List<Location>();
            foreach (var usage in usages.Where(u => u.Definition.CanBeReferencedByName || (symbol as IMethodSymbol)?.MethodKind == MethodKind.Constructor))
            {
                foreach (var location in usage.Locations)
                {
                    locations.Add(location.Location);
                }

                if (!request.ExcludeDefinition)
                {
                    var definitionLocations = usage.Definition.Locations
                        .Where(loc => loc.IsInSource && (!request.OnlyThisFile || loc.SourceTree.FilePath == request.FileName));

                    foreach (var location in definitionLocations)
                    {
                        locations.Add(location);
                    }
                }
            }

            List<QuickFix> quickFixes = locations.Distinct().Select(l => l.GetQuickFix(_workspace)).ToList();
            return quickFixes;
        }

        private async Task<List<QuickFix>> QueryCodeSearchForRefs(FindUsagesRequest request, ISymbol symbol)
        {
            Task<List<QuickFix>> queryCodeSearchTask = Task.FromResult(new List<QuickFix>());
            if (HackOptions.Enabled && request != null && !request.ExcludeDefinition && symbol != null && symbol.Locations != null && symbol.Locations.Any())
            {
                Location location = symbol.Locations.First();
                string symbolText = location.SourceTree.GetText().ToString(location.SourceSpan);
                queryCodeSearchTask = _workspace.QueryCodeSearch(symbolText, 50, TimeSpan.FromSeconds(3), false, CodeSearchQueryType.FindReferences);
            }

            return await queryCodeSearchTask;
        }
    }
}
