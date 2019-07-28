
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.VisualStudio.Services.Client;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Search.Shared.WebApi.Contracts;
using Microsoft.VisualStudio.Services.Search.WebApi;
using Microsoft.VisualStudio.Services.Search.WebApi.Contracts.Code;
using Microsoft.VisualStudio.Services.WebApi;
using OmniSharp.CodeSearch.Proxy.Shared;

namespace OmniSharp.CodeSearch.Proxy.Interactive
{
    // UNDONE: this is an example of accessing Code Search service using interactive authentication. Works on Windows only..
    public class Program
    {
        // Use ADAL auth instead of interactive client library (VssClientCredentials) since the former seems to be working in more scenarios comparting to the latter. Links with details:
        //  - https://docs.microsoft.com/en-us/azure/devops/integrate/get-started/authentication/authentication-guidance?view=azure-devops
        //  - https://github.com/microsoft/azure-devops-auth-samples/blob/master/ManagedClientConsoleAppSample/Program.cs
        public const string VstsAppId = "499b84ac-1321-427f-aa17-267ca6975798";
        public const string VstsAadClient = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1";
        public static readonly Uri AuthRedirectUrl = new Uri("urn:ietf:wg:oauth:2.0:oob");

        private static async Task<VssCredentials> GetVssCredsAsync()
        {
            AuthenticationContext ctx = new AuthenticationContext("https://login.windows.net/common");
            AuthenticationResult authResult = null;
            try
            {
                // First try to aquire auth token silently.
                authResult = await ctx.AcquireTokenAsync(
                    VstsAppId,
                    VstsAadClient,
                    AuthRedirectUrl,
                    new PlatformParameters(PromptBehavior.Never));
            }
            catch (AdalException e)
            {
                // If that failed then pop up dialog to authenticate.
                Console.Write($"Failed to get auth token silently: {e.Message}");
                authResult = await ctx.AcquireTokenAsync(
                    VstsAppId,
                    VstsAadClient,
                    AuthRedirectUrl,
                    new PlatformParameters(PromptBehavior.Auto));
            }

            return new VssAadCredential(new VssAadToken("Bearer", authResult.AccessToken));
        }

        static void Main(string[] args)
        {
            if (args.Length != 2 )
            {
                Console.WriteLine($"Usage: OmniSharp.CodeSearch.Proxy <git repo clone URL> <search term>");
                return;
            }

            string repoUri = args[0];
            string queryTerm = args[1];

            if (!GitRepoUtilities.TryParseRepoUri(repoUri, out Uri accountUri, out string repoName, out string projectName))
            {
                return;
            }

            VssCredentials creds = GetVssCredsAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Created client credentials");

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
