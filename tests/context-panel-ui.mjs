// Run: node tests/context-panel-ui.mjs <cdp-port> [sample-json]
// Injects context-panel.js into the running Codex page, checks the panel, saves a cropped screenshot, then removes it.
import assert from 'node:assert/strict';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createServer } from 'node:http';
import { resolve } from 'node:path';

const port = Number(process.argv[2]);
if (!port) throw new Error("Usage: node tests/context-panel-ui.mjs <codex-cdp-port> [sample-json]");
const samplePath = resolve(process.argv[3] || 'artifacts/context-sample.json');
const sample = JSON.parse(await readFile(samplePath, 'utf8'));
let source = await readFile('context-panel.js', 'utf8');

let disabled = false;
let variantMode = false;
const variant = JSON.parse(JSON.stringify(sample));
for (const band of variant.bands) { if (band.key === 'tool') band.pct = 30; if (band.key === 'free') band.pct = 27; }
const server = createServer((req, res) => {
  res.setHeader('Access-Control-Allow-Origin', req.headers.origin || '*');
  res.setHeader('Vary', 'Origin');
  res.setHeader('Access-Control-Allow-Methods', 'GET, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
  res.setHeader('Access-Control-Allow-Private-Network', 'true');
  res.setHeader('Cache-Control', 'no-store');
  if (req.method === 'OPTIONS') { res.writeHead(204); res.end(); return; }
  const url = new URL(req.url, 'http://127.0.0.1');
  if (url.pathname === '/__disabled') { disabled = url.searchParams.get('v') === '1'; res.writeHead(204); res.end(); return; }
  if (url.pathname === '/__variant') { variantMode = url.searchParams.get('v') === '1'; res.writeHead(204); res.end(); return; }
  res.setHeader('Content-Type', 'application/json');
  if (url.pathname === '/context' && disabled) { res.writeHead(404); res.end('{}'); return; }
  res.end(JSON.stringify(variantMode && url.pathname === '/context' ? variant : sample));
});
await new Promise(resolveListen => server.listen(0, '127.0.0.1', resolveListen));
source = source.replace('__HELPER_CONFIG__', JSON.stringify({ url: `http://127.0.0.1:${server.address().port}/config?token=test` }));

const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
const target = targets.find(item => item.type === 'page' && item.url.startsWith('app://-/index.html'));
assert.ok(target, 'Codex app page not found');
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolveOpen, rejectOpen) => { ws.onopen = resolveOpen; ws.onerror = rejectOpen; });
let nextId = 0;
const pending = new Map();
ws.onmessage = ({ data }) => {
  const message = JSON.parse(data);
  if (pending.has(message.id)) { pending.get(message.id)(message); pending.delete(message.id); }
};
const send = (method, params = {}) => new Promise(resolveSend => {
  const id = ++nextId;
  pending.set(id, resolveSend);
  ws.send(JSON.stringify({ id, method, params }));
});
const evaluate = async expression => {
  const response = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (response.result?.exceptionDetails) throw new Error(JSON.stringify(response.result.exceptionDetails).slice(0, 800));
  return response.result?.result?.value;
};
const delay = ms => new Promise(resolveDelay => setTimeout(resolveDelay, ms));

