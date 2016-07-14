define(["require", "exports"], function (require, exports) {
    "use strict";
    var homeViewModel = (function () {
        function homeViewModel() {
            this.nodes = ko.observableArray([]);
            this.NodesNames = ko.observableArray([]);
            this.KeysWithValues = ko.observableArray([]);
            this.fromPort = ko.observable(8090);
            this.sizeOfCluster = ko.observable(5);
            this.baseDir = ko.observable("c:\\work\\tailfeather\\");
            this.leaderPort = ko.observable(8090);
            this.clusterCreated = ko.observable(false);
            this.key = ko.observable("");
            this.readValue = ko.observable("");
            this.writeValue = ko.observable("");
            this.appendValue = ko.observable("");
        }
        homeViewModel.prototype.createCluster = function () {
            var _this = this;
            if (this.clusterCreated())
                return;
            var args = "baseDir=" + encodeURIComponent(this.baseDir());
            args += "&fromPort=" + this.fromPort();
            args += "&sizeOfCluster=" + this.sizeOfCluster();
            args += "&leaderPort=" + this.leaderPort();
            $.ajax("/demo/create-cluster?" + args, "GET").done(function (x) {
                _this.nodes(_this.topologyToNodes(x));
                _this.clusterCreated(true);
            });
        };
        homeViewModel.prototype.refreshTopology = function () {
            var _this = this;
            if (!this.clusterCreated())
                return;
            $.ajax("/demo/fetch-cluster-topology?" + "fromPort=" + this.leaderPort(), "GET").done(function (x) {
                _this.nodes(_this.topologyToNodes(x));
            });
        };
        homeViewModel.prototype.killNode = function (node) {
            var _this = this;
            $.ajax("/demo/kill-node?name=" + node.Name, "GET").done(function (x) {
                _this.nodes(_this.topologyToNodes(x));
            });
        };
        homeViewModel.prototype.killThemAll = function () {
            var _this = this;
            $.ajax("/demo/kill-them-all", "GET").done(function (x) {
                _this.nodes(_this.topologyToNodes(x));
                _this.clusterCreated(false);
            });
        };
        homeViewModel.prototype.GetkillNodeText = function (node) {
            return "Kill " + node.Name;
        };
        homeViewModel.prototype.reviveNode = function (node) {
            var _this = this;
            $.ajax("/demo/revive-node?name=" + node.Name, "GET").done(function (x) {
                _this.nodes(_this.topologyToNodes(x));
            });
        };
        homeViewModel.prototype.readKey = function () {
            var _this = this;
            var key = this.key();
            $.ajax("/demo/read-key?key=" + key, "GET").done(function (x) {
                _this.readValue(x);
            });
        };
        homeViewModel.prototype.append = function () {
            var url = "/demo/append?key=" + this.key();
            var value = this.appendValue();
            var self = this;
            $.post(url, value, function (returnedData) {
                //this is just so the UI will refresh on write nicly
                self.fetchAllValues();
            });
        };
        homeViewModel.prototype.setKey = function () {
            var key = this.key();
            var writeValue = this.writeValue();
            var self = this;
            $.post("/demo/set-key?key=" + key, writeValue, function (returnedData) {
                self.writeValue("");
                //this is just so the UI will refresh on write nicly
                self.fetchAllValues();
            });
        };
        homeViewModel.prototype.deleteKey = function () {
            var _this = this;
            var key = this.key();
            $.ajax({ url: "/demo/delete-key?key=" + key, type: "DELETE" })
                .done(function (result) {
                _this.key("");
            });
        };
        homeViewModel.prototype.topologyToNodes = function (topo) {
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
        };
        homeViewModel.prototype.fetchAllValues = function () {
            var _this = this;
            var self = this;
            $.ajax("/demo/read-all", "GET").done(function (x) {
                self.NodesNames(x.Nodes);
                _this.KeysWithValues([]);
                for (var i = 0; i < x.KeysValues.length; i++) {
                    _this.KeysWithValues.push(x.KeysValues[i]);
                }
            });
        };
        return homeViewModel;
    }());
    return homeViewModel;
});
