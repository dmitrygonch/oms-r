using System;
using System.Text.RegularExpressions;

namespace OmniSharp.CodeSearch.Proxy.Shared
{
    public static class GitRepoUtilities
    {
        private static readonly Regex CloneUriRegex =
            new Regex(
                @"(?ix)
                  https://(?<account>.+)\.(?<domain>visualstudio\.com|azure.com)
                    /(?<collection>[^/]+)
                    /((?<project>.+(?<!/_git))
                    /)?_git
                    /(?<repo>[^/]+)
                    /?",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool TryParseRepoUri(string repoUri, out Uri accountUri, out string repoName, out string projectName)
        {
            accountUri = null;
            repoName = null;
            projectName = null;

            Match match = CloneUriRegex.Match(repoUri);
            if (!match.Success)
            {
                Console.WriteLine($"Repo {repoUri} is not hosted by Azure DevOps services");
                return false;
            }

            string account = match.Groups["account"].Captures[0].Value;
            string domain = match.Groups["domain"].Captures[0].Value;
            repoName = match.Groups["repo"].Captures[0].Value;
            string collection = (match.Groups["project"].Captures.Count > 0 && match.Groups["collection"].Captures.Count > 0) ?
                    match.Groups["collection"].Captures[0].Value : string.Empty;
            projectName = match.Groups["project"].Captures.Count > 0
                ? match.Groups["project"].Captures[0].Value
                : (match.Groups["collection"].Captures.Count > 0 ? match.Groups["collection"].Captures[0].Value : repoName);

            accountUri = new Uri($"https://{account}.{domain}/{collection}");
            return true;
        }
    }
}
