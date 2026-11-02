'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const { createCandidateConfig } = require('../electron/network-config.cjs');
const { normalizeBypass } = require('../electron/bypass.cjs');
const harness = path.join(__dirname, 'fixtures', 'service-harness.exe');
const windows = { skip: process.platform !== 'win32' };
const profile = { server: 'vpn.example', port: 443, method: 'aes-256-gcm', password: 'test-only' };
const bypass = { enabled: false, paths: [], domains: [], cidrs: [] };
function run(input) { return spawnSync(harness, [], { input: typeof input === 'string' ? input : JSON.stringify(input), encoding: 'utf8', timeout: 10000, windowsHide: true }); }

test('native configuration preserves the current networking policy for every cipher and bypass form', windows, () => {
 for (const method of ['aes-128-gcm', 'aes-192-gcm', 'aes-256-gcm', 'chacha20-ietf-poly1305', 'xchacha20-ietf-poly1305']) {
  for (const rules of [bypass, { enabled: true, paths: ['C:\\Apps\\a+b (test)[1].exe', 'c:\\apps\\A+B (test)[1].exe'], domains: ['Example.COM', 'bücher.example'], cidrs: ['192.0.2.99/24', '0.0.0.0/0', '255.255.255.255/32'] }]) {
   for (const directInterface of [undefined, 'Wi-Fi']) {
    const request = { operation: 'config', profile: { ...profile, method }, bypass: rules, directInterface };
    const result = run(request); assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), createCandidateConfig(request.profile, rules, directInterface));
   }
  }
 }
});
test('native bypass normalization matches the JavaScript validator', windows, () => {
 const input = { enabled: true, paths: ['C:\\Apps\\..\\Tools\\test.exe'], domains: ['example.com.', 'bücher.example'], cidrs: ['192.0.2.123/25', '10.0.0.1'] };
 const result = run({ operation: 'normalize', bypass: input }); assert.equal(result.status, 0, result.stderr);
 assert.deepEqual(JSON.parse(result.stdout), normalizeBypass(input.enabled, input.paths, input.domains, input.cidrs));
});
test('native validation rejects unsafe configuration and unknown fields without disclosing secrets', windows, () => {
 for (const changes of [{ port: 70000 }, { port: '443' }, { method: 'rc4' }, { server: 'bad\nhost' }, { server: '../engine.exe' }, { executable: 'C:\\evil.exe' }, { password: '' }]) {
  const result = run({ operation: 'config', profile: { ...profile, ...changes }, bypass });
  assert.equal(result.status, 1); assert.match(result.stderr, /INVALID_REQUEST/); assert.doesNotMatch(result.stderr + result.stdout, /test-only/);
 }
 for (const rules of [{ ...bypass, rawConfig: {} }, { ...bypass, paths: ['relative.exe'] }, { ...bypass, paths: ['\\\\?\\C:\\evil.exe'] }, { ...bypass, domains: ['*.example.com'] }, { ...bypass, cidrs: ['::/0'] }]) {
  assert.equal(run({ operation: 'config', profile, bypass: rules }).status, 1);
 }
});
test('native JSON parser rejects duplicates, trailing values, invalid numbers and deep nesting', windows, () => {
 for (const input of ['{"operation":"parse","x":1,"x":2}', '{"operation":"parse"}{}', '{"operation":"parse","x":01}', '{"operation":"parse","x":NaN}', '{"operation":"parse","x":"bad\nstring"}', '{"operation":"parse","x":' + '['.repeat(22) + '0' + ']'.repeat(22) + '}']) assert.equal(run(input).status, 1, input);
});
test('native pipes authenticate the actual user, reject spoofed servers and reject invalid connection settings', { ...windows, timeout: 20000 }, () => {
 const result = spawnSync(harness, ['--pipes'], { encoding: 'utf8', timeout: 18000, windowsHide: true }); assert.equal(result.status, 0, result.stderr);
});
test('native Job Object contains the engine after the helper crashes', { ...windows, timeout: 15000 }, () => {
 const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'finley-job-'));
 try {
  const result = spawnSync(harness, ['--job', path.resolve(__dirname, '../bin/finley-tunnel-helper.exe'), path.join(__dirname, 'fixtures/fake-engine.exe'), path.join(dir, 'marker')], { encoding: 'utf8', timeout: 12000, windowsHide: true });
  assert.equal(result.status, 0, result.stderr);
 } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
test('native lifecycle serializes ownership, cancels stale startup, handles logoff and blocks unsafe reconnection', windows, () => {
 const result = spawnSync(harness, ['--lifecycle'], { encoding: 'utf8', timeout: 15000, windowsHide: true }); assert.equal(result.status, 0, result.stderr);
});
test('service registration accepts an absent service despite unrelated filesystem errors and rejects invalid handles', windows, () => {
 const result = spawnSync(harness, ['--service-lookup'], { encoding: 'utf8', timeout: 10000, windowsHide: true });
 assert.equal(result.status, 0, result.stderr);
 const evidence = JSON.parse(result.stdout);
 assert.equal(evidence.missingServiceError, 1060);
 assert.notEqual(evidence.filesystemError, 1060);
 assert.equal(evidence.missingServiceAccepted, true);
});
test('interactive clients can verify a restricted service process without memory or termination access', windows, () => {
 const result = spawnSync(harness, ['--process-query'], { encoding: 'utf8', timeout: 15000, windowsHide: true });
 assert.equal(result.status, 0, result.stderr);
});