try {
  await send('Runtime.enable');
  const direct = await evaluate(`fetch('http://127.0.0.1:${server.address().port}/context?threadId=test', {cache:'no-store'}).then(async r => ({ok:r.ok, status:r.status, text:(await r.text()).slice(0,80)})).catch(e => ({error:String(e)}))`);
  console.log('direct fetch:', JSON.stringify(direct));
  await evaluate(`(() => {
    clearInterval(window.__codexContextPanelTimer);
    window.__codexContextPanelSettingsObserver?.disconnect();
    document.getElementById('codex-context-panel')?.remove();
    document.getElementById('codex-context-panel-style')?.remove();
    delete window.__CODEX_CONTEXT_PANEL_VERSION__;
    delete window.__codexContextPanelTimer;
    delete window.__codexContextPanelSettingsObserver;
    delete window.__ccpLastData;
    delete window.__codexContextPanelReceive;
    delete window.__CODEX_CONTEXT_PANEL_STATE__;
    delete window.__CODEX_CONTEXT_PANEL_COMPACT_CONFIG__;
    localStorage.removeItem('codex-context-panel-collapsed');
  })()`);
  await evaluate(`window.__CODEX_CONTEXT_PANEL_COMPACT_CONFIG__ = { autoLimit: 250000, postTurn: 70, contextWindow: 285000 }`);
  const injected = await send('Runtime.evaluate', { expression: source, returnByValue: true, awaitPromise: true });
  assert.ok(!injected.result?.exceptionDetails, JSON.stringify(injected.result?.exceptionDetails));
  let state;
  for (let attempt = 0; attempt < 50; attempt++) {
    state = await evaluate(`(() => {
      const panel = document.getElementById('codex-context-panel');
      const native = document.querySelector('[data-summary-panel-variant]');
      const rect = panel && panel.getBoundingClientRect();
      return {
        panel: !!panel,
        count: document.querySelectorAll('#codex-context-panel').length,
        mountError: window.__CODEX_CONTEXT_PANEL_MOUNT_ERROR__ || null,
        text: panel ? (panel.innerText || '').replace(/\\s+/g, ' ').trim() : '',
        width: rect ? Math.round(rect.width) : 0,
        height: rect ? Math.round(rect.height) : 0,
        sameRail: !!(panel && native && panel.parentElement === native.parentElement),
        rect: rect ? { x: rect.left + scrollX, y: rect.top + scrollY, width: rect.width, height: rect.height } : null,
      };
    })()`);
    if (state.panel && state.text.includes('%') && !/Context helper unavailable|无法连接/.test(state.text)) break;
    await delay(100);
  }
  assert.ok(state?.panel, `panel did not mount: ${state?.mountError}`);
  assert.equal(state.count, 1, 'panel mounted more than once');
  assert.ok(state.width > 0 && state.height > 0, 'panel has no layout');
  assert.ok(state.text.includes('%'), 'panel has no percentage');
  assert.ok(/Context window|上下文窗口/.test(state.text), 'panel has no context-window section');
  assert.match(state.text, /Free|剩余/, 'panel has no full-window free band');
  assert.match(state.text, /Tool \+ request schema|工具 \+ 请求 schema/, 'panel has no tool/request schema band');
  assert.match(state.text, /Auto compact|自动压缩/, 'panel has no auto-compact section');
  assert.match(state.text, /250k \(88%\)/, 'panel did not use the configured compact limit');
  assert.match(state.text, /≈1 turn|≈1 轮/, 'panel has no estimated turns left');
  assert.match(state.text, /End-of-turn|回合结束/, 'panel has no end-of-turn threshold');
  assert.match(state.text, /Cache hit|缓存命中/, 'panel has no cache-hit row');
  assert.match(state.text, /96\.2%/, 'cache-hit percentage is wrong');
  const marker = await evaluate(`(() => { const m=document.querySelector('#codex-context-panel .ccp-mark'); return m ? {display:getComputedStyle(m).display,left:m.style.left,title:m.title} : null; })()`);
  assert.ok(marker && marker.display !== 'none', 'compact threshold marker is hidden');
  assert.equal(Math.round(parseFloat(marker.left)), 88, 'compact threshold marker is in the wrong place');
  assert.ok(!/What the model carries|Full window:/.test(state.text), 'panel still has the removed hints');

  // Settings keeps the selected sidebar thread, so hide on the Settings view
  // itself instead of waiting for the five-second data poll to say "empty".
  await evaluate(`(() => { const w=document.createElement('div'); w.id='__ccp-test-settings'; w.className='navigation-header'; const h=document.createElement('h1'); h.textContent='Settings'; h.style.position='fixed'; w.append(h); document.body.append(w); })()`);
  await delay(100);
  assert.equal(await evaluate("document.querySelectorAll('#codex-context-panel').length"), 0, 'panel still visible in Settings');
  await evaluate("document.getElementById('__ccp-test-settings')?.remove()");
  let returnedFromSettings = false;
  for (let attempt = 0; attempt < 20; attempt++) {
    if (await evaluate("document.querySelectorAll('#codex-context-panel').length")) { returnedFromSettings = true; break; }
    await delay(100);
  }
  assert.ok(returnedFromSettings, 'panel did not return after Settings closed');

  // The collapse button hides the details but keeps the percentage and meter.
  await evaluate(`document.querySelector('#codex-context-panel .ccp-collapse').click()`);
  await delay(100);
  const collapsedState = await evaluate(`(() => {
    const panel = document.getElementById('codex-context-panel');
    const details = panel.querySelector('.ccp-details');
    return {
      display: getComputedStyle(details).display,
      row: getComputedStyle(panel.querySelector('.ccp-row')).display,
      meter: getComputedStyle(panel.querySelector('.ccp-meter')).display,
      height: Math.round(panel.getBoundingClientRect().height),
      label: panel.querySelector('.ccp-collapse').textContent,
    };
  })()`);
  assert.equal(collapsedState.display, 'none', 'collapse did not hide the details');
  assert.notEqual(collapsedState.row, 'none', 'collapse hid the percentage row');
  assert.notEqual(collapsedState.meter, 'none', 'collapse hid the meter');
  assert.ok(collapsedState.height < 200, 'collapsed panel is too tall');
  assert.match(collapsedState.label, /Show|展开/, 'collapse button did not switch to expand');
  await evaluate(`document.querySelector('#codex-context-panel .ccp-collapse').click()`);
  await delay(100);
  const expandedState = await evaluate(`(() => {
    const panel = document.getElementById('codex-context-panel');
    return {
      display: getComputedStyle(panel.querySelector('.ccp-details')).display,
      height: Math.round(panel.getBoundingClientRect().height),
      label: panel.querySelector('.ccp-collapse').textContent,
    };
  })()`);
  assert.notEqual(expandedState.display, 'none', 'expand did not restore the details');
  assert.ok(expandedState.height > 200, 'expanded panel is too short');
  assert.match(expandedState.label, /Hide|收起/, 'expand button did not switch back');
  // An empty new conversation must not show the panel.
  await evaluate('window.__codexContextPanelReceive({ hasConversation: false })');
  await delay(150);
  assert.equal(await evaluate("document.querySelectorAll('#codex-context-panel').length"), 0, 'empty conversation still shows the panel');
  await evaluate(`window.__codexContextPanelReceive(${JSON.stringify(sample)})`);
  await delay(150);
  assert.equal(await evaluate("document.querySelectorAll('#codex-context-panel').length"), 1, 'panel did not return after data');
  // Simulate React moving the panel out of the rail. The 1s timer must
  // rebuild it next to the native summary panel, not leave a blank clone.
  await evaluate(`(() => { const p=document.getElementById('codex-context-panel'); document.body.appendChild(p); })()`);
  await delay(2500);
  const repaired = await evaluate(`(() => {
    const panel = document.getElementById('codex-context-panel');
    const native = document.querySelector('[data-summary-panel-variant]');
    return {
      count: document.querySelectorAll('#codex-context-panel').length,
      sameRail: !!(panel && native && panel.parentElement === native.parentElement),
      text: panel ? (panel.innerText || '').replace(/\\s+/g, ' ').trim() : '',
      rect: panel ? (() => { const r=panel.getBoundingClientRect(); return {x:r.left+scrollX,y:r.top+scrollY,width:r.width,height:r.height}; })() : null,
    };
  })()`);
  assert.equal(repaired.count, 1, 'misplaced panel duplicated');
  assert.ok(repaired.sameRail, 'misplaced panel was not rebuilt in the rail');
  assert.ok(/Context window|上下文窗口/.test(repaired.text), 'rebuilt panel lost its data');
  // The Voice switch turns the helper endpoint off; the panel must vanish and return.
  await fetch(`http://127.0.0.1:${server.address().port}/__disabled?v=1`);
  await evaluate('window.__codexContextPanelRefresh()');
  await delay(500);
  assert.equal(await evaluate("document.querySelectorAll('#codex-context-panel').length"), 0, 'disabled panel still shows');
  await fetch(`http://127.0.0.1:${server.address().port}/__disabled?v=0`);
  await evaluate('window.__codexContextPanelRefresh()');
  let restored = false;
  for (let attempt = 0; attempt < 20; attempt++) {
    if (await evaluate("document.querySelectorAll('#codex-context-panel').length")) { restored = true; break; }
    await delay(100);
  }
  assert.ok(restored, 'panel did not return after re-enabling');
  // The subtitle follows the sidebar without waiting for the five-second poll.
  const originalTitle = await evaluate("document.querySelector('[data-app-action-sidebar-thread-selected=\"true\"]')?.getAttribute('data-app-action-sidebar-thread-title') || ''");
  await evaluate("document.querySelector('[data-app-action-sidebar-thread-selected=\"true\"]')?.setAttribute('data-app-action-sidebar-thread-title', 'Title sync test')");
  await delay(1400);
  assert.equal(await evaluate("document.querySelector('#codex-context-panel .ccp-head .ccp-sub')?.textContent"), 'Title sync test', 'subtitle did not follow the sidebar within one second');
  await evaluate(`document.querySelector('[data-app-action-sidebar-thread-selected="true"]')?.setAttribute('data-app-action-sidebar-thread-title', ${JSON.stringify(originalTitle)})`);
  // Changing band percentages must redraw the meter, not leave the old widths.
  const widths = () => evaluate("Array.from(document.querySelectorAll('#codex-context-panel .ccp-meter-inner > i')).map(i=>i.style.width).join(',')");
  const widthsBefore = await widths();
  await fetch(`http://127.0.0.1:${server.address().port}/__variant?v=1`);
  let widthsAfter = widthsBefore;
  for (let attempt = 0; attempt < 80; attempt++) {
    widthsAfter = await widths();
    if (widthsAfter !== widthsBefore) break;
    await delay(100);
  }
  assert.notEqual(widthsAfter, widthsBefore, 'meter did not redraw on the five-second poll');
  await fetch(`http://127.0.0.1:${server.address().port}/__variant?v=0`);
  await evaluate('window.__codexContextPanelRefresh()');
  await delay(300);
  state = { ...state, ...repaired };
  const viewport = await evaluate('({width: innerWidth, height: innerHeight})');
  if (state.rect.x + state.rect.width > viewport.width || state.rect.y + state.rect.height > viewport.height) {
    // Codex's own summary panel can sit past the viewport edge in a narrow window.
    // Move only the screenshot copy into view; the placement check above already ran.
    await evaluate(`(() => { clearInterval(window.__codexContextPanelTimer); const p=document.getElementById('codex-context-panel'); document.body.appendChild(p); p.style.position='fixed'; p.style.left='20px'; p.style.top='20px'; p.style.right='auto'; p.style.zIndex='9999'; p.style.height='auto'; p.style.maxHeight='none'; p.style.overflow='visible'; p.style.width='300px'; })()`);
    state.rect = await evaluate(`(() => { const r=document.getElementById('codex-context-panel').getBoundingClientRect(); return {x:r.left+scrollX,y:r.top+scrollY,width:r.width,height:r.height}; })()`);
  }
  const crop = {
    x: Math.max(0, state.rect.x - 8),
    y: Math.max(0, state.rect.y - 8),
    width: Math.ceil(state.rect.width + 16),
    height: Math.ceil(state.rect.height + 16),
    scale: 1,
  };
  const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, clip: crop });
  await mkdir('artifacts', { recursive: true });
  const output = resolve('artifacts/context-panel-live.png');
  await writeFile(output, Buffer.from(shot.result.data, 'base64'));

  console.log(JSON.stringify({ ...state, screenshot: output }, null, 2));
} finally {
  try {
    await evaluate(`(() => {
      clearInterval(window.__codexContextPanelTimer);
      window.__codexContextPanelSettingsObserver?.disconnect();
      document.getElementById('codex-context-panel')?.remove();
      document.getElementById('codex-context-panel-style')?.remove();
      delete window.__CODEX_CONTEXT_PANEL_VERSION__;
      delete window.__codexContextPanelTimer;
      delete window.__codexContextPanelSettingsObserver;
      delete window.__ccpLastData;
      localStorage.removeItem('codex-context-panel-collapsed');
      delete window.__codexContextPanelReceive;
      delete window.__CODEX_CONTEXT_PANEL_COMPACT_CONFIG__;
    })()`);
  } catch {}
  ws.close();
  server.close();
}
