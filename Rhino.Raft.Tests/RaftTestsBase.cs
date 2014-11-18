using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Rhino.Raft.Interfaces;
using Rhino.Raft.Messages;
using Rhino.Raft.Utils;
using Voron;
using Xunit;

namespace Rhino.Raft.Tests
{
	public class RaftTestsBase : IDisposable
	{
		private readonly List<RaftEngine> _nodes = new List<RaftEngine>();

		private readonly DebugWriter _writer = new DebugWriter("Test", Stopwatch.StartNew());

		protected void WriteLine(string format, params object[] args)
		{
			_writer.Write(format, args);
		}

		public IEnumerable<RaftEngine> Nodes { get { return _nodes; } }

		protected ManualResetEventSlim WaitForStateChange(RaftEngine node, RaftEngineState requestedState)
		{
			var mre = new ManualResetEventSlim();
			node.StateChanged += state =>
			{
				if (state == requestedState)
					mre.Set();
			};
			return mre;
		}

		protected ManualResetEventSlim WaitForToplogyChange(RaftEngine node)
		{
			var mre = new ManualResetEventSlim();
			node.TopologyChanged += state => mre.Set();
			return mre;
		}

		protected ManualResetEventSlim WaitForCommit<T>(RaftEngine node, Func<DictionaryStateMachine, bool> predicate)
		{
			var cde = new ManualResetEventSlim();
			node.CommitApplied += command =>
			{
				if (predicate((DictionaryStateMachine)node.StateMachine))
					cde.Set();
			};
			node.SnapshotInstalled += () =>
			{
				var state = (DictionaryStateMachine)node.StateMachine;
				if (predicate(state))
				{
					cde.Set();
				}
			};
			return cde;
		}

		protected ManualResetEventSlim WaitForSnapshot(RaftEngine node)
		{
			var cde = new ManualResetEventSlim();
			node.CreatedSnapshot += cde.Set;
			return cde;
		}

		protected CountdownEvent WaitForCommitsOnCluster(int numberOfCommits)
		{
			var cde = new CountdownEvent(_nodes.Count);
			foreach (var node in _nodes)
			{
				var n = node;
				if (n.CommitIndex == numberOfCommits && cde.CurrentCount > 0)
				{
					cde.Signal();
					continue;
				}
				n.CommitApplied += command =>
				{
					if (n.CommitIndex == numberOfCommits && cde.CurrentCount > 0)
						cde.Signal();
				};
				n.SnapshotInstalled += () =>
				{
					if (n.CommitIndex == numberOfCommits && cde.CurrentCount > 0)
						cde.Signal();
				};
			}

			return cde;
		}

		protected CountdownEvent WaitForCommitsOnCluster(Func<DictionaryStateMachine, bool> predicate)
		{
			var cde = new CountdownEvent(_nodes.Count);
			foreach (var node in _nodes)
			{
				var n = node;
				n.CommitApplied += command =>
				{
					var state = (DictionaryStateMachine)n.StateMachine;
					if (predicate(state) && cde.CurrentCount > 0)
					{
						n.DebugLog.Write("WaitForCommitsOnCluster match");
						cde.Signal();
					}
				};
				n.SnapshotInstalled += () =>
				{
					var state = (DictionaryStateMachine) n.StateMachine;
					if (predicate(state) && cde.CurrentCount > 0)
					{
						n.DebugLog.Write("WaitForCommitsOnCluster match"); 
						cde.Signal();
					}
				};
			}
			
			return cde;
		}


		protected CountdownEvent WaitForToplogyChangeOnCluster()
		{
			var cde = new CountdownEvent(_nodes.Count);
			foreach (var node in _nodes)
			{
				var n = node;
				n.TopologyChanged += (a) =>
				{
					if (cde.CurrentCount > 0)
					{
						cde.Signal();
					}
				};
			}

			return cde;
		}
		protected ManualResetEventSlim WaitForSnapshotInstallation(RaftEngine node)
		{
			var cde = new ManualResetEventSlim();
			node.SnapshotInstalled += cde.Set;
			return cde;
		}

		protected RaftEngine CreateNetworkAndWaitForLeader(int nodeCount, int messageTimeout = -1)
		{
			var raftNodes = CreateNodeNetwork(nodeCount, messageTimeout: messageTimeout).ToList();
			var raftEngine = _nodes[new Random().Next(0, _nodes.Count)];

			var nopCommit = WaitForCommitsOnCluster(1); // nop commit

			((InMemoryTransport) raftEngine.Transport).ForceTimeout(raftEngine.Name);

			raftNodes.First().WaitForLeader();

			nopCommit.Wait();

			var leader = raftNodes.FirstOrDefault(x => x.State == RaftEngineState.Leader);
			Assert.NotNull(leader);
			return leader;
		}


		protected static RaftEngineOptions CreateNodeOptions(string nodeName, ITransport transport, int messageTimeout, params string[] peers)
		{
			var nodeOptions = new RaftEngineOptions(nodeName,
				StorageEnvironmentOptions.CreateMemoryOnly(),
				transport,
				new DictionaryStateMachine(), 
				messageTimeout)
			{
				AllVotingNodes = peers,
				Stopwatch = Stopwatch.StartNew()
			};
			return nodeOptions;
		}

		protected static RaftEngineOptions CreateNodeOptions(string nodeName, ITransport transport, int messageTimeout, StorageEnvironmentOptions storageOptions, params string[] peers)
		{
			var nodeOptions = new RaftEngineOptions(nodeName,
				storageOptions,
				transport,
				new DictionaryStateMachine(),
				messageTimeout)
			{
				AllVotingNodes = peers,
				Stopwatch = Stopwatch.StartNew()
			};
			return nodeOptions;
		}

		protected bool AreEqual(byte[] array1, byte[] array2)
		{
			if (array1.Length != array2.Length)
				return false;

			return !array1.Where((t, i) => t != array2[i]).Any();
		}


		protected RaftEngine NewNodeFor(RaftEngine leader)
		{
			var raftEngine = new RaftEngine(CreateNodeOptions("node" + _nodes.Count, leader.Transport, leader.MessageTimeout));
			_nodes.Add(raftEngine);
			return raftEngine;
		}

		protected IEnumerable<RaftEngine> CreateNodeNetwork(int nodeCount, ITransport transport = null, int messageTimeout = -1, Func<RaftEngineOptions,RaftEngineOptions> optionChangerFunc = null)
		{
			if (messageTimeout == -1)
				messageTimeout = Debugger.IsAttached ? 60*1000 : 1000;
			transport = transport ?? new InMemoryTransport();
			var nodeNames = new List<string>();
			for (int i = 0; i < nodeCount; i++)
			{
				nodeNames.Add("node" + i);
			}

			if (optionChangerFunc == null)
				optionChangerFunc = options => options;

			var raftNetwork = nodeNames
				.Select(name => optionChangerFunc(CreateNodeOptions(name, transport, messageTimeout, nodeNames.ToArray())))
				.Select(nodeOptions => new RaftEngine(nodeOptions))
				.ToList();

			_nodes.AddRange(raftNetwork);

			return raftNetwork;
		}

		public virtual void Dispose()
		{
			_nodes.ForEach(node => node.Dispose());
		}
	}
}