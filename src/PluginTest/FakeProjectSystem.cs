using System;
using System.Collections.Generic;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OmniSharp;
using OmniSharp.Mef;
using OmniSharp.Models.WorkspaceInformation;
using OmniSharp.Services;

namespace PluginTest
{
    [ExportProjectSystem("FakePS"), Shared]
    public class FakeProjectSystem : IProjectSystem
    {
        public string Key { get; } = "Fake";
        public string Language { get; } = "Fake";
        public IEnumerable<string> Extensions { get; } = Array.Empty<string>();
        public bool EnabledByDefault { get; } = true;
        public bool Initialized { get; private set;  } = false;

        private readonly object _wsModel = new object();
        private readonly object _projModel = new object();

        [ImportingConstructor]
        public FakeProjectSystem(
            IOmniSharpEnvironment environment,
            OmniSharpWorkspace workspace,
            ILoggerFactory loggerFactory)
        {

        }

        public Task<object> GetWorkspaceModelAsync(WorkspaceInformationRequest request)
        {
            return Task.FromResult(_wsModel);
        }

        public Task<object> GetProjectModelAsync(string filePath)
        {
            return Task.FromResult(_projModel);
        }

        public void Initalize(IConfiguration configuration)
        {
            Initialized = true;
        }
    }
}
