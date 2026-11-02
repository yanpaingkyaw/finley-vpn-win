'use strict';
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const VERSION = 1, MAX_BYTES = 1024 * 1024;

function frame(value) {
 const body = Buffer.from(JSON.stringify(value));
 if (!body.length || body.length > MAX_BYTES) throw new Error('Finley service request is too large.');
 const header = Buffer.alloc(4); header.writeUInt32LE(body.length);
 return Buffer.concat([header, body]);
}
function createDecoder(receive) {
 let buffered = Buffer.alloc(0);
 return chunk => {
  if (!Buffer.isBuffer(chunk)) throw new Error('Invalid service data.');
  // Native output uses bounded frames; retain at most one incomplete frame.
  buffered = Buffer.concat([buffered, chunk]);
  while (buffered.length >= 4) {
   const length = buffered.readUInt32LE(0);
   if (!length || length > MAX_BYTES) throw new Error('Invalid service frame.');
   if (buffered.length < length + 4) break;
   const body = buffered.subarray(4, length + 4);
   const message = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(body));
   buffered = buffered.subarray(length + 4);
   if (!message || typeof message !== 'object' || Array.isArray(message) || message.version !== VERSION) throw new Error('Incompatible Finley service. Ask an administrator to repair Finley.');
   receive(message);
  }
  if (buffered.length > MAX_BYTES + 4) throw new Error('Invalid service frame.');
 };
}
function serviceError(code, message) { const error = new Error(message); error.code = code; return error; }
function validateStatus(value) {
 if (!value || !['disconnected', 'connecting', 'connected', 'disconnecting', 'busy', 'error'].includes(value.state) || typeof value.owned !== 'boolean' || typeof value.cleanupConfirmed !== 'boolean' || typeof value.engineReady !== 'boolean' || !Number.isSafeInteger(value.revision) || value.revision < 0 || (value.errorCode !== null && typeof value.errorCode !== 'string')) throw new Error('Invalid Finley service status.');
 return value;
}
function createServiceClient({ clientPath, onStatus = () => {}, onFailure = () => {}, spawnClient = spawn } = {}) {
 let child = null, starting = null, heartbeat = null, nextId = 0, generation = 0, revision = -1;
 const pending = new Map();
 const unavailable = () => serviceError('SERVICE_UNAVAILABLE', 'Finley Tunnel Service is unavailable. Ask an administrator to install or repair Finley.');
 function end(error, notify = true) {
  generation++; const instance = child; child = null; starting = null; revision = -1;
  clearInterval(heartbeat); heartbeat = null;
  for (const request of pending.values()) { clearTimeout(request.timer); request.reject(error); }
  pending.clear();
  if (instance) { instance.stdin.destroy(); instance.kill(); }
  if (notify) onFailure(error);
 }
 function acceptStatus(value) {
  validateStatus(value);
  if (value.revision >= revision) { revision = value.revision; onStatus(value); }
 }
 function send(method, fields = {}, timeout = 5000) {
  if (!child || pending.size >= 4) return Promise.reject(unavailable());
  const id = String(++nextId), instance = child;
  return new Promise((resolve, reject) => {
   const timer = setTimeout(() => end(serviceError('SERVICE_TIMEOUT', 'The Finley service did not respond. Cleanup is unconfirmed; check Windows network settings.')), timeout);
   pending.set(id, { resolve, reject, timer });
   try {
    if (!instance.stdin.write(frame({ version: VERSION, id, method, ...fields }))) {
     // At most four bounded requests may be buffered; no unbounded producer exists.
     if (instance.stdin.writableLength > MAX_BYTES * 4 + 16) throw unavailable();
    }
   } catch (error) { end(error); }
  });
 }
 function open() {
  if (starting) return starting;
  if (child) return Promise.resolve();
  if (!clientPath || !fs.existsSync(clientPath)) return Promise.reject(unavailable());
  const current = ++generation; revision = -1;
  try { child = spawnClient(clientPath, [], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] }); }
  catch { return Promise.reject(unavailable()); }
  const instance = child;
  const decode = createDecoder(message => {
   if (current !== generation) return;
   if (message.id === 'bridge' && message.ok === false) throw serviceError(message.error?.code || 'SERVICE_UNAVAILABLE', message.error?.message || unavailable().message);
   if (message.event === 'status') { acceptStatus(message.value); return; }
   const request = pending.get(message.id);
   if (!request || typeof message.ok !== 'boolean') throw new Error('Invalid Finley service response.');
   if (message.ok) acceptStatus(message.value);
   pending.delete(message.id); clearTimeout(request.timer);
   if (message.ok) request.resolve(message.value);
   else request.reject(serviceError(message.error?.code || 'SERVICE_FAILURE', message.error?.message || 'The Finley service could not complete the request.'));
  });
  instance.stdout.on('data', bytes => { if (current === generation) { try { decode(bytes); } catch (error) { end(error); } } });
  instance.stdin.on('error', () => { if (current === generation) end(unavailable()); });
  instance.once('error', () => { if (current === generation) end(unavailable()); });
  instance.once('exit', () => { if (current === generation) end(unavailable()); });
  starting = send('hello', {}, 10000).then(() => {
   if (current !== generation) throw unavailable();
   heartbeat = setInterval(() => { void send('getStatus').catch(error => { if (current === generation) end(error); }); }, 3000);
   heartbeat.unref(); starting = null;
  }).catch(error => { if (current === generation) end(error); throw error; });
  return starting;
 }
 return Object.freeze({
  async request(method, fields) { await open(); return send(method, fields, method === 'connect' ? 155000 : method === 'disconnect' ? 30000 : 5000); },
  close() { end(serviceError('CLIENT_CLOSED', 'Finley service connection closed.'), false); }
 });
}
module.exports = { createServiceClient, frame, createDecoder, validateStatus };
