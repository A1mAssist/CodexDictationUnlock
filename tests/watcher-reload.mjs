// Run: node tests/watcher-reload.mjs <path-to-CodexDictation.dll>
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const profile = await mkdtemp(join(tmpdir(), 'cdu-reload-'));
const source = 'const bridge={dictationStreaming:{native:true}};let o=bridge?.dictationStreaming,s;globalThis.transport=o;async function connect(){return(await client.getInstance().post(`/codex/dictation-stream-connect-info`,void 0)).body}';
const server = createServer((req, res) => {
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('Content-Type', req.url.startsWith('/app-initial-') ? 'text/javascript' : 'text/html');
  res.end(req.url.startsWith('/app-initial-') ? source : '<script src="/app-initial-fixture.js"></script>');
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const chrome = spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', ['--headless=new', '--no-first-run', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
let host, ws;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const until = async fn => {
  for (let i = 0; i < 100; i++) { try { const value = await fn(); if (value) return value; } catch {} await delay(100); }
  throw Error('Test timed out');
};
try {
  const port = await until(async () => Number((await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]));
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const target = pages.find(p => p.type === 'page');
  ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  let id = 0;
  const pending = new Map();
  ws.onmessage = ({ data }) => { const m = JSON.parse(data); if (pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
  const send = (method, params = {}) => new Promise(resolve => { const key = ++id; pending.set(key, resolve); ws.send(JSON.stringify({ id:key, method, params })); });
  const evaluate = async expression => (await send('Runtime.evaluate', { expression, returnByValue:true })).result?.result?.value;
  await send('Page.navigate', { url:`http://127.0.0.1:${server.address().port}/` });
  await until(() => evaluate('globalThis.transport?.native'));
  host = spawn('pwsh', ['-NoProfile', '-File', fileURLToPath(new URL('./watcher-host.ps1', import.meta.url)), '-AssemblyPath', resolve(process.argv[2]), '-SocketUrl', target.webSocketDebuggerUrl], { windowsHide:true, stdio:['ignore','pipe','pipe'] });
  let errors = '';
  host.stderr.on('data', data => { errors += data; });
  await until(() => evaluate('globalThis.__CDU_RELOAD_TEST__'));
  // Runtime.evaluate completes before Fetch.enable; allow the host to finish registering interception.
  await delay(500);
  for (let reload = 0; reload < 2; reload++) {
    await send('Page.reload', { ignoreCache:true });
    try { await until(() => evaluate('globalThis.transport===null && globalThis.__CDU_RELOAD_TEST__===true')); }
    catch { throw Error(JSON.stringify({reload,state:await evaluate('({marker:globalThis.__CDU_RELOAD_TEST__,transport:globalThis.transport})'),errors})); }
    assert.equal(await evaluate('connect.toString().includes("__CODEX_DICTATION_CONNECT_INFO__")'), true);
  }
  assert.equal(errors, '');
  console.log('Watcher reload test passed: runtime and transport patches survive two reloads.');
  await send('Browser.close');
} finally {
  ws?.close();
  host?.kill();
  chrome.kill();
  server.close();
  await delay(500);
  await rm(profile, {recursive:true,force:true,maxRetries:5,retryDelay:200});
}
