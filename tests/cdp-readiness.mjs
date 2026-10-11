// Run: node tests/cdp-readiness.mjs <path-to-CodexDictation.dll>
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

let failures = 0;
const server = createServer((req, res) => {
  if (req.url !== '/json/list') { res.writeHead(404); res.end(); return; }
  failures++;
  if (failures < 3) { res.writeHead(500); res.end('starting'); return; }
  res.setHeader('Content-Type', 'application/json');
  res.end('[]');
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const host = spawn('pwsh', ['-NoProfile', '-File', fileURLToPath(new URL('./cdp-readiness-host.ps1', import.meta.url)), '-AssemblyPath', resolve(process.argv[2]), '-Port', String(server.address().port)], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
let stderr = '';
host.stderr.on('data', data => { stderr += data; });
const exitCode = await new Promise(resolve => host.on('close', resolve));
server.close();
assert.equal(exitCode, 0, stderr);
assert.equal(failures, 3);
console.log('CDP readiness test passed: transient 500 responses are retried.');
