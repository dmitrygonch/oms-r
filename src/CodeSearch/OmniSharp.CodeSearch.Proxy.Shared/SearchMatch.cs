namespace OmniSharp.CodeSearch.Proxy.Shared
{
    //
    // Summary:
    //     Describes the position of a piece of text in a document.
    public class SearchMatch
    {
        //
        // Summary:
        //     Gets or sets the start character offset of a piece of text.
        public int CharOffset { get; set; }
        //
        // Summary:
        //     Gets or sets the length of a piece of text.
        public int Length { get; set; }
    }
}