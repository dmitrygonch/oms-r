using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using OmniSharp.Extensions;
using Microsoft.Extensions.Logging;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Models.FindSymbols;
using OmniSharp.Options;

namespace OmniSharp.Roslyn.CSharp.Services.Navigation
{
    [OmniSharpHandler(OmniSharpEndpoints.FindSymbols, LanguageNames.CSharp)]
    public class FindSymbolsService : IRequestHandler<FindSymbolsRequest, QuickFixResponse>
    {
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
            if (request?.Filter?.Length < request?.MinFilterLength.GetValueOrDefault())
            {
                return new QuickFixResponse { QuickFixes = Array.Empty<QuickFix>() };
            }

            if (HackOptions.Enabled && string.IsNullOrEmpty(request.Filter))
            {
                return new QuickFixResponse { QuickFixes = Array.Empty<QuickFix>() };
            }

            var queryCodeSearchTask = Task.FromResult(new List<QuickFix>());
            if (HackOptions.Enabled)
            {
                queryCodeSearchTask = _workspace.QueryCodeSearch(request.Filter, 50, QueryCodeSearchTimeout, true, CodeSearchQueryType.FindDefinitions);
            }

            int maxItemsToReturn = (request?.MaxItemsToReturn).GetValueOrDefault();
            var csprojSymbols = await _workspace.CurrentSolution.FindSymbols(request?.Filter, ".csproj", maxItemsToReturn);
            var projectJsonSymbols = await _workspace.CurrentSolution.FindSymbols(request?.Filter, ".json", maxItemsToReturn);
            var csxSymbols = await _workspace.CurrentSolution.FindSymbols(request?.Filter, ".csx", maxItemsToReturn);

            var roslynSymbols = csprojSymbols.QuickFixes.Concat(projectJsonSymbols.QuickFixes).Concat(csxSymbols.QuickFixes).ToList();

            HashSet<string> filesKnownToRoslyn = new HashSet<string>(roslynSymbols.Select(l => l.FileName), StringComparer.OrdinalIgnoreCase);
            List<QuickFix> locationsFromCodeSearch = await queryCodeSearchTask;
            roslynSymbols.AddRange(locationsFromCodeSearch.Where(locationFromCodeSearch => !filesKnownToRoslyn.Contains(locationFromCodeSearch.FileName)));

            return new QuickFixResponse()
            {
                QuickFixes = roslynSymbols.Distinct()
            };
        }
    }
}
