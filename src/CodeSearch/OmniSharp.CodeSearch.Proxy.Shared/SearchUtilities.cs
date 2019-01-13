using System;
using System.Collections.Generic;

namespace OmniSharp.CodeSearch.Proxy.Shared
{
    public static class SearchUtilities
    {
        public static IDictionary<string, IEnumerable<string>> GetRequestFilter(string projectName, string repoName)
        {
            var filters = new Dictionary<string, IEnumerable<string>>
            {
                ["Project"] = new[] { projectName },
                ["Repository"] = new[] { repoName },
                ["Branch"] = new[] { "master" },
            };

            return filters;
        }

        public static void PrintResults(IList<SearchResult> results)
        {
            foreach (SearchResult result in results)
            {
                Console.WriteLine($"{result.Path} : {result.ContentMatches.Count}");
            }
        }
    }
}