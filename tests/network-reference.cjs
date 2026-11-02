'use strict';
const { spawn, execFile } = require('node:child_process');
const { randomBytes } = require('node:crypto');
const net = require('node:net');
const tls = require('node:tls');
const fs = require('node:fs');
const { createCandidateConfig } = require('../electron/network-config.cjs');

function reservePort() {
 return new Promise((resolve, reject) => {
  const server = net.createServer();
  server.once('error', reject);
  server.listen(0, '127.0.0.1', () => { const port = server.address().port; server.close(error => error ? reject(error) : resolve(port)); });
 });
}
// Verify both halves of the IPv4 internet. A competing VPN may install two
// more-specific /1 routes that override Finley's default route.
function findEffectiveRoute(remote) {
 const script = "$r=Find-NetRoute -RemoteIPAddress '" + remote + "' -ErrorAction Stop | Where-Object { $_.DestinationPrefix } | Select-Object -First 1 -ExpandProperty InterfaceAlias; if (-not $r) { exit 2 }; [Console]::Out.Write($r)";
 return new Promise((resolve,reject)=>execFile('powershell.exe',['-NoProfile','-NonInteractive','-Command',script],{windowsHide:true,timeout:20000,maxBuffer:4096},(error,stdout)=>{const name=stdout.trim(); if(error||!name||name.length>256||/[\r\n\x00]/.test(name)) return reject(new Error('Could not check Windows network routes. Check your connection and try again.')); resolve(name);}));
}
async function verifyTunnelRoutes(name, lookup = findEffectiveRoute) {
 const routes = await Promise.all(['9.9.9.9','200.1.1.1'].map(lookup));
 const conflict = routes.find(route => route.toLowerCase() !== name.toLowerCase());
 if (conflict) throw new Error(`Another network route is taking precedence on ${conflict}. Disconnect any other VPN, then try connecting Finley again.`);
}
// Select the connected IPv4 default route before creating the tunnel. Binding
// the direct outbound prevents Windows selecting the newly-created TUN again.
function discoverDefaultInterface() {
 const script = "$r=Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop | ForEach-Object { $i=Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue; if ($i.ConnectionState -eq 'Connected' -and $_.InterfaceAlias -notlike 'Finley-*') { [pscustomobject]@{ Name=$_.InterfaceAlias; Metric=($_.RouteMetric + $i.InterfaceMetric) } } } | Sort-Object Metric | Select-Object -First 1 -ExpandProperty Name; if (-not $r) { exit 2 }; [Console]::Out.Write($r)";
 return new Promise((resolve,reject)=>execFile('powershell.exe',['-NoProfile','-NonInteractive','-Command',script],{windowsHide:true,timeout:20000,maxBuffer:4096},(error,stdout)=>{const name=stdout.trim();if(error||!name||name.length>256||/[\r\n\x00]/.test(name)) return reject(new Error('Could not determine the current internet interface for bypass. Check your connection and try again.'));resolve(name);}));
}
function readBytes(socket, count) {
 return new Promise((resolve, reject) => {
  let parts = Buffer.alloc(0);
  const finish = (err, value) => { socket.off('data', data); socket.off('error', fail); socket.off('close', closed); err ? reject(err) : resolve(value); };
  const data = chunk => { parts = Buffer.concat([parts, chunk]); if (parts.length >= count) { socket.pause(); const tail = parts.subarray(count); const result = parts.subarray(0, count); socket.off('data', data); if (tail.length) socket.unshift(tail); finish(null, result); } };
  const fail = () => finish(new Error('Connectivity check failed.'));
  const closed = () => finish(new Error('Connectivity check failed.'));
  socket.on('data', data); socket.once('error', fail); socket.once('close', closed); socket.resume();
 });
}
async function probeSocks(port, username, password) {
 let socket = net.connect({host:'127.0.0.1',port});
 socket.on('error', () => {});
 socket.setTimeout(8000, () => socket.destroy());
 try {
  await new Promise((resolve,reject) => { socket.once('connect',resolve); socket.once('error',reject); });
  socket.write(Buffer.from([5,1,2])); let response = await readBytes(socket,2);
  if (response[0] !== 5 || response[1] !== 2) throw new Error('Connectivity check failed.');
  const user = Buffer.from(username), pass = Buffer.from(password);
  socket.write(Buffer.concat([Buffer.from([1,user.length]),user,Buffer.from([pass.length]),pass])); response = await readBytes(socket,2);
  if (response[1] !== 0) throw new Error('Connectivity check failed.');
  const host = Buffer.from('www.gstatic.com');
  socket.write(Buffer.concat([Buffer.from([5,1,0,3,host.length]),host,Buffer.from([1,187])]));
  response = await readBytes(socket,4);
  if (response[0] !== 5 || response[1] !== 0) throw new Error('Connectivity check failed.');
  if (response[3] === 1) await readBytes(socket,6); else if (response[3] === 4) await readBytes(socket,18); else if (response[3] === 3) { const length = await readBytes(socket,1); await readBytes(socket,length[0]+2); } else throw new Error('Connectivity check failed.');
  const secure = tls.connect({socket,servername:'www.gstatic.com'}); secure.on('error', () => {}); socket = secure; secure.resume();
  await new Promise((resolve,reject) => { secure.once('secureConnect',resolve); secure.once('error',reject); });
  secure.write('GET /generate_204 HTTP/1.1\r\nHost: www.gstatic.com\r\nConnection: close\r\n\r\n');
  const line = await new Promise((resolve,reject) => { let value=''; secure.on('data', data => { value += data.toString('ascii'); if (value.includes('\r\n')) resolve(value.split('\r\n')[0]); if (value.length > 4096) reject(new Error('Connectivity check failed.')); }); secure.once('error',reject); secure.once('close',() => reject(new Error('Connectivity check failed.'))); });
  if (!/^HTTP\/1\.[01] 204(?:\s|$)/.test(line)) throw new Error('Connectivity check failed.');
 } finally { socket.destroy(); }
}
function sleep(ms) { return new Promise(resolve => setTimeout(resolve,ms)); }
function withTimeout(promise, ms, message) { let timer; return Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); })]).finally(() => clearTimeout(timer)); }
module.exports = {probeSocks, verifyTunnelRoutes, discoverDefaultInterface};
