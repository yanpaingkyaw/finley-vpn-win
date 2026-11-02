'use strict';
const test=require('node:test');const assert=require('node:assert/strict');
const {verifyTunnelRoutes}=require('../electron/network.cjs');
test('verifies both IPv4 halves and accepts interface names case-insensitively',async()=>{const seen=[];await verifyTunnelRoutes('Finley-ABC',async address=>{seen.push(address);return 'finley-abc';});assert.deepEqual(seen,['9.9.9.9','200.1.1.1']);});
test('explains a competing route rather than misdiagnosing administrator access',async()=>{await assert.rejects(verifyTunnelRoutes('Finley-abc',async address=>address==='9.9.9.9'?'Finley-abc':'outline-tap0'),e=>e.message.includes('outline-tap0')&&e.message.includes('other VPN')&&!e.message.includes('administrator'));});
test('does not report connected when route lookup fails',async()=>{await assert.rejects(verifyTunnelRoutes('Finley-abc',async()=>{throw new Error('Could not check Windows network routes.');}),/Could not check/);});
