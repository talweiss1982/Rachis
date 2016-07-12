using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TailFeather.Client;

namespace TailFeatherDemoServer
{
    public class TailFeatherCluster
    {
        public static TailFeatherCluster Instance => _instance.Value;
        private static Lazy<TailFeatherCluster> _instance = new Lazy<TailFeatherCluster>(()=>new TailFeatherCluster());
        private string baseDir;
        public object CreateTailFeatherCluster(string baseDir, int fromPort, int sizeOfCluster, int? leaderPort)
        {
            if (string.IsNullOrEmpty(baseDir))
                throw new ArgumentException("Base directory can't be null or empty");
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
            }
            else if (Directory.EnumerateFileSystemEntries(baseDir).Any())
            {
                LaunchTailFeatherCluster(baseDir,fromPort,sizeOfCluster);  
                return GetClusterTopology(leaderPort??fromPort);              
            }
            this.baseDir = baseDir;
            for (var i = fromPort; i < fromPort + sizeOfCluster; ++i)
            {
                var nodeName = $"Node{i}";
                var nodeDir = Path.Combine(baseDir, nodeName);
                Directory.CreateDirectory(nodeDir);
                var args = $"--port={i} --d={nodeDir} --n={nodeName} ";
                if (leaderPort.HasValue && leaderPort.Value == i)
                {
                    this.leaderPort = i;
                    var leaderProcess = Process.Start($@"..\..\..\TailFeather\bin\{Mode}\TailFeather.exe", args + " --bootstrap");
                    SpinWait.SpinUntil(() => leaderProcess.HasExited);
                }
                var p = Process.Start($@"..\..\..\TailFeather\bin\{Mode}\TailFeather.exe", args);
                portsToProcess[i] = p;
            }
            for (var i = fromPort; i < fromPort + sizeOfCluster; ++i)
            {
                if (leaderPort.HasValue && leaderPort.Value == i)
                    continue;
                var nodeName = $"Node{i}";
                var encodedUrl = HttpUtility.UrlEncode($"http://localhost:{i}");
                var request = WebRequest.Create($"http://localhost:{leaderPort}/tailfeather/admin/fly-with-us?url={encodedUrl}&name={nodeName}");
                request.Method = HttpMethod.Get.Method;
                request.GetResponse();
            }
            return GetClusterTopology(leaderPort ?? fromPort);
        }

        private const string LeaderTitle = "Leader - ";
        public object GetClusterTopology(int fromPort)
        {
            var request = WebRequest.Create($"http://localhost:{fromPort}/tailfeather/admin/flock");
            request.Method = HttpMethod.Get.Method;
            using (var reader = new StreamReader(request.GetResponse().GetResponseStream()))
            {
                var responseAsString = reader.ReadToEnd();
                
                Topology = JsonConvert.DeserializeObject<TailFeatherTopology>(responseAsString);
                return Topology;
            }
        }

        public object TryGetTopologyFromVotingNodes()
        {
            if (Topology == null)
                return null;
            foreach (var voterPort in Topology.AllVotingNodes.Select(x=>int.Parse(x.Name.Substring("Node".Length))))
            {
                try
                {
                    return GetClusterTopology(voterPort);
                }
                catch
                {
                }
            }
            return null;
        }
        public TailFeatherTopology Topology
        {
            get { return _topology; }
            private set
            {                
                var uries = value.AllVotingNodes.Select(x => x.Uri);
                    uries.Union(value.NonVotingNodes.Select(x => x.Uri));
                    uries.Union(value.PromotableNodes.Select(x => x.Uri));
                if (Client != null)
                {
                    Client.Dispose();
                }
                Client = new TailFeatherClient(uries.ToArray());                    
                _topology = value;
            }
        }

        public TailFeatherClient Client { get; set; }

        //TailFeatherClient
        private TailFeatherTopology _topology;

        public object LaunchTailFeatherCluster(string baseDir, int fromPort, int sizeOfCluster)
        {
            portsToProcess.Clear();
            this.baseDir = baseDir;
            for (var i = fromPort; i < fromPort + sizeOfCluster; ++i)
            {
                var nodeName = $"Node{i}";
                var nodeDir = Path.Combine(baseDir, nodeName);
                if (Directory.Exists(nodeDir))
                {
                    var args = $"--port={i} --d={nodeDir} --n={nodeName} ";
                    var p = Process.Start($@"..\..\..\TailFeather\bin\{Mode}\TailFeather.exe", args);
                    portsToProcess[i] = p;
                    // we will fetch the leader when fetching the topology
                    leaderPort = -1;
                }
                else
                {
                    throw new DirectoryNotFoundException($"The base directory:{baseDir} doesn't contain {nodeName} directory");
                }
            }
            return GetClusterTopology(fromPort);
        }

        public void KillNode(string name)
        {
            int port;
            Process p;
            if (int.TryParse(name.Substring(4), out port) && portsToProcess.TryGetValue(port, out p))
            {
                p.Kill();
                portsToProcess.Remove(port);
            }            
        }

        public void ReviveNode(string name)
        {
            var port = int.Parse(name.Substring(4));
            var nodeDir = Path.Combine(baseDir, name);
            if (Directory.Exists(nodeDir))
            {
                var args = $"--port={port} --d={nodeDir} --n={name} ";
                var p = Process.Start($@"..\..\..\TailFeather\bin\{Mode}\TailFeather.exe", args);
                portsToProcess[port] = p;
                // we will fetch the leader when fetching the topology
                leaderPort = -1;
            }
            else
            {
                throw new DirectoryNotFoundException($"The base directory:{baseDir} doesn't contain {name} directory");
            }
        }

        public async Task<JToken> ReadKey(string key)
        {
            if (Client == null)
                return null;
            var val = await Client.Get(key);
            keysToJObjects[key] = val;
            return val;
        }

        public object ReadAll()
        {
            return keysToJObjects.Select(x=>new {x.Key,x.Value}).ToArray();
        }

        public async Task SetKey(string key,JToken val)
        {
            if (Client == null)
                return;
            await Client.Set(key, val);
            keysToJObjects[key] = val;
        }

        public async Task DeleteKey(string key)
        {
            if (Client == null)
                return;
            await Client.Remove(key);
            if(keysToJObjects.ContainsKey(key))
                keysToJObjects.Remove(key);
        }


        private Dictionary<int,Process> portsToProcess = new Dictionary<int, Process>();
        private Dictionary<string,JToken> keysToJObjects = new Dictionary<string, JToken>();
        private int leaderPort;
        [DllImport("user32.dll")]
        static extern int SetWindowText(IntPtr hWnd, string text);

#if DEBUG
        private static string Mode = "Debug";
#else
        private static string Mode = "Release";
#endif


    }
}
