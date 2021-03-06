﻿// -----------------------------------------------------------------------
//  <copyright file="AbstractRaftStateBehavior.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rachis.Commands;
using Rachis.Messages;
using Rachis.Storage;
using Rachis.Transport;

namespace Rachis.Behaviors
{
	public class LeaderStateBehavior : AbstractRaftStateBehavior
	{
		protected readonly ConcurrentDictionary<string, long> _matchIndexes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, long> _nextIndexes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, DateTime> _lastContact = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, Task> _snapshotsPendingInstallation = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);

		private readonly ConcurrentQueue<Command> _pendingCommands = new ConcurrentQueue<Command>();
		private readonly Task _heartbeatTask;

		private readonly CancellationTokenSource _disposedCancellationTokenSource = new CancellationTokenSource();
		private readonly CancellationTokenSource _stopHeartbeatCancellationTokenSource;

		public event Action HeartbeatSent;

		public LeaderStateBehavior(RaftEngine engine)
			: base(engine)
		{
			Timeout = engine.Options.ElectionTimeout / 2;
			engine.TopologyChanged += OnTopologyChanged;
			var lastLogEntry = Engine.PersistentState.LastLogEntry();

			foreach (var peer in Engine.CurrentTopology.AllNodeNames)
			{
				_nextIndexes[peer] = lastLogEntry.Index + 1;
				_matchIndexes[peer] = 0;
			}

			AppendCommand(new NopCommand());
			_stopHeartbeatCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(Engine.CancellationToken, _disposedCancellationTokenSource.Token);

			_heartbeatTask = Task.Run(() => Heartbeat(), _stopHeartbeatCancellationTokenSource.Token);
		}

		private void OnTopologyChanged(TopologyChangeCommand tcc)
		{
			// if we have any removed servers, we need to know let them know that they have
			// been removed, we do that by committing the current entry (hopefully they already
			// have topology change command, so they know they are being removed from the cluster).
			// This is mostly us being nice neighbors, this isn't required, and the cluster will reject
			// messages from nodes not considered to be in the cluster.
			if (tcc.Previous == null)
				return;

			var removedNodes = tcc.Previous.AllNodeNames.Except(tcc.Requested.AllNodeNames).ToList();
			foreach (var removedNode in removedNodes)
			{
				var nodeByName = tcc.Previous.GetNodeByName(removedNode);
				if (nodeByName == null)
					continue;
				// try sending the latest updates (which include the topology removal entry)
				SendEntriesToPeer(nodeByName);
				// at any rate, try sending the disconnection command explicitly, to gracefully shut down the node if we can
				Engine.Transport.Send(nodeByName, new DisconnectedFromCluster
				{
					From = Engine.Name,
					ClusterTopologyId = Engine.CurrentTopology.TopologyId,
					Term = Engine.PersistentState.CurrentTerm
				});
			}
		}

		private void Heartbeat()
		{
			while (_stopHeartbeatCancellationTokenSource.IsCancellationRequested == false)
			{
				foreach (var peer in Engine.CurrentTopology.AllNodes)
				{
					if (peer.Name.Equals(Engine.Name, StringComparison.OrdinalIgnoreCase))
						continue;// we don't need to send to ourselves

					_stopHeartbeatCancellationTokenSource.Token.ThrowIfCancellationRequested();

					SendEntriesToPeer(peer);
				}

				OnHeartbeatSent();
				Thread.Sleep(Engine.Options.HeartbeatTimeout);
			}
		}

		private void SendEntriesToPeer(NodeConnectionInfo peer)
		{
			LogEntry prevLogEntry;
			LogEntry[] entries;

			var nextIndex = _nextIndexes.GetOrAdd(peer.Name, -1); //new peer's index starts at, we use -1 as a sentital value

			if (Engine.StateMachine.SupportSnapshots && nextIndex != -1)
			{
				var snapshotIndex = Engine.PersistentState.GetLastSnapshotIndex();

				if (snapshotIndex != null && nextIndex < snapshotIndex)
				{
					if (_snapshotsPendingInstallation.ContainsKey(peer.Name))
						return;

					using (var snapshotWriter = Engine.StateMachine.GetSnapshotWriter())
					{
						Engine.Transport.Send(peer, new CanInstallSnapshotRequest
						{
							From = Engine.Name,
							ClusterTopologyId = Engine.CurrentTopology.TopologyId,
							Index = snapshotWriter.Index,
							Term = snapshotWriter.Term,
						});
					}
					return;
				}
			}

			if (nextIndex == -1)
				nextIndex = 0;

			try
			{
				entries = Engine.PersistentState.LogEntriesAfter(nextIndex)
					.Take(Engine.Options.MaxEntriesPerRequest)
					.ToArray();

				prevLogEntry = entries.Length == 0
					? Engine.PersistentState.LastLogEntry()
					: Engine.PersistentState.GetLogEntry(entries[0].Index - 1);
			}
			catch (Exception e)
			{
				_log.Error("Error while fetching entries from persistent state.", e);
				throw;
			}

			if (_log.IsDebugEnabled)
			{
				_log.Debug("Sending {0:#,#;;0} entries to {1} (PrevLogEntry: Term = {2} Index = {3}).", entries.Length, peer, prevLogEntry.Term, prevLogEntry.Index);
			}

			var aer = new AppendEntriesRequest
			{
				Entries = entries,
				LeaderCommit = Engine.CommitIndex,
				PrevLogIndex = prevLogEntry.Index,
				PrevLogTerm = prevLogEntry.Term,
				Term = Engine.PersistentState.CurrentTerm,
				From = Engine.Name,
				ClusterTopologyId = Engine.CurrentTopology.TopologyId,
			};

			Engine.Transport.Send(peer, aer);

			Engine.OnEntriesAppended(entries);
		}

