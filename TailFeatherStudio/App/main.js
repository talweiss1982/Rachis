requirejs.config({
    paths: {
    }
});

define('jquery', function () { return jQuery; });
define('knockout', ko);
define('mapping', ko.mapping);
define(["require", "exports", 'ViewModels/homeViewModel'], function (a, b, model) {
    var vm = new model();
    ko.applyBindings(vm, document.getElementById('homeViewModel'));
});
