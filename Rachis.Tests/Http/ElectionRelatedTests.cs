using System.Linq;
using System.Threading;

using Xunit;
using Xunit.Extensions;

namespace Rachis.Tests.Http
{
	public class ElectionRelatedTests : HttpRaftTestsBase
	{
		[Theory]
		[InlineData(11)]
		[InlineData(13)]
		[InlineData(15)]
		public void LeaderShouldStayLeader(int numberOfNodes)
		{
			var leader = CreateNetworkAndGetLeader(numberOfNodes);

			Thread.Sleep(numberOfNodes * 2000);

			Assert.Equal(RaftEngineState.Leader, leader.RaftEngine.State);

			foreach (var node in AllNodes.Where(node => node != leader))
				Assert.Equal(RaftEngineState.Follower, node.RaftEngine.State);
		}
	}
}