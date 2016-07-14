class homeViewModel {
    nodes = ko.observableArray([]);
    NodesNames = ko.observableArray([]);
    KeysWithValues = ko.observableArray([]);
    fromPort = ko.observable(8090);
    sizeOfCluster = ko.observable(5);
    baseDir = ko.observable("c:\\work\\tailfeather\\");
    leaderPort = ko.observable(8090);
    clusterCreated = ko.observable(false);
    key = ko.observable("");
    readValue = ko.observable("");
    writeValue = ko.observable("");
    appendValue = ko.observable("");
    createCluster() {
        if (this.clusterCreated())
            return;
        var args = "baseDir=" + encodeURIComponent(this.baseDir());
        args += "&fromPort=" + this.fromPort();
        args += "&sizeOfCluster=" + this.sizeOfCluster();
        args += "&leaderPort=" + this.leaderPort();
        $.ajax("/demo/create-cluster?"+args, "GET").done(x => {
            this.nodes(this.topologyToNodes(x));
            this.clusterCreated(true);
        });
    }
    refreshTopology() {
        if (!this.clusterCreated())
            return;
        $.ajax("/demo/fetch-cluster-topology?" + "fromPort=" + this.leaderPort(), "GET").done(x => {
            this.nodes(this.topologyToNodes(x));
        });
    }
    killNode(node) {
        $.ajax("/demo/kill-node?name=" + node.Name , "GET").done(x => {
            this.nodes(this.topologyToNodes(x));
        });
    }
    killThemAll() {
        $.ajax("/demo/kill-them-all", "GET").done(x => {
            this.nodes(this.topologyToNodes(x));
            this.clusterCreated(false);
        });        
    }
    
    GetkillNodeText(node) {
        return "Kill " + node.Name;
    }
    reviveNode(node) {
        $.ajax("/demo/revive-node?name=" + node.Name, "GET").done(x => {
            this.nodes(this.topologyToNodes(x));
        });
    }
    readKey() {
        var key = this.key();
        $.ajax("/demo/read-key?key=" + key, "GET").done(x => {
            this.readValue(x);
        }); 
    }
    append() {
        var url = "/demo/append?key=" + this.key();
        var value = this.appendValue();
        var self = this;
        $.post(url, value, function (returnedData) {
            //this is just so the UI will refresh on write nicly
            self.fetchAllValues();
        });
    }
    setKey() {
        var key = this.key();
        var writeValue = this.writeValue();
        var self = this;
        $.post("/demo/set-key?key=" + key, writeValue, function (returnedData) {
            self.writeValue("");
            //this is just so the UI will refresh on write nicly
            self.fetchAllValues();
        });
    }
    deleteKey() {
        var key = this.key();
        $.ajax({ url: "/demo/delete-key?key=" + key, type: "DELETE" })
            .done(
                result =>
                {
                    this.key("");
                });
    }
    topologyToNodes(topo) {
        var newNodes = [];
        var leader = topo.CurrentLeader;  
        for (var index in topo.AllVotingNodes) {
            var node = topo.AllVotingNodes[index];
            if (leader === node['Name'])
                newNodes.push({ Name: node.Name, Uri: node.Uri, State: "Leader" });
            else
                newNodes.push({ Name: node.Name, Uri: node.Uri, State: "Voting" });
        }
        for (var index in topo.PromotableNodes) {
            var node = topo.PromotableNodes[index];
            newNodes.push({ Name: node.Name, Uri: node.Uri, State: "Promotable" });
        }
        for (var index in topo.NonVotingNodes) {
            var node = topo.PromotableNodes[index];
            newNodes.push({ Name: node.Name, Uri: node.Uri, State: "Non Voting" });
        }
        return newNodes;
    }

    fetchAllValues() {
        var self = this;
        $.ajax("/demo/read-all", "GET").done(x => {
            self.NodesNames(x.Nodes);
            this.KeysWithValues([]);
            for (var i = 0; i < x.KeysValues.length; i++) {
                this.KeysWithValues.push(x.KeysValues[i]);
            }
        });
    }
}

export = homeViewModel;