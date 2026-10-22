// Run: node tests/context-panel-toggle.mjs [screenshot-output-directory]
// Mounts injection.js in a mock Voice page and checks the Context panel switch.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cdu-context-toggle-'));
const injection = fs.readFileSync(new URL('../injection.js', import.meta.url), 'utf8')
  .replace('__CONNECT_INFO__', JSON.stringify({websocketUrl:'ws://127.0.0.1:9/dictation',protocols:[]}))
  .replace('__HELPER_CONFIG__', JSON.stringify({url:'http://127.0.0.1:9/config?token=test'}));
try {
  const mock = `
    let contextPanel=true;
    const posts=[];
    window.fetch=async(url,options={})=>{
      if((options.method||'GET').toUpperCase()==='POST'){
        const body=JSON.parse(options.body);
        if('contextPanel' in body){posts.push(body);contextPanel=body.contextPanel;}
        return {ok:true,json:async()=>({ready:true})};
      }
      return {ok:true,json:async()=>({provider:'aliyun',workspaceId:'ws-test',hasApiKey:true,ready:true,contextPanel,title:{mode:'off'}})};
    };
  `;
  const probe = `
    setTimeout(async()=>{
      const card=document.querySelector('[data-codex-context-settings]');
      const sw=card?.querySelector('[data-context-switch]');
      const track=sw?.firstElementChild;const thumb=track?.firstElementChild;
      const before={exists:!!card,checked:sw?.getAttribute('aria-checked'),state:sw?.getAttribute('data-state'),buttonClass:sw?.className||'',trackClass:track?.className||'',thumbClass:thumb?.className||'',text:card?.innerText||''};
      sw?.click();
      await new Promise(resolve=>setTimeout(resolve,250));
      const after={checked:sw?.getAttribute('aria-checked'),state:sw?.getAttribute('data-state'),trackClass:track?.className||'',thumbState:thumb?.getAttribute('data-state'),status:card?.querySelector('[data-context-status]')?.textContent||''};
      const pre=document.createElement('pre');pre.id='results';pre.textContent=JSON.stringify({before,after,posts});document.body.append(pre);
    },3200);
  `;
  const html = `<!doctype html><html lang="en" style="color-scheme:dark"><head><meta charset="utf-8"><style>
    body{background:#0a0a0a;color:#eee;font:14px "Segoe UI",sans-serif;padding:24px;width:880px}
    input,button,select{font:inherit;border:1px solid #333;border-radius:10px;background:#171717;color:#eee;padding:0 10px}
    button{background:#202020} pre{display:none}
  </style></head><body><div role="dialog" aria-modal="true"><section>
    <div style="border:1px solid #292929;border-radius:16px;padding:16px">
      <h2>Dictation</h2><label>Dictionary <input data-dictation-dictionary-entry-index placeholder="Jane Doe" value="Existing entry" style="height:36px"></label><button type="button">Add</button>
    </div></section></div><script>${mock}</script><script>${injection}</script><script>${probe}</script></body></html>`;
  const file = path.join(dir, 'toggle.html');
  fs.writeFileSync(file, html);
  const args=['--headless=new','--disable-gpu','--no-first-run',`--user-data-dir=${path.join(dir,'profile')}`,'--virtual-time-budget=5000','--window-size=980,900','--dump-dom'];
  if(process.argv[2]){fs.mkdirSync(process.argv[2],{recursive:true});args.push('--screenshot='+path.resolve(process.argv[2],'context-toggle.png'));}
  args.push('file:///'+file.replaceAll('\\','/'));
  const result=spawnSync('C:/Program Files/Google/Chrome/Application/chrome.exe',args,{maxBuffer:16*1024*1024,encoding:'utf8',timeout:30000});
  assert.equal(result.status,0,result.stderr?.slice(-1000));
  const match=result.stdout.match(/<pre id="results">([\s\S]*?)<\/pre>/);
  assert.ok(match,'toggle probe missing');
  const data=JSON.parse(match[1].replaceAll('&quot;','"').replaceAll('&amp;','&').replaceAll('&lt;','<').replaceAll('&gt;','>'));
  assert.equal(data.before.exists,true,'context settings card did not mount');
  assert.equal(data.before.checked,'true','switch should start enabled');
  assert.equal(data.before.state,'checked','native switch state missing');
  assert.match(data.before.buttonClass,/inline-flex/,'switch is not a native-style control');
  assert.match(data.before.trackClass,/bg-chart-blue/,'native checked track class missing');
  assert.match(data.before.trackClass,/h-5 w-8/,'native switch track size missing');
  assert.match(data.before.thumbClass,/data-\[state=checked\]:translate-x-\[14px\]/,'native thumb motion class missing');
  assert.match(data.before.text,/Context panel/,'card is missing its title');
  assert.equal(data.after.checked,'false','click did not turn the switch off');
  assert.equal(data.after.state,'unchecked','native switch state did not change');
  assert.match(data.after.trackClass,/bg-text\/10/,'native unchecked track class missing');
  assert.equal(data.after.thumbState,'unchecked','native thumb state did not change');
  assert.deepEqual(data.posts,[{contextPanel:false}],'wrong payload sent to /config');
  assert.equal(data.after.status,'Saved','save status missing');
  console.log('Context panel toggle passed.');
} finally {fs.rmSync(dir,{recursive:true,force:true,maxRetries:5,retryDelay:200});}
