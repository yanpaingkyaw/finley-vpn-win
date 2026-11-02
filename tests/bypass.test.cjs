'use strict';
const test=require('node:test');const assert=require('node:assert/strict');const {normalizeBypass}=require('../electron/bypass.cjs');
test('normalizes and deduplicates names, paths and IPv4 networks',()=>{
 const r=normalizeBypass(true,['C:\\Apps\\A.exe','c:\\apps\\a.exe'],['Example.COM.','example.com','BÜCHER.example'],['192.0.2.42/24','192.0.2.0/24','198.51.100.5']);
 assert.equal(r.paths.length,1);assert.deepEqual(r.domains,['example.com','xn--bcher-kva.example']);assert.deepEqual(r.cidrs,['192.0.2.0/24','198.51.100.5/32']);
});
test('rejects invalid domains and unsupported IP input',()=>{
 for (const d of ['https://example.com','*.example.com','example.com:443','bad..example','127.0.0.1','-x.example']) assert.throws(()=>normalizeBypass(true,[],[d],[]));
 for (const r of ['192.0.2.3/33','192.0.2.3/-1','192.0.2.3/01','256.1.1.1','2001:db8::/32']) assert.throws(()=>normalizeBypass(true,[],[],[r]));
});
test('legacy settings remain compatible and IPv4 prefix boundaries normalize',()=>{
 assert.deepEqual(normalizeBypass(false,[]),{enabled:false,paths:[],domains:[],cidrs:[]});
 assert.deepEqual(normalizeBypass(true,[],[],['10.1.2.3/0','10.1.2.3/32']).cidrs,['0.0.0.0/0','10.1.2.3/32']);
});
