using System.Composition;

namespace OmniSharp.Mef
{
    [MetadataAttribute]
    public class OmniSharpHandlerAttribute : ExportAttribute
    {
        public string Language { get; }

        public bool IsAuxiliary { get; }

        public string EndpointName { get; }

        public OmniSharpHandlerAttribute(string endpoint, string language, bool isAuxiliary = false) : base(typeof(IRequestHandler))
        {
            EndpointName = endpoint;
            Language = language;
            IsAuxiliary = isAuxiliary;
        }
    }
}
