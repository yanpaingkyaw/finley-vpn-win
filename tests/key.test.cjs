'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { parseKey } = require('../electron/key.cjs');
const { createNetworkController } = require('../electron/network.cjs');
const b64 = s => Buffer.from(s).toString('base64url');
test('parses standard encoded user info and a label', () => {
 const p = parseKey(`ss://${b64('aes-256-gcm:secret')}@vpn.example:8388#My%20server`);
 assert.deepEqual(p, {server:'vpn.example',port:8388,method:'aes-256-gcm',password:'secret',label:'My server'});
});
test('parses encoded full key and passwords with colon', () => {
 const p = parseKey(`ss://${b64('chacha20-ietf-poly1305:pass:word@vpn.example:80')}`);
 assert.equal(p.password,'pass:word'); assert.equal(p.port,80);
});
test('parses percent encoded plain credentials', () => {
 const p = parseKey('ss://aes-128-gcm:p%40ss@vpn.example:443');
 assert.equal(p.password,'p@ss');
});
test('rejects invalid and unsupported access keys', () => {
 for (const key of ['https://example.com/config','ssconf://example.com','ss://invalid','ss://aes-256-gcm:pw@host:99999','ss://rc4:pw@host:443','ss://aes-256-gcm:pw@host:443/?plugin=test']) assert.throws(() => parseKey(key));
});
test('missing service client never reports a connection', async () => {
 const n = createNetworkController();
 await assert.rejects(n.connect(), /Service is unavailable/);
 assert.equal(n.getStatus().state, 'disconnected');
 assert.equal((await n.disconnect()).state, 'disconnected');
});