		private void SendSnapshotToPeer(NodeConnectionInfo peer)
		{
			try
			{
				var sp = Stopwatch.StartNew();
				using (var snapshotWriter = Engine.StateMachine.GetSnapshotWriter())
				{
					_log.Info("Streaming snapshot to {0} - term {1}, index {2}", peer,
						snapshotWriter.Term,
						snapshotWriter.Index);

					Engine.Transport.Stream(peer, new InstallSnapshotRequest
					{
						Term = Engine.PersistentState.CurrentTerm,
						LastIncludedIndex = snapshotWriter.Index,
						LastIncludedTerm = snapshotWriter.Term,
						From = Engine.Name,
						ClusterTopologyId = Engine.CurrentTopology.TopologyId,
					}, stream => snapshotWriter.WriteSnapshot(stream));

					_log.Info("Finished snapshot streaming -> to {0} - term {1}, index {2} in {3}", peer, snapshotWriter.Index,
						snapshotWriter.Term, sp.Elapsed);
				}

			}
			catch (Exception e)
			{
				_log.Error("Failed to send snapshot to " + peer, e);
			}
		}

		public override RaftEngineState State
		{
			get { return RaftEngineState.Leader; }
		}

		public override void HandleTimeout()
		{
			_lastContact[Engine.Name] = DateTime.UtcNow;
			if (Engine.CurrentTopology.QuorumSize == 1)
			{
				// we can't step down, nothing to check
				LastHeartbeatTime = DateTime.UtcNow;
				return;
			}

			// we got a timeout, but during normal operations, we should get a timeout only if
			// there are no new commands  to distribute (we raise the heart beat whenever we commit)
			// or if we can't talk to a majority of the cluster, in which case we need to step 
			// down.
			// We check when was the last time we talked to a majority of the cluster, then make our decision
			var latency = GetQuorumLatencyInMilliseconds();

			if (latency < Engine.Options.ElectionTimeout)
			{
				// we are okay, a full quorum was reached within the message timeout, so we can 
				// safely resume running as the leader
				LastHeartbeatTime = DateTime.UtcNow;
				return;
			}

			if (_log.IsWarnEnabled)
			{
				var lastLogEntry = Engine.PersistentState.LastLogEntry();

				_log.Warn(
					"Couldn't commit an entry on the cluster (current index: {1}, cluster quorum latency: {0}ms), stepping down as the leader.",
					latency,
					lastLogEntry.Index);
			}

			Engine.SetState(RaftEngineState.FollowerAfterStepDown);

		}

		public override void Handle(InstallSnapshotResponse resp)
		{
            if (FromOurTopology(resp) == false)
            {
                _log.Info("Got an append entries response message outside my cluster topology (id: {0}), ignoring", resp.ClusterTopologyId);
                return;
            }

			_matchIndexes[resp.From] = resp.LastLogIndex;
			_nextIndexes[resp.From] = resp.LastLogIndex + 1;
			_lastContact[resp.From] = DateTime.UtcNow;
			Task snapshotInstallationTask;
			_snapshotsPendingInstallation.TryRemove(resp.From, out snapshotInstallationTask);
			if (resp.Success == false)
			{
				_log.Warn("Failed to install snapshot for {0} (term {1} / index {2}) because: {3}",
					resp.From, resp.CurrentTerm, resp.LastLogIndex, resp.Message);
			}
			else
			{
				_log.Info("Successfully installed snapshot at {0} for (term {1} / index {2})",
					resp.From, resp.CurrentTerm, resp.LastLogIndex);
			}
		}

