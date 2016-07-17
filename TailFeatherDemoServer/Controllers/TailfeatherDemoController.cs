using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TailFeather.Client;

namespace TailFeatherDemoServer.Controllers
{
    public class TailfeatherDemoController: ApiController
    {
        [HttpGet]
        [Route("demo/create-cluster")]
        public object CreateCluster([FromUri] string baseDir, [FromUri] int fromPort, [FromUri]int sizeOfCluster, [FromUri]int leaderPort)
        {
            return TailFeatherCluster.Instance.CreateTailFeatherCluster(baseDir,fromPort,sizeOfCluster,leaderPort);
        }

        [HttpGet]
        [Route("demo/launch-cluster")]
        public object LunchCluster([FromUri] string baseDir, [FromUri] int fromPort, [FromUri]int sizeOfCluster)
        {
            return TailFeatherCluster.Instance.LaunchTailFeatherCluster(baseDir, fromPort, sizeOfCluster);
        }

        [HttpGet]
        [Route("demo/fetch-cluster-topology")]
        public object FetchClusterTopology([FromUri] int fromPort)
        {
            try
            {
                return TailFeatherCluster.Instance.GetClusterTopology(fromPort);
            }
            catch
            {
                return TailFeatherCluster.Instance.TryGetTopologyFromVotingNodes();
            }            
        }

        [HttpGet]
        [Route("demo/kill-node")]
        public object KillNode([FromUri] string name)
        {
            TailFeatherCluster.Instance.KillNode(name);
            return TailFeatherCluster.Instance.TryGetTopologyFromVotingNodes();
        }

        [HttpGet]
        [Route("demo/kill-them-all")]
        public object KillThemAll()
        {
            return TailFeatherCluster.Instance.KillAllNodes();             
        }

        [HttpGet]
        [Route("demo/revive-node")]
        public object ReviveNode([FromUri] string name)
        {
            TailFeatherCluster.Instance.ReviveNode(name);
            return TailFeatherCluster.Instance.TryGetTopologyFromVotingNodes();
        }

        [HttpGet]
        [Route("demo/read-key")]
        public async Task<JToken> ReadKey([FromUri] string key)
        {
            return await TailFeatherCluster.Instance.ReadKey(key);
        }

        [HttpGet]
        [Route("demo/read-all")]
        public async Task<object> Readall()
        {
            return await TailFeatherCluster.Instance.ReadAll();
        }

        [HttpPost]
        [Route("demo/append")]
        public Task Append([FromUri] string key)
        {
            HttpContent requestContent = Request.Content;
            string jsonContent = requestContent.ReadAsStringAsync().Result;
            return TailFeatherCluster.Instance.Append(key, jsonContent);
        }

        [HttpPost]
        [Route("demo/set-key")]
        public Task SetKey([FromUri] string key)
        {
            HttpContent requestContent = Request.Content;
            string jsonContent = requestContent.ReadAsStringAsync().Result;
            return TailFeatherCluster.Instance.SetKey(key, jsonContent);
        }

        [HttpDelete]
        [Route("demo/delete-key")]
        public Task DeleteKey([FromUri] string key)
        {
            return TailFeatherCluster.Instance.DeleteKey(key);
        }

        /*  [HttpGet]
          [Route("demo/read-key")]
          public object FetchClusterTopology([FromUri] string key)
          {
              //return TailFeatherCluster.Instance.GetClusterTopology(fromPort);
          }*/

    }
}
