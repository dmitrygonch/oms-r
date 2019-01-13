using System.Collections.Generic;

namespace OmniSharp.CodeSearch.Proxy.Shared
{
    public class SearchResult
    {
        //
        // Summary:
        //     Path at which result file is present.
        public string Path { get; set; }
        //
        // Summary:
        //     Dictionary of field to hit offsets in the result file.
        public IList<SearchMatch> ContentMatches { get; set; }
    }
}