		public override void Handle(CanInstallSnapshotResponse resp)
		{
            if (FromOurTopology(resp) == false)
            {
                _log.Info("Got an append entries response message outside my cluster topology (id: {0}), ignoring", resp.ClusterTopologyId);
                return;
            }

			Task snapshotInstallationTask;
			if (resp.Success == false)
			{
				_log.Debug("Received CanInstallSnapshotResponse(Success=false) from {0}, Term = {1}, Index = {2}, updating and will try again",
					resp.From,
					resp.Term,
					resp.Index);
				_matchIndexes[resp.From] = resp.Index;
				_nextIndexes[resp.From] = resp.Index + 1;
				_lastContact[resp.From] = DateTime.UtcNow;
				_snapshotsPendingInstallation.TryRemove(resp.From, out snapshotInstallationTask);
				return;
			}
			if (resp.IsCurrentlyInstalling)
			{
				_log.Debug("Received CanInstallSnapshotResponse(IsCurrentlyInstalling=false) from {0}, Term = {1}, Index = {2}, will retry when it is done",
					resp.From,
					resp.Term,
					resp.Index);

				_snapshotsPendingInstallation.TryRemove(resp.From, out snapshotInstallationTask);
				return;
			}
			_log.Debug("Received CanInstallSnapshotResponse from {0}, starting snapshot streaming task", resp.From);


			// problem, we can't just send the log entries, we have to send
			// the full snapshot to this destination, this can take a very long 
			// time for large data sets. Because of that, we are doing that in a 
			// background thread, and while we are doing that, we aren't going to be
			// doing any communication with this peer. Note that while the peer
			// is accepting the snapshot, it isn't counting the heartbeat timer, or 
			// can move to become a candidate.
			// During normal operations, we will never be using this, since we leave a buffer
			// in place (by default roughly 4K entries) to make sure that small disconnects will
			// not cause us to be forced to send a snapshot over the wire.

			if (_snapshotsPendingInstallation.ContainsKey(resp.From))
				return; // already sending

			var nodeConnectionInfo = Engine.CurrentTopology.GetNodeByName(resp.From);
			if (nodeConnectionInfo == null)
			{
				_log.Info("Got CanInstallSnapshotResponse for {0}, but it isn't in our topology, ignoring", resp.From);
				return;
			}

			var task = new Task(() => SendSnapshotToPeer(nodeConnectionInfo));
			task.ContinueWith(_ => _snapshotsPendingInstallation.TryRemove(resp.From, out _));

			if (_snapshotsPendingInstallation.TryAdd(resp.From, task))
				task.Start();
		}

		public override void Handle(AppendEntriesResponse resp)
		{
            if (FromOurTopology(resp) == false)
            {
                _log.Info("Got an append entries response message outside my cluster topology (id: {0}), ignoring", resp.ClusterTopologyId);
                return;
            }

			if (Engine.CurrentTopology.Contains(resp.From) == false)
			{
				_log.Info("Rejecting append entries response from {0} because it is not in my cluster", resp.From);
				return;
			}

			_log.Debug("Handling AppendEntriesResponse from {0}", resp.From);

			// there is a new leader in town, time to step down
			if (resp.CurrentTerm > Engine.PersistentState.CurrentTerm)
			{
				Engine.UpdateCurrentTerm(resp.CurrentTerm, resp.LeaderId);
				return;
			}

			Debug.Assert(resp.From != null);
			_nextIndexes[resp.From] = resp.LastLogIndex + 1;
			_matchIndexes[resp.From] = resp.LastLogIndex;
			_lastContact[resp.From] = DateTime.UtcNow;

			if (Engine.CurrentTopology.IsPromotable(resp.From) &&
				resp.LastLogIndex == Engine.CommitIndex)
			{
				PromoteNodeToVoter(resp);
			}

			_log.Debug("Follower ({0}) has LastLogIndex = {1}", resp.From, resp.LastLogIndex);

			if (resp.Success == false)
			{
				_log.Debug("Received Success = false in AppendEntriesResponse from {1}. Now _nextIndexes[{1}] = {0}. Reason: {2}",
					_nextIndexes[resp.From], resp.From, resp.Message);
				return;
			}

			var maxIndexOnCurrentQuorum = GetMaxIndexOnQuorum();
			if (maxIndexOnCurrentQuorum <= Engine.CommitIndex)
			{
				_log.Debug("maxIndexOnCurrentQuorum = {0} <= Engine.CommitIndex = {1}.",
					maxIndexOnCurrentQuorum, Engine.CommitIndex);
				return;
			}

			// we update the heartbeat time whenever we get a successful quorum, because
			// that means that we are good to run without any issues. Further handling is done 
			// in the HandleTimeout, to handle a situation where the leader can't talk to the clients
			LastHeartbeatTime = DateTime.UtcNow;

			_log.Debug(
				"AppendEntriesResponse => applying commits, maxIndexOnQuorom = {0}, Engine.CommitIndex = {1}", maxIndexOnCurrentQuorum,
				Engine.CommitIndex);
			Engine.ApplyCommits(Engine.CommitIndex + 1, maxIndexOnCurrentQuorum, _pendingCommands.ToDictionary(x => x.AssignedIndex, x => x));

			Command result;
			while (_pendingCommands.TryPeek(out result) && result.AssignedIndex <= maxIndexOnCurrentQuorum)
			{
				if (_pendingCommands.TryDequeue(out result) == false)
					break; // should never happen

				result.Complete();
			}
		}

