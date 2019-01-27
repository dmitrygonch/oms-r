using System.Threading.Tasks;

namespace OmniSharp.Endpoint.Exports
{
    abstract class ExportHandler<TRequest, TResponse>
    {
        protected ExportHandler(string language, bool isAuxiliary)
        {
            Language = language;
            IsAuxiliary = isAuxiliary;
        }

        public string Language { get; }

        public bool IsAuxiliary { get; }
        
        public abstract Task<TResponse> Handle(TRequest request);
    }
}
