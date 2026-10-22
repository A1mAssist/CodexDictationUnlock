// node tests/context-panel-switch.mjs
// Simulates leaving a thread (new chat) and returning to it. The panel must
// hide immediately and come back from the per-thread cache without a fetch.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cdu-context-switch-'));
const sample = JSON.parse(fs.readFileSync(new URL('../artifacts/context-sample.json', import.meta.url), 'utf8'));
const injection = fs.readFileSync(new URL('../context-panel.js', import.meta.url), 'utf8')
  .replace('__HELPER_CONFIG__', JSON.stringify({url: 'http://127.0.0.1:9/config?token=test'}));

try {
  const probe = `
    setTimeout(async () => {
      const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
      const panel = () => document.getElementById('codex-context-panel');
      const row = document.querySelector('[data-app-action-sidebar-thread-selected="true"]');
      const composer = document.getElementById('composer');
      await wait(1200);
      const initial = !!panel();
      const text = panel()?.innerText || '';
      row.setAttribute('data-app-action-sidebar-thread-selected', 'false');
      await wait(100);
      const staysWithoutSidebar = !!panel();
      composer.removeAttribute('data-above-composer-conversation-id');
      await wait(100);
      const hidden = !panel();
      window.__fetchBlocked = true;
      composer.setAttribute('data-above-composer-conversation-id', '019ff09e-7b75-7ea3-a1fe-d67b53c5fd53');
      row.setAttribute('data-app-action-sidebar-thread-selected', 'true');
      await wait(100);
      const returned = !!panel();
      const result = {
        initial,
        staysWithoutSidebar,
        hidden,
        returned,
        hasContext: /Context|上下文/.test(text),
        hasPercent: /%/.test(text),
      };
      const pre = document.createElement('pre');
      pre.id = 'results';
      pre.textContent = JSON.stringify(result);
      document.body.append(pre);
    }, 1200);
  `;
  const html = `<!doctype html><html lang="en" style="color-scheme:dark"><head><meta charset="utf-8"><style>
    body{margin:0;background:#0a0a0a;color:#fff;font:14px "Segoe UI",sans-serif}
    .rail{width:320px;padding:16px}.panel{padding:12px;border:1px solid #333;border-radius:16px}
    pre{display:none}
  </style></head><body>
    <div data-app-action-sidebar-thread-selected="true" data-app-action-sidebar-thread-id="local:019ff09e-7b75-7ea3-a1fe-d67b53c5fd53" data-app-action-sidebar-thread-title="Thread A"><span data-app-action-sidebar-thread-title="Thread A">Thread A</span></div>
    <div id="composer" data-above-composer-conversation-id="019ff09e-7b75-7ea3-a1fe-d67b53c5fd53"></div>
    <div class="_WorkspaceContent_test"><div class="rail"><div class="panel" data-summary-panel-variant><div>Native summary</div></div></div></div>
    <script>
      window.__fetchBlocked = false;
      const sample = ${JSON.stringify(sample)};
      window.fetch = async () => {
        if (window.__fetchBlocked) return new Promise(() => {});
        return {ok: true, json: async () => JSON.parse(JSON.stringify(sample))};
      };
    </script>
    <script>${injection}</script>
    <script>${probe}</script>
  </body></html>`;
  const file = path.join(dir, 'switch.html');
  fs.writeFileSync(file, html);
  const result = spawnSync('C:/Program Files/Google/Chrome/Application/chrome.exe', [
    '--headless=new', '--disable-gpu', '--no-first-run',
    `--user-data-dir=${path.join(dir, 'profile')}`,
    '--virtual-time-budget=5000', '--window-size=900,900', '--dump-dom',
    'file:///' + file.replaceAll('\\', '/'),
  ], {maxBuffer: 32 * 1024 * 1024, encoding: 'utf8', timeout: 30000});
  assert.equal(result.status, 0, result.stderr?.slice(-1000));
  const match = result.stdout.match(/<pre id="results">([\s\S]*?)<\/pre>/);
  assert.ok(match, 'switch probe missing');
  const data = JSON.parse(match[1].replaceAll('&quot;', '"').replaceAll('&amp;', '&').replaceAll('&lt;', '<').replaceAll('&gt;', '>'));
  assert.ok(data.initial, 'panel did not mount for the first thread');
  assert.ok(data.hasContext && data.hasPercent, 'panel did not render context data');
  assert.ok(data.staysWithoutSidebar, 'panel disappeared when the sidebar selection was removed');
  assert.ok(data.hidden, 'panel stayed visible after leaving the thread');
  assert.ok(data.returned, 'panel did not return from cache with fetch blocked');
  console.log('Context panel switch passed');
} finally {
  fs.rmSync(dir, {recursive: true, force: true, maxRetries: 5, retryDelay: 200});
}