'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const net = require('node:net');
const { probeSocks } = require('../electron/network.cjs');
test('probe rejects an unauthenticated or broken proxy without treating it as connected', async () => {
 const server = net.createServer(socket => socket.once('data', () => socket.end(Buffer.from([5,255]))));
 await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
 try { await assert.rejects(probeSocks(server.address().port,'user','pass')); }
 finally { await new Promise(resolve => server.close(resolve)); }
});
test('probe handles a SOCKS reply delivered in one packet and advances to TLS', {timeout: 5000}, async () => {
 let stage = 0;
 const server = net.createServer(socket => socket.on('data', () => {
  if (stage === 0) { stage++; socket.write(Buffer.from([5,2])); }
  else if (stage === 1) { stage++; socket.write(Buffer.from([1,0])); }
  else if (stage === 2) { stage++; socket.write(Buffer.from([5,0,0,1,127,0,0,1,12,34])); }
  else { stage++; socket.destroy(); }
 }));
 await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
 try { await assert.rejects(probeSocks(server.address().port,'user','pass')); assert.equal(stage,4); }
 finally { await new Promise(resolve => server.close(resolve)); }
});
