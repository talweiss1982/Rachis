using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Rhino.Raft.Messages;

namespace Rhino.Raft.Interfaces
{

	/// <summary>
	/// abstraction for transport between Raft nodes.
	/// </summary>
	public interface ITransport
	{
		bool TryReceiveMessage(out MessageEnvelope messageEnvelope, int timeout, CancellationToken cancellationToken);

		void Send(string dest, AppendEntriesRequest req);
		void Send(string dest, RequestVoteRequest req);
		void Send(string dest, AppendEntriesResponse resp);
		void Send(string dest, RequestVoteResponse resp);
	}

}