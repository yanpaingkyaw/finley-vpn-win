'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { createCandidateConfig } = require('../electron/network-config.cjs');
const profile = { server: 'vpn.example', port: 443, method: 'aes-256-gcm', password: 'test-only' };
test('candidate config routes non-bypass traffic through the proxy', () => {
 const c = createCandidateConfig(profile, {enabled: false, paths: ['C:\\Apps\\test.exe']});
 assert.equal(c.route.final, 'proxy');
 assert.equal(c.route.auto_detect_interface, true);
 assert.equal(c.route.rules.some(r => r.process_path || r.process_path_regex), false);
 assert.equal(c.outbounds[0].password, 'test-only');
 assert.equal(c.log.disabled, true);
});
test('candidate config only adds requested bypass paths when enabled', () => {
 const c = createCandidateConfig(profile, {enabled: true, paths: ['C:\\Apps\\test.exe']});
 const rule = c.route.rules.find(r => r.process_path_regex);
 assert.ok(rule);
 const expression = new RegExp(rule.process_path_regex[0].replace('(?i)', ''), 'i');
 assert.ok(expression.test('c:\\apps\\TEST.exe'));
 assert.ok(!expression.test('C:\\Apps\\testXexe'));
 assert.ok(!expression.test('C:\\Apps\\other-test.exe'));
 const dns = c.route.rules.findIndex(r => r.port === 53 && r.action === 'hijack-dns');
 assert.ok(dns >= 0 && dns < c.route.rules.indexOf(rule));
});
test('invalid inputs fail without disclosing the password', () => {
 assert.throws(() => createCandidateConfig({...profile, port: 70000}, {enabled: false, paths: []}), e => !e.message.includes('test-only'));
 assert.throws(() => createCandidateConfig(profile, {enabled: true, paths: ['relative.exe']}));
});

test('tunnel rejects unsupported IPv6 and gives an empty AAAA answer', () => {
 const c = createCandidateConfig(profile, {enabled: false, paths: []});
 assert.equal(c.inbounds[0].mtu, 1400);
 assert.deepEqual(c.dns.rules, [{query_type:'AAAA',action:'predefined',rcode:'NOERROR'}]);
 assert.ok(c.route.rules.some(rule => rule.inbound === 'tun-in' && rule.ip_version === 6 && rule.action === 'reject'));
 assert.ok(c.inbounds[0].address.some(x => x.includes('::')));
});


test('domain suffix and network rules are included only when enabled', () => {
 const c = createCandidateConfig(profile, {enabled:true, paths:[], domains:['Example.COM'], cidrs:['192.0.2.99/24']});
 assert.ok(c.route.rules.some(r => r.action === 'sniff' && r.inbound === 'tun-in'));
 assert.ok(c.route.rules.some(r => r.domain_suffix?.[0] === 'example.com' && r.outbound === 'direct'));
 assert.ok(c.route.rules.some(r => r.ip_cidr?.[0] === '192.0.2.0/24' && r.outbound === 'direct'));
 assert.notEqual(c.dns.reverse_mapping, true);
 const disabled = createCandidateConfig(profile, {enabled:false, paths:[], domains:['example.com'], cidrs:['192.0.2.0/24']});
 assert.ok(!disabled.route.rules.some(r => r.outbound === 'direct' && (r.domain_suffix || r.ip_cidr)));
});

test('tunnel addresses are rejected before direct routes', () => {
 const c = createCandidateConfig(profile, {enabled:true,paths:[],domains:[],cidrs:['0.0.0.0/0']});
 const block = c.route.rules.findIndex(r => r.action === 'reject' && r.ip_cidr?.includes('172.19.0.0/30'));
 const direct = c.route.rules.findIndex(r => r.outbound === 'direct');
 assert.ok(block >= 0 && block < direct);
});

test('uses the discovered interface for direct traffic', () => {
 const c = createCandidateConfig(profile, {enabled:true,paths:[],domains:['example.com'],cidrs:[]}, 'Wi-Fi');
 assert.equal(c.outbounds.find(o => o.tag === 'direct').bind_interface, 'Wi-Fi');
 assert.throws(() => createCandidateConfig(profile, {enabled:true,paths:[],domains:[],cidrs:[]}, 'Bad\nInterface'));
});
