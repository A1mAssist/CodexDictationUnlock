// node tests/voice-fallback-layout.mjs [screenshot-output-directory]
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cdu-voice-fallback-'));
const injection = fs.readFileSync(new URL('../injection.js', import.meta.url), 'utf8')
  .replace('__CONNECT_INFO__', JSON.stringify({websocketUrl: 'ws://127.0.0.1:9/dictation', protocols: []}))
  .replace('__HELPER_CONFIG__', JSON.stringify({url: 'http://127.0.0.1:9/config?token=test'}));

try {
  const probe = `
    setTimeout(() => {
      const asr = document.querySelector('[data-codex-dictation-asr-settings]');
      const title = document.querySelector('[data-codex-title-settings]');
      const context = document.querySelector('[data-codex-context-settings]');
      const recent = Array.from(document.querySelectorAll('.card')).find((element) => (element.textContent || '').includes('Recent recordings'));
      const root = asr?.firstElementChild;
      const provider = asr?.querySelector('select[name=provider]');
      const endpoint = asr?.querySelector('input[name=endpointId]');
      const key = asr?.querySelector('input[name=apiKey]');
      const save = asr?.querySelector('[data-asr-form] button[type=submit]');
      const rect = (element) => element?.getBoundingClientRect() || null;
      const data = {
        asr: !!asr,
        title: !!title,
        context: !!context,
        rootDisplay: root ? getComputedStyle(root).display : '',
        rootPadding: root ? getComputedStyle(root).padding : '',
        providerHeight: Math.round(rect(provider)?.height || 0),
        fieldRow: [provider, endpoint, key].every((element) => Math.round(rect(element)?.y || -1) === Math.round(rect(provider)?.y || -2)),
        saveBelow: !!save && Math.round(rect(save)?.y || 0) > Math.round(rect(provider)?.y || 0) + 30,
        inputBorder: provider ? getComputedStyle(provider).borderTopWidth : '',
        inputBackground: provider ? getComputedStyle(provider).backgroundColor : '',
        buttonBackground: save ? getComputedStyle(save).backgroundColor : '',
        titleRootDisplay: title?.firstElementChild ? getComputedStyle(title.firstElementChild).display : '',
        order: !!asr && !!title && !!context && !!recent &&
          !!(asr.compareDocumentPosition(title) & Node.DOCUMENT_POSITION_FOLLOWING) &&
          !!(title.compareDocumentPosition(context) & Node.DOCUMENT_POSITION_FOLLOWING) &&
          !!(context.compareDocumentPosition(recent) & Node.DOCUMENT_POSITION_FOLLOWING),
        count: document.querySelectorAll('[data-codex-dictation-asr-settings],[data-codex-title-settings],[data-codex-context-settings]').length,
      };
      const pre = document.createElement('pre');
      pre.id = 'results';
      pre.textContent = JSON.stringify(data);
      document.body.append(pre);
    }, 1800);
  `;
  const html = `<!doctype html><html lang="en" style="color-scheme:dark"><head><meta charset="utf-8"><style>
    :root{--color-text-primary:#fff;--color-text-secondary:rgba(255,255,255,.65);--color-background-secondary-soft:rgba(255,255,255,.052);--color-token-input-border:rgba(255,255,255,.156);--input-text-color:#fff;--input-placeholder-text-color:rgba(255,255,255,.5)}
    body{background:#000;color:#fff;font:430 16px/24px "Segoe UI",sans-serif;padding:24px;width:880px;box-sizing:border-box}
    .card{display:flex;flex-direction:column;border:1px solid rgba(255,255,255,.082);border-radius:20px;background:#0c0c0c;overflow:hidden}
    .row{display:flex;align-items:center;justify-content:space-between;gap:24px;padding:12px 16px}
    .muted{color:rgba(255,255,255,.5);font-size:14px;line-height:1.4}
  </style></head><body>
    <section style="display:flex;flex-direction:column;width:100%">
      <div style="display:flex;align-items:center;justify-content:space-between;padding-bottom:14px"><div style="font-weight:600">Dictation</div></div>
      <div style="display:flex;flex-direction:column;gap:6px">
        <div class="card"><div class="row"><div><div style="font-weight:500">Dictation unavailable</div><div class="muted">Dictation is not available in your current configuration</div></div></div></div>
        <div class="card"><div class="row"><div><div style="font-weight:500">Recent recordings</div><div class="muted">Your last 20 recordings are saved on this device</div></div><button type="button" style="border:0;background:transparent;padding:4px">Show 20 recordings</button></div></div>
      </div>
    </section>
    <script>window.fetch=async()=>({ok:true,json:async()=>({provider:'aliyun',workspaceId:'ws-test',hasApiKey:true,ready:true,title:{mode:'off',available:true,hasApiKey:true}})});</script>
    <script>${injection}</script><script>${probe}</script>
  </body></html>`;
  const file = path.join(dir, 'fallback.html');
  fs.writeFileSync(file, html);
  const args = ['--headless=new', '--disable-gpu', '--no-first-run', `--user-data-dir=${path.join(dir, 'profile')}`, '--virtual-time-budget=5000', '--window-size=980,1400', '--dump-dom'];
  if (process.argv[2]) {
    fs.mkdirSync(process.argv[2], {recursive: true});
    args.push('--screenshot=' + path.resolve(process.argv[2], 'fallback-layout.png'));
  }
  args.push('file:///' + file.replaceAll('\\', '/'));
  const result = spawnSync('C:/Program Files/Google/Chrome/Application/chrome.exe', args, {maxBuffer: 32 * 1024 * 1024, encoding: 'utf8', timeout: 30000});
  assert.equal(result.status, 0, result.stderr?.slice(-1000));
  const match = result.stdout.match(/<pre id="results">([\s\S]*?)<\/pre>/);
  assert.ok(match, 'probe missing');
  const data = JSON.parse(match[1].replaceAll('&quot;', '"').replaceAll('&amp;', '&').replaceAll('&lt;', '<').replaceAll('&gt;', '>'));
  assert.ok(data.asr && data.title && data.context, 'injected cards missing');
  assert.equal(data.count, 3, 'unexpected card count');
  assert.equal(data.rootDisplay, 'block', 'content root is not a block');
  assert.equal(data.rootPadding, '12px 16px', 'content root padding drifted');
  assert.ok(data.providerHeight === 36, 'control height drifted');
  assert.ok(data.fieldRow, 'ASR fields are not on one row');
  assert.ok(data.saveBelow, 'Save is not below the ASR fields');
  assert.notEqual(data.inputBorder, '0px', 'fallback input has no border');
  assert.notEqual(data.inputBackground, 'rgba(0, 0, 0, 0)', 'fallback input has no surface');
  assert.notEqual(data.buttonBackground, 'rgba(0, 0, 0, 0)', 'fallback button has no surface');
  assert.equal(data.titleRootDisplay, 'block', 'title content root is not a block');
  assert.ok(data.order, 'card order is wrong');
  console.log('Voice fallback layout passed');
} finally {
  fs.rmSync(dir, {recursive: true, force: true, maxRetries: 5, retryDelay: 200});
}