
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Search.Shared.WebApi.Contracts;
using Microsoft.VisualStudio.Services.Search.WebApi;
using Microsoft.VisualStudio.Services.Search.WebApi.Contracts.Code;
using Microsoft.VisualStudio.Services.WebApi;
using OmniSharp.CodeSearch.Proxy.Shared;

namespace OmniSharp.CodeSearch.Proxy
{
    // UNDONE: this is an example of accessing Code Search service using Personal Access Token. Works on all OSs.
    public class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 3 )
            {
                Console.WriteLine($"Usage: OmniSharp.CodeSearch.Proxy <git repo clone URL> <search term> <personal access token>");
                return;
            }

            string repoUri = args[0];
            string queryTerm = args[1];
            string personalAccessToken = args[2];

            if (!GitRepoUtilities.TryParseRepoUri(repoUri, out Uri accountUri, out string repoName, out string projectName))
            {
                return;
            }

            var creds = new VssCredentials(null, new VssBasicCredential("PersonalAccessToken", personalAccessToken), CredentialPromptType.DoNotPrompt);
            Console.WriteLine($"Created credentials using personal access token");

            var connection = new VssConnection(accountUri, creds);
            Console.WriteLine($"Created a connection to {accountUri}");
            
            SearchHttpClient client = connection.GetClientAsync<SearchHttpClient>().GetAwaiter().GetResult();
            Console.WriteLine($"Successfully created Code Search Client for {accountUri}");

            var filters = SearchUtilities.GetRequestFilter(projectName, repoName);

            CodeSearchRequest request = new CodeSearchRequest
            {
                SearchText = $"ext:cs def:{queryTerm}*",
                Skip = 0,
                Top = 10,
                Filters = filters,
                IncludeFacets = false
            };

            Console.WriteLine($"Querying Code Search for max {request.Top} result with filter '{request.SearchText}'");
            Stopwatch sw = Stopwatch.StartNew();
            CodeSearchResponse response = client.FetchCodeSearchResultsAsync(request).GetAwaiter().GetResult();
            Console.WriteLine($"Response from Code Search for filter '{request.SearchText}' completed in {sw.Elapsed.TotalSeconds} seconds and contains {response.Results.Count()} result(s)");

            SearchUtilities.PrintResults(ToSearchResults(response));
        }

        private static IList<SearchResult> ToSearchResults(CodeSearchResponse response)
        {
            var results = new List<SearchResult>();
            foreach (CodeResult codeResult in response.Results)
            {
                if (codeResult.Matches.TryGetValue("content", out IEnumerable<Hit> contentMatches))
                {
                    var result = new SearchResult {Path = codeResult.Path};
                    result.ContentMatches = contentMatches.Select(m => new SearchMatch {CharOffset = m.CharOffset, Length = m.Length}).ToList();
                    results.Add(result);
                    continue;
                }
            }

            return results;
        }
    }
}
