'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { PassThrough } = require('node:stream');
const { createServiceClient, frame, createDecoder, validateStatus } = require('../electron/service-client.cjs');
const { createNetworkController } = require('../electron/network.cjs');
const state = (revision = 0, changes = {}) => ({ state: 'disconnected', owned: false, cleanupConfirmed: true, revision, errorCode: null, engineReady: true, ...changes });
function fakeBridge(handle) {
 const child = new EventEmitter(); child.stdin = new PassThrough(); child.stdout = new PassThrough(); child.killed = false;
 child.kill = () => { child.killed = true; child.emit('exit', 0); };
 child.stdin.on('data', createDecoder(request => handle(request, value => child.stdout.write(frame(value)), child)));
 return child;
}
const response = (request, value = state()) => ({ version: 1, id: request.id, ok: true, value });
test('service frames decode fragmented headers/bodies and coalesced messages', () => {
 const received = [], decode = createDecoder(value => received.push(value));
 const one = { version: 1, id: '1', value: 'bücher' }, two = { version: 1, id: '2' };
 const bytes = Buffer.concat([frame(one), frame(two)]);
 for (const byte of bytes) decode(Buffer.from([byte]));
 assert.deepEqual(received, [one, two]);
 const batched = []; createDecoder(value => batched.push(value))(bytes); assert.deepEqual(batched, [one, two]);
});
test('service frames reject empty/oversized bodies, malformed JSON, bad UTF-8 and version mismatch', () => {
 for (const length of [0, 1024 * 1024 + 1, 0xffffffff]) { const header = Buffer.alloc(4); header.writeUInt32LE(length); assert.throws(() => createDecoder(() => {})(header)); }
 for (const bytes of [Buffer.from('bad json'), Buffer.from([0xff])]) { const header = Buffer.alloc(4); header.writeUInt32LE(bytes.length); assert.throws(() => createDecoder(() => {})(Buffer.concat([header, bytes]))); }
 assert.throws(() => createDecoder(() => {})(frame({ version: 2 })), /Incompatible/);
 assert.throws(() => validateStatus({ ...state(), state: 'invented' }));
});
test('client completes handshake before forwarding a connection and correlates out-of-order responses', async () => {
 const requests = [], events = []; let replyHello, send, bridge;
 const client = createServiceClient({ clientPath: process.execPath, onStatus: value => events.push(value), spawnClient() {
  bridge = fakeBridge((request, write) => { requests.push(request); send = write; if (request.method === 'hello') replyHello = () => write(response(request)); }); return bridge;
 } });
 try {
  const connect = client.request('connect', { profile: { password: 'test-only' } });
  assert.equal(requests.length, 1); assert.equal(requests[0].method, 'hello'); assert.equal(JSON.stringify(requests).includes('test-only'), false);
  replyHello(); await new Promise(resolve => setImmediate(resolve));
  const status = client.request('getStatus'); await new Promise(resolve => setImmediate(resolve));
  const connectRequest = requests.find(request => request.method === 'connect'), statusRequest = requests.find(request => request.method === 'getStatus');
  send(response(statusRequest, state(2, { state: 'connected', owned: true })));
  send(response(connectRequest, state(1, { state: 'connecting', owned: true })));
  await Promise.all([connect, status]);
  assert.equal(events.at(-1).revision, 2);
  send({ version: 1, event: 'status', value: state(3, { state: 'error', errorCode: 'ENGINE_EXIT' }) });
  assert.equal(events.at(-1).errorCode, 'ENGINE_EXIT');
 } finally { client.close(); }
 assert.equal(bridge.killed, true);
});
test('client reports native bridge authentication failure and never forwards credentials', async () => {
 const requests = [], failures = [];
 const client = createServiceClient({ clientPath: process.execPath, onFailure: error => failures.push(error.code), spawnClient: () => fakeBridge((request, send) => {
  requests.push(request); send({ version: 1, id: 'bridge', ok: false, error: { code: 'UNAUTHORIZED', message: 'Service identity mismatch.' } });
 }) });
 await assert.rejects(client.request('connect', { profile: { password: 'test-only' } }), /identity mismatch/);
 assert.deepEqual(requests.map(request => request.method), ['hello']); assert.deepEqual(failures, ['UNAUTHORIZED']); client.close();
});
test('bridge exit rejects all outstanding requests and a later connection starts a new generation', async () => {
 let bridge, launches = 0; const statuses = [];
 const client = createServiceClient({ clientPath: process.execPath, onStatus: value => statuses.push(value), spawnClient: () => {
  launches++; bridge = fakeBridge((request, send) => { if (request.method === 'hello') send(response(request)); }); return bridge;
 } });
 try {
  const request = client.request('getStatus'); await new Promise(resolve => setImmediate(resolve)); bridge.emit('exit', 1);
  await assert.rejects(request, /Service is unavailable/);
  const next = client.request('getStatus'); await new Promise(resolve => setImmediate(resolve)); bridge.emit('exit', 1);
  await assert.rejects(next, /Service is unavailable/); assert.equal(launches, 2); assert.equal(statuses.length, 2);
 } finally { client.close(); }
});
test('service rejection preserves its stable code and does not drop a healthy bridge', async () => {
 let bridge;
 const client = createServiceClient({ clientPath: process.execPath, spawnClient: () => {
  bridge = fakeBridge((request, send) => request.method === 'hello' ? send(response(request)) : send({ version: 1, id: request.id, ok: false, error: { code: 'VPN_IN_USE', message: 'VPN in use.' } })); return bridge;
 } });
 try { await assert.rejects(client.request('connect'), error => error.code === 'VPN_IN_USE'); assert.equal(bridge.killed, false); }
 finally { client.close(); }
});
test('controller maps another session and service loss without claiming successful cleanup', async () => {
 let handlers; const changes = [];
 const controller = createNetworkController({ onStatus: value => changes.push(value), clientFactory: options => { handlers = options; return { request: async () => {}, close() {} }; } });
 handlers.onStatus(state(1, { state: 'busy' })); assert.match(controller.getStatus().error, /another app or Windows session/);
 handlers.onStatus(state(2, { state: 'connected', owned: true })); assert.equal(controller.getStatus().state, 'connected');
 handlers.onFailure(new Error('Service connection lost.')); assert.equal(controller.getStatus().cleanupConfirmed, false); assert.equal(controller.getStatus().state, 'error');
 assert.equal(changes.length, 3); controller.close();
});
test('disconnect during initial service discovery cancels connection before any credentials are sent', async () => {
 let finishDiscovery; const requests = [];
 const controller = createNetworkController({ clientFactory: options => ({
  request(method) {
   requests.push(method);
   if (method === 'getStatus') return new Promise(resolve => { finishDiscovery = () => { options.onStatus(state()); resolve(state()); }; });
   return Promise.resolve(state());
  }, close() {}
 }) });
 const connecting = controller.connect({ password: 'test-only' }, {});
 await controller.disconnect(); finishDiscovery();
 await assert.rejects(connecting, /cancelled/);
 assert.deepEqual(requests, ['getStatus', 'disconnect']); controller.close();
});
test('controller rejects a successful RPC response whose cleanup state remains unconfirmed', async () => {
 let handlers;
 const controller = createNetworkController({ clientFactory: options => { handlers = options; return { request: async () => state(2, { state: 'error', cleanupConfirmed: false }), close() {} }; } });
 handlers.onStatus(state(1, { state: 'connected', owned: true }));
 await assert.rejects(controller.disconnect(), /cleanup could not be confirmed/);
 controller.close();
});
