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
            string symbolText = null;
            if (_workspace.HackOptions.Enabled)
            {
                HackUtils.TryGetSymbolTextForRequest(request, out symbolText);
            }

            var document = _workspace.GetDocument(request.FileName);
            var response = new QuickFixResponse();
            if (document != null)
            {
                var semanticModel = await document.GetSemanticModelAsync();
                var sourceText = await document.GetTextAsync();
                var position = sourceText.Lines.GetPosition(new LinePosition(request.Line, request.Column));
                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, _workspace);

                List<QuickFix> quickFixes = await QueryRoslynForRefs(request, document, symbol);

                List<QuickFix> codeSearchRefs = await QueryCodeSearchForRefs(request, symbolText, symbol, sourceText, position);
                HashSet<string> roslynRefFiles = new HashSet<string>(quickFixes.Select(f => f.FileName), StringComparer.OrdinalIgnoreCase);
                quickFixes.AddRange(codeSearchRefs.Where(r => !roslynRefFiles.Contains(r.FileName)));

                response = new QuickFixResponse(quickFixes.Distinct()
                                                .OrderBy(q => q.FileName)
                                                .ThenBy(q => q.Line)
                                                .ThenBy(q => q.Column));
            }
            // Try to get symbol text from the file - this isn't very presize version as file might be modified in memory
            else if (_workspace.HackOptions.Enabled)
            {
                List<QuickFix> quickFixes = await QueryCodeSearchForRefs(request, symbolText, null, null, 0);
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

        private Task<List<QuickFix>> QueryCodeSearchForRefs(FindUsagesRequest request, string symbolText, ISymbol symbol,
            SourceText sourceText, int positionInSourceText)
        {
            if (!_workspace.HackOptions.Enabled || request.ExcludeDefinition || request.OnlyThisFile)
            {
                return Task.FromResult(new List<QuickFix>());
            }

            // Try to get symbol text from Roslyn's symbol object - this is the most presize option
            if (symbol != null && symbol.Locations != null && symbol.Locations.Any())
            {
                Location location = symbol.Locations.First();
                symbolText = location.SourceTree.GetText().ToString(location.SourceSpan);
            }
            // Try to get symbol text from Roslyn's in-memory text which might be different from what is stored on disk
            else if (sourceText != null)
            {
                int symboldStartPosition = positionInSourceText;
                do
                {
                    symboldStartPosition--;
                } while (symboldStartPosition > 0 && char.IsLetter(sourceText[symboldStartPosition]));
                symboldStartPosition++;

                int symbolEndPosition = positionInSourceText;
                while (symbolEndPosition < sourceText.Length && char.IsLetterOrDigit(sourceText[symbolEndPosition]))
                {
                    symbolEndPosition++;
                }

                symbolText = sourceText.ToString(new TextSpan(symboldStartPosition, symbolEndPosition - symboldStartPosition));
            }

            if (string.IsNullOrWhiteSpace(symbolText) || !char.IsLetter(symbolText.ElementAt(0)))
            {
                return Task.FromResult(new List<QuickFix>());
            }

            return _workspace.QueryCodeSearch(symbolText, 50, TimeSpan.FromSeconds(3), false, CodeSearchQueryType.FindReferences);
        }
    }
}
