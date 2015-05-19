using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Web.Http;

using Microsoft.Owin.Hosting;

using Owin;

using Rachis.Commands;
using Rachis.Storage;
using Rachis.Transport;

using Voron;

using Xunit;

namespace Rachis.Tests.Http
{
	public abstract class HttpRaftTestsBase : IDisposable
	{
		private const int PortRangeStart = 9000;

		private static int numberOfPortRequests;

		protected readonly List<RaftNode> AllNodes = new List<RaftNode>();

		public static IEnumerable<object[]> Nodes
		{
			get
			{
				return new[]
				{
					new object[] { 1 },
					new object[] { 3 },
					new object[] { 5 },
					new object[] { 7 },
					new object[] { 11 }
				};
			}
		}

		protected RaftNode CreateNetworkAndGetLeader(int numberOfNodes)
		{
			var nodes = Enumerable
				.Range(0, numberOfNodes)
				.Select(i => new RaftNode(GetPort()))
				.ToList();

			AllNodes.AddRange(nodes);

			var allNodesFinishedJoining = new ManualResetEventSlim();

			var random = new Random();
			var leader = nodes[random.Next(0, numberOfNodes - 1)];

			Console.WriteLine("Leader: " + leader.RaftEngine.Options.SelfConnection.Uri);

			InitializeTopology(leader);

			Assert.True(leader.RaftEngine.WaitForLeader());
			Assert.Equal(RaftEngineState.Leader, leader.RaftEngine.State);

			leader.RaftEngine.TopologyChanged += command =>
			{
				if (command.Requested.AllNodeNames.All(command.Requested.IsVoter))
				{
					allNodesFinishedJoining.Set();
				}
			};

			for (var i = 0; i < numberOfNodes; i++)
			{
				var n = nodes[i];

				if (n == leader)
					continue;

				Assert.Equal(RaftEngineState.Leader, leader.RaftEngine.State);
				Assert.True(leader.RaftEngine.AddToClusterAsync(new NodeConnectionInfo
				{
					Name = n.Name,
					Uri = new Uri(n.Url)
				}).Wait(3000));
			}

			if (numberOfNodes == 1)
				allNodesFinishedJoining.Set();

			Assert.True(allNodesFinishedJoining.Wait(10000 * numberOfNodes), "Not all nodes become voters. " + leader.RaftEngine.CurrentTopology);
			Assert.True(leader.RaftEngine.WaitForLeader());

			return leader;
		}

		private static void InitializeTopology(RaftNode node)
		{
			var topologyId = Guid.NewGuid();
			var topology = new Topology(topologyId, new List<NodeConnectionInfo> { node.RaftEngine.Options.SelfConnection }, Enumerable.Empty<NodeConnectionInfo>(), Enumerable.Empty<NodeConnectionInfo>());

			var tcc = new TopologyChangeCommand
			{
				Requested = topology
			};

			node.RaftEngine.PersistentState.SetCurrentTopology(tcc.Requested, 0);
			node.RaftEngine.StartTopologyChange(tcc);
			node.RaftEngine.CommitTopologyChange(tcc);
			node.RaftEngine.CurrentLeader = null;
		}

		private static int GetPort()
		{
			var portRequest = Interlocked.Increment(ref numberOfPortRequests);
			return PortRangeStart - (portRequest % 25);
		}

		public void Dispose()
		{
			var exceptions = new List<Exception>();

			foreach (var node in AllNodes)
			{
				try
				{
					node.Dispose();
				}
				catch (Exception e)
				{
					exceptions.Add(e);
				}
			}

			if (exceptions.Count > 0)
				throw new AggregateException(exceptions);
		}
	}

	public class RaftNode : IDisposable
	{
		private RaftEngine _raftEngine;

		private IDisposable _server;

		private string _name;

		private string _url;

		public RaftEngine RaftEngine
		{
			get
			{
				return _raftEngine;
			}
		}

		public string Name
		{
			get
			{
				return _name;
			}
		}

		public string Url
		{
			get
			{
				return _url;
			}
		}

		public RaftNode(int port)
		{
			_name = "node-" + port;
			_url = string.Format("http://{0}:{1}", Environment.MachineName, port);

			var nodeTransport = new HttpTransport(_name);

			var node1 = new NodeConnectionInfo { Name = _name, Uri = new Uri(_url) };
			var engineOptions = new RaftEngineOptions(
				node1,
				StorageEnvironmentOptions.CreateMemoryOnly(),
				nodeTransport,
				new DictionaryStateMachine());

			engineOptions.ElectionTimeout *= 2;
			engineOptions.HeartbeatTimeout *= 2;

			_raftEngine = new RaftEngine(engineOptions);

			_server = WebApp.Start(new StartOptions
			{
				Urls = { string.Format("http://+:{0}/", port) }
			}, builder =>
			{
				var httpConfiguration = new HttpConfiguration();
				RaftWebApiConfig.Load();
				httpConfiguration.MapHttpAttributeRoutes();
				httpConfiguration.Properties[typeof(HttpTransportBus)] = nodeTransport.Bus;
				builder.UseWebApi(httpConfiguration);
			});
		}

		public void Dispose()
		{
			var toDispose = new[] { _raftEngine, _server };
			var exceptions = new List<Exception>();

			foreach (var disposable in toDispose)
			{
				if (disposable == null)
					continue;

				try
				{
					disposable.Dispose();
				}
				catch (Exception e)
				{
					exceptions.Add(e);
				}
			}

			if (exceptions.Count > 0)
				throw new AggregateException(exceptions);
		}
	}
}