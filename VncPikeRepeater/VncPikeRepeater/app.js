'use strict';

// Read command line
let nodePath = process.argv[0];
let appPath = process.argv[1];
let viewerlistenPort = process.argv[2];
let serverlistenPort = process.argv[3];
let lookerListenPort = process.argv[4];

console.log("Server port: " + serverlistenPort + " Viewers port: " + viewerlistenPort + " Lookers port: " + lookerListenPort);

var fs = require("fs");
var net = require("net");
var http = require("http");
var crypto = require("crypto");
var urlParse = require("url").parse;
var VncRepeaterSession = require("./repeater/VncRepeaterSession");
var VncServerSession = require("./repeater/VncServerSession");

// Certificados para https
//var fileKey = "ssl.key";
//var fileCrt = "ssl.crt";

//var key = fs.readFileSync("./certificado/" + fileKey, "utf8");
//var cert = fs.readFileSync("./certificado/" + fileCrt, "utf8");

// Keep sessions chats
var sessions = [];

function getSessionByServerId(_serverId) {
    var picked = sessions.find(o => o.getServerId() === _serverId);
    return picked;
}

function checkSessions() {
    var toremove = [];
    sessions.forEach(function (item, i, sessions) {
        let t = item.getTimealive();
        if (t > 10000 && item.getViewerSocket() == null) {
            item.close();
            toremove.push(item);
        }
    });

    toremove.forEach(function (item, i, toremove) {
        sessions.splice(i, 1);
    });
}

setInterval(function () {
    checkSessions();
}, 10000);

// Start a TCP Server (VNC SERVER side)
net.createServer(function (socket) {

    // Identify this client.
    socket.name = socket.remoteAddress + ":" + socket.localPort;
    console.log("[VNC Server] connected " + socket.name);

    let session = new VncRepeaterSession(socket);

    // Handle incoming messages from clients.
    socket.on('data', function (data) {

        let _session = session;

        if (_session != null) {
            if (_session.getServerId() == 0) {
                // get the server id.
                let serverId_s = String(data).toUpperCase().replace("ID:", "");
                let setverId_i = parseInt(serverId_s);

                // check if already has session with the id
                if (getSessionByServerId(setverId_i) != null) {
                    console.log("[VNC Server] alreay has been ID:" + setverId_i);
                    socket.emit('end');
                    return;
                }

                // Put this new client in the list
                sessions.push(_session);

                _session.setServerId(setverId_i)
            }
            else {

                // Transfer data
                if (_session.getViewerSocket() != null) {
                    _session.write(data);
                    console.log("[VNC Server] S -> V : " + data.length);
                }
            }
        }
    });

    // Remove the client from the list when it leaves
    socket.on('end', function () {
        console.log("Desconection");
        if (session != null) {
            if (session.getViewerSocket() != null) {
                session.getViewerSocket().destroy();
            }
            session.close();
            session = null;
        }
    });

}).listen(serverlistenPort);

// Put a friendly message on the terminal of the server.
console.log("Chat server running at port " + serverlistenPort + "\n");

// Start a TCP Server (VNC VIEWER side)
net.createServer(function (socket) {
    // Identify this client
    socket.name = socket.remoteAddress + ":" + socket.localPort;
    console.log("[VNC Viewer] connected " + socket.name);

    // Send to viewer message that it is a repeater
    socket.write("RFB 000.000\n", "binary");

    let session = null;
    let setverId_i = 0;

    // Handle incoming messages from clients.
    socket.on('data', function (data) {
        let _session = session;

        if (setverId_i == 0) {
            let serverId_s = String(data).toUpperCase().replace("ID:", "");
            setverId_i = parseInt(serverId_s);
            session = getSessionByServerId(setverId_i);

            // Close the socket if I cant find server session
            if (session == undefined)
            {
                console.log("Cant find a session with ID:" + serverId_s);
                socket.emit('end');
            }

            // Append the socket as viewer role
            session.appendViewer(socket);

            socket.write("RFB 003.008\n", "binary");
        }
        else {

            if (_session != null) {
                if (_session == null) {
                    console.log("Cant find a server session. Illegal data");
                    socket.emit('end');
                }

                // Transfer data
                if (_session.getServerSocket() != null) {
                    _session.getServerSocket().write(data);
                    console.log("[VNC Server] V -> S : " + data.length);
                }
            }
        }
    });

    // Remove the client from the list when it leaves
    socket.on('end', function () {
        console.log("Desconection");
        if (session != null) {
            if (session.getServerSocket() != null) {
                session.getServerSocket().destroy();
            }
            session.close();
            session = null;
        }
    });

}).listen(viewerlistenPort);

// Put a friendly message on the terminal of the server.
console.log("Chat viewer running at port " + viewerlistenPort + "\n");

// Start a TCP Server (VNC LOOKER side)
net.createServer(function (socket) {

    // Identify this client
    socket.name = socket.remoteAddress + ":" + socket.localPort;
    console.log("[VNC Looker]  Knock-knock " + socket.name);

    // Send to viewer message that it is a repeater
    socket.write("RFB 000.000\n", "binary");

    let session = null;
    let setverId_i = 0;

    // Handle incoming messages from clients.
    socket.on('data', function (data) {

        if (setverId_i == 0) {
            let serverId_s = String(data).toUpperCase().replace("ID:", "");
            setverId_i = parseInt(serverId_s);
            session = getSessionByServerId(setverId_i);

            // Close the socket if I cant find server session
            if (session == null) {
                console.log("Cant find a session with ID:" + serverId_s);
                socket.emit('end');
            }

            // Append the socket as viewer role
            session.appendLooker(socket);
        }
    });

    // Remove the client from the list when it leaves
    socket.on('end', function () {
        
    });

}).listen(lookerListenPort);

// Put a friendly message on the terminal of the server.
console.log("Chat looker running at port " + lookerListenPort + "\n");