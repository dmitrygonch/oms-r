using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using OmniSharp.Abstractions.Services;
using OmniSharp.Eventing;
using OmniSharp.Models.MembersTree;
using OmniSharp.MSBuild;
using OmniSharp.Services;
using OmniSharp.Utilities;
using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace OmniSharp.DotNetTest.Services
{
    [Export(typeof(ITestAdapterServer)), Shared]
    internal class TestAdapterServer : ITestAdapterServer
    {
        private readonly OmniSharpWorkspace _workspace;
        private readonly IDotNetCliService _dotNetCli;
        private readonly IEventEmitter _eventEmitter;
        private readonly ILoggerFactory _loggerFactory;
        private readonly List<ISyntaxFeaturesDiscover> _testDiscovers;

        private readonly ILogger _logger;
        private Socket _socket;
        private NetworkStream _stream;
        private BinaryReader _reader;
        private BinaryWriter _writer;

        [ImportingConstructor]
        public TestAdapterServer(OmniSharpWorkspace workspace, IDotNetCliService dotNetCli, IEventEmitter eventEmitter, ILoggerFactory loggerFactory,
            [ImportMany] IEnumerable<ISyntaxFeaturesDiscover> featureDiscovers)
        {
            _workspace = workspace;
            _dotNetCli = dotNetCli;
            _eventEmitter = eventEmitter;
            _loggerFactory = loggerFactory;

            _logger = loggerFactory.CreateLogger<TestAdapterServer>();

            _testDiscovers = featureDiscovers.Where(d => typeof(TestFeaturesDiscover).IsAssignableFrom(d.GetType())).ToList();
        }

        public void Run()
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 12345));
            listener.Listen(1);

            _socket = listener.Accept();
            _stream = new NetworkStream(_socket);
            _reader = new BinaryReader(_stream);
            _writer = new BinaryWriter(_stream);

            Task.Run(HandleRpcMEssages).FireAndForget(_logger);
        }

        private Task HandleRpcMEssages()
        {
            while (true)
            {
                try
                {
                    Message request = ReadMessage();
                    switch (request.MessageType)
                    {
                        case "enumtests":

                            TestInfo[] infos = DiscoverAllLoadedTests();
                            SendMessage("enumtests", infos);

                            break;
                        default:
                            _logger.LogWarning($"Unknown message type '{request.MessageType}'");
                            break;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Exception while handling RPC messages");
                }
            }
        }

        private Message ReadMessage()
        {
            var rawMessage = _reader.ReadString();
            _logger.LogDebug($"read: {rawMessage}");

            return JsonDataSerializer.Instance.DeserializeMessage(rawMessage);
        }

        private void SendMessage<T>(string messageType, T payload)
        {
            var rawMessage = JsonDataSerializer.Instance.SerializePayload(messageType, payload);
            _logger.LogDebug($"send: {rawMessage}");

            _writer.Write(rawMessage);
        }

        private TestInfo[] DiscoverAllLoadedTests()
        {
            var testMethods = new List<FileMemberElement>();

            foreach (Project project in _workspace.CurrentSolution.Projects)
            {
                StructureComputer.Compute(project.Documents, _testDiscovers, element =>
                {
                    if (element.Features.Count > 0)
                    {
                        testMethods.Add(element);
                    }
                }
                ).GetAwaiter().GetResult();
            }

            return testMethods.Select(m => new TestInfo
            {
                Id = m.Features.Single().Data,
                Label = m.Location.Text,
                File = m.Location.FileName,
                Project = m.Projects.FirstOrDefault(),
                Line = m.Location.Line,
            }).ToArray();
        }
    }

    public class TestInfo
    {
        public string Id { get; set; } // full type name
        public string Label { get; set; }
        public string File { get; set; } // full path
        public string Project { get; set; } 
        public int Line { get; set; }
    }

}
