using System.Threading.Tasks;
using OmniSharp.Mef;

namespace OmniSharp.Endpoint.Exports
{
    class RequestHandlerExportHandler<TRequest, TResponse> : ExportHandler<TRequest, TResponse>
    {
        private readonly IRequestHandler<TRequest, TResponse> _handler;

        public RequestHandlerExportHandler(string language, bool isAuxiliary, IRequestHandler<TRequest, TResponse> handler)
         : base(language, isAuxiliary)
        {
            _handler = handler;
        }

        public override Task<TResponse> Handle(TRequest request)
        {
            return _handler.Handle(request);
        }
    }
}
