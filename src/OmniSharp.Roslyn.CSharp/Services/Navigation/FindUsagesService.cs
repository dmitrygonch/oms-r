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
            Document document;
            if (!_workspace.HackOptions.Enabled)
            {
                // To produce complete list of usages for symbols in the document wait until all projects are loaded.
                document = await _workspace.GetDocumentFromFullProjectModelAsync(request.FileName);
                if (document == null)
                {
                    return new QuickFixResponse();
                }
            }
            else
            {
                document = _workspace.GetDocument(request.FileName);
            }

            var quickFixes = new List<QuickFix>();
            SourceText sourceText = null;
            int position = 0;
            ISymbol symbol = null;
            if (document != null)
            {
                var semanticModel = await document.GetSemanticModelAsync();
                sourceText = await document.GetTextAsync();
                position = sourceText.Lines.GetPosition(new LinePosition(request.Line, request.Column));
                symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, _workspace);
                var definition = await SymbolFinder.FindSourceDefinitionAsync(symbol, _workspace.CurrentSolution);
                var usages = request.OnlyThisFile
                    ? await SymbolFinder.FindReferencesAsync(definition ?? symbol, _workspace.CurrentSolution, ImmutableHashSet.Create(document))
                    : await SymbolFinder.FindReferencesAsync(definition ?? symbol, _workspace.CurrentSolution);
                var locations = usages.SelectMany(u => u.Locations).Select(l => l.Location).ToList();

                if (!request.ExcludeDefinition)
                {
                    // always skip get/set methods of properties from the list of definition locations.
                    var definitionLocations = usages.Select(u => u.Definition)
                        .Where(def => !(def is IMethodSymbol method && method.AssociatedSymbol is IPropertySymbol))
                        .SelectMany(def => def.Locations)
                        .Where(loc => loc.IsInSource && (!request.OnlyThisFile || loc.SourceTree.FilePath == request.FileName));

                    locations.AddRange(definitionLocations);
                }

                quickFixes = locations.Distinct().Select(l => l.GetQuickFix(_workspace)).ToList();
            }

            List<QuickFix> codeSearchRefs = await QueryCodeSearchForRefs(request, symbol, sourceText, position);
            HashSet<string> roslynRefFiles = new HashSet<string>(quickFixes.Select(f => f.FileName), StringComparer.OrdinalIgnoreCase);
            quickFixes.AddRange(codeSearchRefs.Where(r => !roslynRefFiles.Contains(r.FileName)));

            return new QuickFixResponse(quickFixes.Distinct()
                                            .OrderBy(q => q.FileName)
                                            .ThenBy(q => q.Line)
                                            .ThenBy(q => q.Column));
        }
        
        private Task<List<QuickFix>> QueryCodeSearchForRefs(FindUsagesRequest request, ISymbol symbol,
            SourceText sourceText, int positionInSourceText)
        {
            // OnlyThisFile is supplied for CodeLense requests which won't benefit from VSTS data.
            // If symbol is available then avoid querying VSTS for private symbols because all their references will be already loaded in Roslyn's workspace.
            if (!_workspace.HackOptions.Enabled || request.OnlyThisFile || (symbol != null && symbol.DeclaredAccessibility == Accessibility.Private))
            {
                return Task.FromResult(new List<QuickFix>());
            }

            HackUtils.TryGetSymbolTextForRequest(request, out string symbolText);

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
