'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const root = path.join(__dirname,'..');
const helper = path.join(root,'bin','finley-tunnel-helper.exe');
const fake = path.join(__dirname,'fixtures','fake-engine.exe');
async function run(stopWithEof) {
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'finley-helper-')); const marker=path.join(dir,'marker');
 try {
  const child=spawn(helper,[fake],{windowsHide:true,stdio:['pipe','pipe','pipe']}); let output=''; child.stdout.on('data',b=>output+=b.toString());
  const done=new Promise((resolve,reject)=>{ child.on('error',reject); child.on('exit',(code)=>resolve(code)); });
  child.stdin.write(marker+'\n');
  const start=Date.now(); while (!fs.existsSync(marker)) { if(Date.now()-start>10000) {child.kill(); throw Error('Fake engine did not start: '+output);} await new Promise(r=>setTimeout(r,50)); }
  if(stopWithEof) child.stdin.end(); else child.stdin.write('STOP\n');
  const code=await Promise.race([done,new Promise((_,reject)=>setTimeout(()=>reject(Error('Helper did not stop')),15000))]);
  assert.equal(code,0,output); assert.equal(fs.readFileSync(marker,'utf8'),'stopped'); assert.match(output,/READY/); assert.match(output,/EXIT 0/);
 } finally { fs.rmSync(dir,{recursive:true,force:true}); }
}
test('supervisor stops child gracefully on STOP', {skip:process.platform!=='win32',timeout:20000},async()=>run(false));
test('supervisor stops child gracefully on parent pipe EOF', {skip:process.platform!=='win32',timeout:20000},async()=>run(true));
test('real sing-box exits on graceful STOP without changing routes', {skip:process.platform!=='win32',timeout:20000}, async () => {
 const net=require('node:net'); const engine=path.join(root,'bin','sing-box.exe');
 const server=net.createServer(); await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
 const port=server.address().port; await new Promise(resolve=>server.close(resolve));
 const child=spawn(helper,[engine],{windowsHide:true,stdio:['pipe','pipe','pipe']}); let output=''; child.stdout.on('data',b=>output+=b.toString());
 const done=new Promise((resolve,reject)=>{child.on('error',reject);child.on('exit',code=>resolve(code));});
 child.stdin.write(JSON.stringify({log:{disabled:true},inbounds:[{type:'socks',listen:'127.0.0.1',listen_port:port}],outbounds:[{type:'direct',tag:'direct'}],route:{final:'direct'}})+'\n');
 try {
  const start=Date.now(); let listening=false;
  while(Date.now()-start<10000&&!listening) {
   listening=await new Promise(resolve=>{const s=net.connect(port,'127.0.0.1');s.once('connect',()=>{s.destroy();resolve(true);});s.once('error',()=>resolve(false));});
   if(!listening) await new Promise(r=>setTimeout(r,50));
  }
  assert.equal(listening,true,'engine did not listen: '+output);
  child.stdin.write('STOP\n'); const code=await done;
  assert.equal(code,0,output); assert.match(output,/EXIT 0/); assert.doesNotMatch(output,/CLEANUP_UNCONFIRMED/);
 } finally { if(child.exitCode===null) child.kill(); }
});
