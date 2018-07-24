using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Models.FindSymbols;
using OmniSharp.Options;

namespace OmniSharp.Roslyn.CSharp.Services.Navigation
{
    [OmniSharpHandler(OmniSharpEndpoints.FindSymbols, LanguageNames.CSharp)]
    public class FindSymbolsService : IRequestHandler<FindSymbolsRequest, QuickFixResponse>
    {
        private const int MaxSymbolsToReturn = 100;
        private static readonly TimeSpan QueryCodeSearchTimeout = TimeSpan.FromSeconds(5);
        private readonly ILogger _logger;

        private OmniSharpWorkspace _workspace;

        [ImportingConstructor]
        public FindSymbolsService(OmniSharpWorkspace workspace, ILoggerFactory loggerFactory)
        {
            _workspace = workspace;
            _logger = loggerFactory.CreateLogger<FindSymbolsService>();
        }

        public async Task<QuickFixResponse> Handle(FindSymbolsRequest request = null)
        {
            Func<string, bool> isMatch =
                candidate => request != null
                ? candidate.IsValidCompletionFor(request.Filter)
                : true;

            if (HackOptions.Enabled && (string.IsNullOrEmpty(request.Filter) || request.Filter.Length < 2))
            {
                return new QuickFixResponse(new List<QuickFix>());
            }

            Task<List<QuickFix>> queryCodeSearchTask = Task.FromResult(new List<QuickFix>());
            if (HackOptions.Enabled)
            {
                queryCodeSearchTask = _workspace.QueryCodeSearch(request.Filter, 40, QueryCodeSearchTimeout);
            }

            return await FindSymbols(isMatch, queryCodeSearchTask);
        }

        private async Task<QuickFixResponse> FindSymbols(Func<string, bool> predicate, Task<List<QuickFix>> queryCodeSearchTask)
        {
            // Get symbols from Roslyn first and store the list preserving the order
            IEnumerable<ISymbol> symbols = await SymbolFinder.FindSourceDeclarationsAsync(_workspace.CurrentSolution, predicate, SymbolFilter.TypeAndMember);

            var symbolLocations = new List<QuickFix>();
            foreach(var symbol in symbols.Take(MaxSymbolsToReturn))
            {
                // for partial methods, pick the one with body
                var s = symbol;
                if (s is IMethodSymbol method)
                {
                    s = method.PartialImplementationPart ?? symbol;
                }

                foreach (var location in s.Locations)
                {
                    QuickFix converted = ConvertSymbol(symbol, location);
                    symbolLocations.Add(converted);
                }
            }

            HashSet<string> filesKnownToRoslyn = new HashSet<string>(symbolLocations.Select(l => l.FileName), StringComparer.OrdinalIgnoreCase);
            List<QuickFix> locationsFromCodeSearch = await queryCodeSearchTask;
            symbolLocations.AddRange(locationsFromCodeSearch.Where(locationFromCodeSearch => !filesKnownToRoslyn.Contains(locationFromCodeSearch.FileName)));

            _logger.LogDebug($"Found {symbolLocations.Count} symbol(s) in total");
            return new QuickFixResponse(symbolLocations.Distinct());
        }

        private QuickFix ConvertSymbol(ISymbol symbol, Location location)
        {
            var lineSpan = location.GetLineSpan();
            var path = lineSpan.Path;
            var documents = _workspace.GetDocuments(path);

            var format = SymbolDisplayFormat.MinimallyQualifiedFormat;
            format = format.WithMemberOptions(format.MemberOptions
                                              ^ SymbolDisplayMemberOptions.IncludeContainingType
                                              ^ SymbolDisplayMemberOptions.IncludeType);

            format = format.WithKindOptions(SymbolDisplayKindOptions.None);

            return new SymbolLocation
            {
                Text = symbol.ToDisplayString(format),
                Kind = symbol.GetKind(),
                FileName = path,
                Line = lineSpan.StartLinePosition.Line,
                Column = lineSpan.StartLinePosition.Character,
                EndLine = lineSpan.EndLinePosition.Line,
                EndColumn = lineSpan.EndLinePosition.Character,
                Projects = documents.Select(document => document.Project.Name).ToArray()
            };
        }

    }
}