		private void PromoteNodeToVoter(AppendEntriesResponse resp)
		{			
			// if we got a successful append entries response from a promotable node, and it has caught up
			// with the committed entries, it means that we can promote it to voting positions, since it 
			// can now become a leader.
			var upgradedNode = Engine.CurrentTopology.GetNodeByName(resp.From);
			if (upgradedNode == null)
				return;
			var requestTopology = new Topology(
				Engine.CurrentTopology.TopologyId,
				Engine.CurrentTopology.AllVotingNodes.Union(new[] { upgradedNode }),
				Engine.CurrentTopology.NonVotingNodes,
				Engine.CurrentTopology.PromotableNodes.Where(x => x != upgradedNode)
				);
			if (Engine.CurrentlyChangingTopology() == false)
			{
				_log.Info(
					"Node {0} is a promotable node, and it has caught up to the current cluster commit index, but we are currently updating the topology, will try again later",
					resp.From);
				return;
			}

			_log.Info(
				"Node {0} is a promotable node, and it has caught up to the current cluster commit index, promoting to voting member",
				resp.From);
			Engine.ModifyTopology(requestTopology);
		}

		/// <summary>
		/// This method works on the last contact from all nodes.
		/// </summary>
		protected long GetQuorumLatencyInMilliseconds()
		{
			var currentTopology = Engine.CurrentTopology;
			var now = DateTime.UtcNow;
			var results = (
				from contact in _lastContact
				where currentTopology.IsVoter(contact.Key)
				select (now - contact.Value).TotalMilliseconds
					into time
					orderby time
					select time
				).ToList();

			return (long)results[currentTopology.QuorumSize - 1];
		}

		/// <summary>
		/// This method works on the match indexes, assume that we have three nodes
		/// A, B and C, and they have the following index values:
		/// 
		/// { A = 4, B = 3, C = 2 }
		/// 
		/// 
		/// In this case, the quorum agrees on 3 as the committed index.
		/// 
		/// Why? Because A has 4 (which implies that it has 3) and B has 3 as well.
		/// So we have 2 nodes that have 3, so that is the quorom.
		/// </summary>
		protected long GetMaxIndexOnQuorum()
		{
			var topology = Engine.CurrentTopology;
			var dic = new Dictionary<long, int>();
			foreach (var index in _matchIndexes)
			{
				if (topology.IsVoter(index.Key) == false)
					continue;

				int count;
				dic.TryGetValue(index.Value, out count);

				dic[index.Value] = count + 1;
			}
			var boost = 0;
			foreach (var source in dic.OrderByDescending(x => x.Key))
			{
				var confirmationsForThisIndex = source.Value + boost;
				boost += source.Value;
				if (confirmationsForThisIndex >= topology.QuorumSize)
					return source.Key;
			}

			return -1;
		}

		public void AppendCommand(Command command)
		{
			var index = Engine.PersistentState.AppendToLeaderLog(command);
			_matchIndexes[Engine.Name] = index;
			_nextIndexes[Engine.Name] = index + 1;
			_lastContact[Engine.Name] = DateTime.UtcNow;

			if (Engine.CurrentTopology.QuorumSize == 1)
			{
				CommitEntries(null, index);
			    command.Complete();

				return;
			}

			if (command.Completion != null)
				_pendingCommands.Enqueue(command);
		}

		public override void Dispose()
		{
			Engine.TopologyChanged -= OnTopologyChanged;
			_disposedCancellationTokenSource.Cancel();
			var sp = Stopwatch.StartNew();
			try
			{
				_heartbeatTask.Wait(Timeout * 2);
			}
			catch (OperationCanceledException)
			{
				//expected
			}
			catch (AggregateException e)
			{
				if (e.InnerException is OperationCanceledException == false)
					throw;
			}
			finally
			{
				_log.Info("Disposing leader behavior took {0:#,#;;0} ms", sp.ElapsedMilliseconds);
				Command result;
				TimeoutException timeoutException = null;
				while (_pendingCommands.TryDequeue(out result))
				{
					if (result.Completion != null)
					{
						timeoutException = timeoutException ??
										   new TimeoutException(
											   "Couldn't commit this command after the timeout has expired, aborting (note that this still might get commited)");
						result.Completion.TrySetException(timeoutException);
					}
				}
			}
		}

		protected virtual void OnHeartbeatSent()
		{
			var handler = HeartbeatSent;
			if (handler != null) handler();
		}
	}
}