using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using OmniSharp.Abstractions.Services;
using OmniSharp.DotNetTest.Models;
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
using System.Text;
using System.Threading.Tasks;

namespace OmniSharp.DotNetTest.Services
{
    internal interface ITestEventsSubscriber
    {
        void OnStarting(IEnumerable<string> testNames);
        void OnUpdate(IEnumerable<TestResult> testResults);
    }

    internal class NoopTestEventsSubscriber : ITestEventsSubscriber
    {
        public void OnStarting(IEnumerable<string> testNames)
        {
        }

        public void OnUpdate(IEnumerable<TestResult> testResults)
        {
        }
    }

    [Export(typeof(ITestAdapterServer)), Shared]
    [Export(typeof(ITestEventsSubscriber))]
    internal class TestAdapterServer : ITestAdapterServer, ITestEventsSubscriber
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
            Task.Run(HandleRpcMEssages).FireAndForget(_logger);
        }

        private Task HandleRpcMEssages()
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 12345));
            listener.Listen(1);

            _socket = listener.Accept();
            _stream = new NetworkStream(_socket);
            _reader = new BinaryReader(_stream);
            _writer = new BinaryWriter(_stream);

            while (true)
            {
                try
                {
                    Message request = ReadMessage();
                    switch (request.MessageType)
                    {
                        case "enumtests":
                            {
                                TestInfo[] infos = DiscoverAllLoadedTests();
                                SendMessage("enumtests", infos);
                                break;
                            }

                        case "runtests":
                            {
                                TestInfo[] infos = JsonDataSerializer.Instance.DeserializePayload<TestInfo[]>(request);
                                RunTests(infos);
                                break;
                            }

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

        private const string EndOfString = "<EOF>";

        private Message ReadMessage()
        {
            StringBuilder sb = new StringBuilder();
            while (sb.Length < EndOfString.Length || sb.ToString().Substring(sb.Length - EndOfString.Length, EndOfString.Length) != EndOfString)
            {
                sb.Append(_reader.ReadChar());
            }

            string rawMessage = sb.ToString().Substring(0, sb.Length - EndOfString.Length);
            _logger.LogDebug($"read: {rawMessage}");

            return JsonDataSerializer.Instance.DeserializeMessage(rawMessage);
        }

        private void SendMessage<T>(string messageType, T payload)
        {
            var rawMessage = JsonDataSerializer.Instance.SerializePayload(messageType, payload);
            _logger.LogDebug($"send: {rawMessage}");

            byte[] bytes = Encoding.ASCII.GetBytes(rawMessage + EndOfString);
            _writer.Write(bytes);
        }

        private TestInfo[] DiscoverAllLoadedTests()
        {
            var testMethods = new List<Tuple<FileMemberElement, string>>();

            foreach (Project project in _workspace.CurrentSolution.Projects)
            {
                StructureComputer.Compute(project.Documents, _testDiscovers, (element, projectName) =>
                {
                    if (element.Features.Count > 0)
                    {
                        testMethods.Add(new Tuple<FileMemberElement, string>(element, projectName));
                    }
                }
                ).GetAwaiter().GetResult();
            }

            return testMethods.Select(m => new TestInfo
            {
                Id = m.Item1.Features.Single().Data,
                Label = m.Item1.Location.Text,
                File = m.Item1.Location.FileName,
                Project = m.Item2,
                Line = m.Item1.Location.Line,
            }).ToArray();
        }
        private void RunTests(TestInfo[] infos)
        {
            IEnumerable<IGrouping<string, TestInfo>> groups = infos.GroupBy(i => i.Project, StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, TestInfo> group in groups)
            {
                using (var testManager = CreateTestManager(group.First().File))
                {
                    RunTestResponse fullResults = testManager.RunTest(group.Select(i => i.Id).ToArray(), "mstest", ".NETFramework,Version=v4.7.2");
                    RunTestResult[] results = fullResults.Results.Select(r =>
                    new RunTestResult
                    {
                        Id = r.MethodName,
                        Outcome = r.Outcome,
                        ErrorMessage = r.ErrorMessage,
                        ErrorStackTrace = r.ErrorStackTrace

                    }).ToArray();
                    SendMessage("runtests", results);
                }
            }
        }

        TestManager CreateTestManager(string fileName)
        {
            Document document = _workspace.GetDocument(fileName);
            return TestManager.Start(document.Project, _dotNetCli, _eventEmitter, _loggerFactory, new ITestEventsSubscriber[] { new NoopTestEventsSubscriber() });
        }

        public void OnStarting(IEnumerable<string> testNames)
        {
            RunTestResult[] results = testNames.Select(n =>
            new RunTestResult
            {
                Id = n,
                Outcome = "running"

            }).ToArray();
            SendMessage("runtests", results);
        }

        public void OnUpdate(IEnumerable<TestResult> testResults)
        {
            RunTestResult[] results = testResults.Select(r =>
            new RunTestResult
            {
                Id = r.TestCase.FullyQualifiedName,
                Outcome = r.Outcome.ToString().ToLowerInvariant(),
                ErrorMessage = r.ErrorMessage,
                ErrorStackTrace = r.ErrorStackTrace

            }).ToArray();
            SendMessage("runtests", results);
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

    public class RunTestResult
    {
        public string Id { get; set; } // full type name
        public string Outcome { get; set; }
        public string ErrorMessage { get; set; }
        public string ErrorStackTrace { get; set; }
    }

}
