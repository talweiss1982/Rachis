using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
            var topology = GetClusterTopology(leaderPort ?? fromPort);
            File.WriteAllText(Path.Combine(baseDir,"topology.json"), JObject.FromObject(topology).ToString());            
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
                var uries = GetAllNodesUries(value);
                Client?.Dispose();
                if(uries!= null && uries.Any())
                    Client = new TailFeatherClient(uries.ToArray());                    
                _topology = value;
            }
        }

        private static IEnumerable<Uri> GetAllNodesUries(TailFeatherTopology value)
        {
            var uries = value.AllVotingNodes?.Select(x => x.Uri);
            uries?.Union(value.NonVotingNodes?.Select(x => x.Uri));
            uries?.Union(value.PromotableNodes?.Select(x => x.Uri));
            return uries;
        }

        private static Dictionary<string,Uri> GetAllNodesUriesAsDictionary(TailFeatherTopology value)
        {
            var res = new Dictionary<string,Uri>();
            foreach (var nodeConnectionInfo in value.AllVotingNodes)
            {
                res[nodeConnectionInfo.Name] = nodeConnectionInfo.Uri;
            }
            foreach (var nodeConnectionInfo in value.NonVotingNodes)
            {
                res[nodeConnectionInfo.Name] = nodeConnectionInfo.Uri;
            }
            foreach (var nodeConnectionInfo in value.PromotableNodes)
            {
                res[nodeConnectionInfo.Name] = nodeConnectionInfo.Uri;
            }
            return res;
        }
        public TailFeatherClient Client { get; set; }

        //TailFeatherClient
        private TailFeatherTopology _topology;

        public object LaunchTailFeatherCluster(string baseDir, int fromPort, int sizeOfCluster)
        {
            portsToProcess.Clear();
            this.baseDir = baseDir;
            //Assuming the running process are the ones in the topology...
            //I could get the ports from the title of the process but this is just a demo...
            var topoFilePath = Path.Combine(baseDir, "topology.json");
            if (File.Exists(topoFilePath) && Process.GetProcessesByName("TailFeather").Length == sizeOfCluster)
            {
                return JsonConvert.DeserializeObject<TailFeatherTopology>(File.ReadAllText(topoFilePath));
            }
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
                try
                {
                    p.Kill();
                }
                catch 
                {
                    //what can i do...
                }
                portsToProcess.Remove(port);
            }            
        }

        public TailFeatherTopology KillAllNodes()
        {
            foreach (var process in portsToProcess.Values)
            {
                try
                {
                    process.Kill();
                }
                catch 
                {
                }
            }
            portsToProcess.Clear();
            Topology = new TailFeatherTopology();
            return Topology;
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

        //very inefficient, just for the demo...
        public async Task<object> ReadAll()
        {
            if (Topology == null)
                return null;
            var nodesToUries = GetAllNodesUriesAsDictionary(Topology);
            var res = new Dictionary<string,dynamic[]>();
            List<Task> readingTasks = new List<Task>();
            foreach (var nameAndUri in nodesToUries)
            {
                    var readTask = Task.Run(() => ReadAllKeysFromSingleSource(nameAndUri)).ContinueWith((t) =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion)
                        {
                            res[nameAndUri.Key] = t.Result;
                        }
                    });
                readingTasks.Add(readTask);
            }
            await Task.WhenAll(readingTasks);
            return GenerateStudioReadyResult(res);
        }

        private object GenerateStudioReadyResult(Dictionary<string, dynamic[]> res)
        {
            var nodesNamesAsList = res.Keys.ToList();
            int keyIndex = 0;
            var allKeysAndValuesArray = new dynamic[keysToJObjects.Keys.Count];
            foreach (var key in keysToJObjects.Keys)
            {
                var keyValuesArray = new dynamic[nodesNamesAsList.Count + 1];
                var colIndex = 0;
                keyValuesArray[colIndex++] = key;
                foreach (var nodeName in nodesNamesAsList)
                {
                    keyValuesArray[colIndex++] = res[nodeName][keyIndex];
                    if (keyValuesArray[colIndex - 1] == null)
                        keyValuesArray[colIndex - 1] = "--empty--";
                }
                allKeysAndValuesArray[keyIndex++] = keyValuesArray;
            }
            var studioReadyResult = new {Nodes = nodesNamesAsList, KeysValues = allKeysAndValuesArray};
            return studioReadyResult;
        }

        private dynamic[] ReadAllKeysFromSingleSource(KeyValuePair<string, Uri> nameAndUri)
        {
            dynamic[] resArray = new dynamic[keysToJObjects.Keys.Count];
            int i = 0;
            foreach (var keyToRead in keysToJObjects.Keys)
            {
                var request = WebRequest.Create($"{nameAndUri.Value}/tailfeather/key-val/read?key={keyToRead}");
                request.Method = HttpMethod.Get.Method;
                using (var reader = new StreamReader(request.GetResponse().GetResponseStream()))
                {
                    var responseAsString = reader.ReadToEnd();

                    resArray[i++] =  JObject.Parse(responseAsString)["Value"];
                }
            }
            return resArray;
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
