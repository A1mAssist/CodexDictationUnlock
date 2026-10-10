// node tests/retry-settings.mjs [screenshot-output-directory]
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cdu-retry-ui-'));
const injection = fs.readFileSync(new URL('../injection.js', import.meta.url), 'utf8')
  .replace('__CONNECT_INFO__', JSON.stringify({websocketUrl:'ws://127.0.0.1:9/dictation',protocols:[]}))
  .replace('__HELPER_CONFIG__', JSON.stringify({url:'http://127.0.0.1:9/config?token=test'}));
try {
  for (const scenario of ['unavailable', 'configured', 'recovering', 'builtin']) {
    const mock = `
      const scenario=${JSON.stringify(scenario)};
      const writes=[];
      const config={model_provider:scenario==='builtin'?'openai':'relay',model_providers:{relay:{name:'Relay',stream_max_retries:7}}};
      const client={async sendRequest(method,params){
        if(method==='config/read')return {config,layers:[{name:{type:'user',file:'test-config.toml'},version:'test-version'}]};
        if(method==='config/batchWrite'){writes.push(params);return {};}
        throw Error('Unexpected RPC method: '+method);
      }};
      if(scenario==='configured'||scenario==='builtin')window.__CODEX_DICTATION_APP_SERVERS__=new Map([['local',client]]);
      if(scenario==='recovering')setTimeout(()=>{window.__CODEX_DICTATION_APP_SERVERS__=new Map([['local',client]]);},500);
      window.fetch=async()=>({ok:true,json:async()=>({provider:'aliyun',workspaceId:'ws-test',hasApiKey:true,ready:true,title:{mode:'off'}})});
    `;
    const probe = `
      setTimeout(async()=>{
        const form=document.querySelector('[data-retry-form]');
        const input=form.elements.retryCount;
        const before={value:input.value,placeholder:input.placeholder,disabled:input.disabled,provider:form.querySelector('[data-retry-provider]').textContent,status:form.querySelector('[data-retry-status]').textContent};
        if(!input.disabled){input.value='3';input.dispatchEvent(new Event('input'));form.dispatchEvent(new Event('submit',{cancelable:true,bubbles:true}));}
        await new Promise(resolve=>setTimeout(resolve,50));
        const pre=document.createElement('pre');pre.id='results';pre.textContent=JSON.stringify({before,writes,status:form.querySelector('[data-retry-status]').textContent});document.body.append(pre);
      },3200);
    `;
    const html = `<!doctype html><html lang="en" style="color-scheme:dark"><head><meta charset="utf-8"><style>
      body{background:#0a0a0a;color:#eee;font:14px "Segoe UI",sans-serif;padding:24px;width:880px}
      input,button,select{font:inherit;border:1px solid #333;border-radius:10px;background:#171717;color:#eee;padding:0 10px}
      button{background:#202020} input:disabled,button:disabled{opacity:.5} pre{display:none}
    </style></head><body><div role="dialog" aria-modal="true"><section>
      <div style="border:1px solid #292929;border-radius:16px;padding:16px">
        <h2>Dictation</h2><label>Dictionary <input data-dictation-dictionary-entry-index placeholder="Jane Doe" value="Existing entry" style="height:36px"></label><button type="button">Add</button>
      </div></section></div><script>${mock}</script><script>${injection}</script><script>${probe}</script></body></html>`;
    const file = path.join(dir, scenario+'.html');fs.writeFileSync(file,html);
    const args=['--headless=new','--disable-gpu','--no-first-run',`--user-data-dir=${path.join(dir,'profile-'+scenario)}`,'--virtual-time-budget=5000','--window-size=980,900','--dump-dom'];
    if(process.argv[2]){fs.mkdirSync(process.argv[2],{recursive:true});args.push('--screenshot='+path.resolve(process.argv[2],scenario+'.png'));}
    args.push('file:///'+file.replaceAll('\\','/'));
    const result=spawnSync('C:/Program Files/Google/Chrome/Application/chrome.exe',args,{maxBuffer:16*1024*1024,encoding:'utf8',timeout:30000});
    assert.equal(result.status,0,result.stderr?.slice(-1000));
    const match=result.stdout.match(/<pre id="results">([\s\S]*?)<\/pre>/);assert.ok(match,scenario+' probe missing');
    const data=JSON.parse(match[1].replaceAll('&quot;','"').replaceAll('&amp;','&').replaceAll('&lt;','<').replaceAll('&gt;','>'));
    assert.equal(data.before.placeholder,'',scenario+' clears cloned placeholder');
    assert.notEqual(data.before.value,'Existing entry',scenario+' clears cloned value');
    if(scenario==='unavailable'){
      assert.equal(data.before.disabled,true);assert.equal(data.before.value,'');
      assert.equal(data.before.provider,'Current provider');assert.equal(data.before.status,'Codex configuration is unavailable');assert.equal(data.writes.length,0);
    }else if(scenario==='builtin'){
      assert.equal(data.before.disabled,true);assert.equal(data.before.value,'5');assert.equal(data.writes.length,0);
    }else{
      assert.equal(data.before.disabled,false);assert.equal(data.before.value,'7');assert.equal(data.before.provider,'Current provider: Relay (relay)');
      assert.deepEqual(data.writes,[{edits:[{keyPath:'model_providers.relay.stream_max_retries',value:3,mergeStrategy:'upsert'}],filePath:'test-config.toml',expectedVersion:'test-version',reloadUserConfig:true}]);
      assert.equal(data.status,'Saved · applies to the next request');
    }
    console.log('Retry settings passed: '+scenario);
  }
} finally {fs.rmSync(dir,{recursive:true,force:true,maxRetries:5,retryDelay:200});}
