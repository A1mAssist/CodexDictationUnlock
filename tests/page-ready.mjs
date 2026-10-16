// Run: node tests/page-ready.mjs <path-to-CodexDictation.dll>
// 验证 WaitForPageLoadedAsync：readyState=complete 时立刻放行，卡在 loading 时按预算封顶返回。
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const profile = await mkdtemp(join(tmpdir(), 'cdu-pageready-'));
const hanging = [];
const server = createServer((req, res) => {
  if (req.url === '/hang-image') { hanging.push(res); return; }
  res.setHeader('Content-Type', 'text/html');
  res.end(req.url === '/loading' ? '<img src="/hang-image">' : '<p>ready</p>');
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const chrome = spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', ['--headless=new', '--no-first-run', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
let ws;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const until = async fn => {
  for (let i = 0; i < 100; i++) { try { const value = await fn(); if (value) return value; } catch {} await delay(100); }
  throw Error('Test timed out');
};
const runGate = (socketUrl, seconds) => {
  const out = execFileSync('pwsh', ['-NoProfile', '-File', fileURLToPath(new URL('./page-ready-host.ps1', import.meta.url)), '-AssemblyPath', resolve(process.argv[2]), '-SocketUrl', socketUrl, '-BudgetSeconds', String(seconds)], { encoding: 'utf8', timeout: 60000 });
  return Number(/elapsed=([\d.]+)/.exec(out)?.[1]);
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

  await send('Page.navigate', { url:`${origin}/ready` });
  await until(async () => await evaluate('document.readyState') === 'complete');
  const ready = runGate(target.webSocketDebuggerUrl, 10);
  assert.ok(ready < 5, `loaded page should pass immediately, took ${ready}s`);

  await send('Page.navigate', { url:`${origin}/loading` });
  await until(async () => await evaluate(`!!document.querySelector('img')`));
  assert.notEqual(await evaluate('document.readyState'), 'complete');
  const stuck = runGate(target.webSocketDebuggerUrl, 3);
  assert.ok(stuck >= 2.5, `loading page must wait out the budget, took ${stuck}s`);
  assert.ok(stuck < 8, `budget must cap the wait, took ${stuck}s`);

  console.log(`Page-ready gate passed: loaded=${ready}s, loading=${stuck}s.`);
  await send('Browser.close');
} finally {
  ws?.close();
  chrome.kill();
  for (const res of hanging) res.destroy();
  server.close();
  await delay(500);
  await rm(profile, { recursive:true, force:true, maxRetries:5, retryDelay:200 });
}
