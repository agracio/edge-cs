const spawn = require('child_process').spawn;
const path = require('path')
const fs = require('fs');

spawn('dotnet', ['restore', 'EdgeCs.sln'], { stdio: 'inherit', cwd: path.resolve(__dirname) })
    .on('close', function(code, signal) {
        if (code === 0) {
            spawn('dotnet', ['build', 'EdgeCs.sln', '--configuration', 'Release'], { stdio: 'inherit', cwd: path.resolve(__dirname) })
            .on('close', function(code, signal) {
                if (code === 0) {
                    copy();
                }
            })
        }
    });

function copy() {

    fs.copyFileSync('src/edge-cs/bin/Release/edge-cs.dll', 'lib/edge-cs.dll')
    fs.copyFileSync('src/edge-cs-coreclr/bin/Release/edge-cs-coreclr.dll', 'lib/edge-cs-coreclr.dll')
    fs.copyFileSync('src/edge-cs-coreclr/bin/Release/edge-cs-base.dll', 'lib/edge-cs-base.dll')
    fs.copyFileSync('src/edge-cs-coreclr/bin/Release/edge-cs-coreclr.deps.json', 'lib/edge-cs-coreclr.deps.json')

}

   