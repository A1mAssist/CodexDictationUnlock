// node tests/config-client-bridge.mjs <built-dll> [current-app-shared.js]
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
import {fileURLToPath} from 'node:url';

const old = 'function Dm(e,t){let n=e.get(Om);if(n==null)throw Error(`AppServerManager RPC is not connected`);return n.forHost(t)}';
const current = 'function KI(e,t){try{var n=GI();let r=e.get(HI,t),i=e.get(qI);if(i!=null&&r.status!==`disconnected`&&(r.status!==`unavailable`||!e.get(BI).includes(t)))try{return i.forHost(t)}catch{}let{client:a}=r;if(a!=null)return a.rpc;let o=e.get(JI,t);if(o!=null)return o;return deferred}catch(e){n.e=e}finally{n.d()}}';
const sources = [old, current];
if (process.argv[3]) sources.push(fs.readFileSync(process.argv[3], 'utf8'));
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cdu-config-bridge-'));
try {
  const fixture = path.join(dir, 'fixtures.json');
  fs.writeFileSync(fixture, JSON.stringify(sources));
  const patched = JSON.parse(execFileSync('pwsh', ['-NoProfile', '-File', fileURLToPath(new URL('./config-client-patch.ps1', import.meta.url)), '-AssemblyPath', path.resolve(process.argv[2]), '-FixturePath', fixture], {encoding:'utf8',maxBuffer:32*1024*1024,timeout:30000}));
  const rpc = {sendRequest(){}};
  for (const route of ['manager', 'direct', 'cached', 'deferred']) {
    let disposed = 0;
    const context = {HI:'state',qI:'manager',BI:'hosts',JI:'cached',deferred:rpc,GI:()=>({d(){disposed++;}})};
    vm.createContext(context);
    vm.runInContext(patched[1], context);
    const scope = {get(key) {
      if (key === 'state') return {status:'connected',client:route === 'direct' ? {rpc} : null};
      if (key === 'manager') return {forHost(){if(route === 'manager') return rpc; throw Error('unavailable');}};
      if (key === 'cached') return route === 'cached' ? rpc : null;
      if (key === 'hosts') return [];
    }};
    assert.equal(context.KI(scope, 'local'), rpc, route);
    assert.equal(context.__CODEX_DICTATION_APP_SERVERS__.get('local'), rpc, route);
    assert.equal(disposed, 1, `${route} disposal`);
    context.KI(scope, 'remote');
    assert.equal(context.__CODEX_DICTATION_APP_SERVERS__.get('local'), rpc);
    assert.equal(context.__CODEX_DICTATION_APP_SERVERS__.get('remote'), rpc);
  }
  const context = {Om:'manager'}; vm.createContext(context); vm.runInContext(patched[0], context);
  assert.equal(context.Dm({get:()=>({forHost:()=>rpc})}, 'local'), rpc);
  assert.equal(context.__CODEX_DICTATION_APP_SERVERS__.get('local'), rpc);
  if (patched[2]) {
    assert.equal(patched[2].split('let codexDictationClient=(()=>').length - 1, 1, 'current bundle matches exactly once');
    const bundle = path.join(dir, 'patched.mjs'); fs.writeFileSync(bundle, patched[2]);
    execFileSync(process.execPath, ['--check', bundle]);
  }
  console.log('Config bridge passed: old entry, all four new return paths, disposal, host isolation, and optional current-bundle syntax.');
} finally { fs.rmSync(dir, {recursive:true,force:true}); }
