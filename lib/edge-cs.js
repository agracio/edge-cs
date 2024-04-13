var path = require('path');

exports.getCompiler = function () {
	return path.join(__dirname, 'edge-cs.dll');
};

exports.getBootstrapDependencyManifest = function() {
	return path.join(__dirname, 'Edge.js.CSharp.deps.json');
}
