import {resolve, sep} from 'node:path';
import {mkdtemp, rm, writeFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import {createCdpClient, waitForDefaultExecutionContext} from './godot-web-cdp';

const root=resolve(process.argv[2]);
const mode=process.argv[3]; // off/testing/player/swap/csp
const replacement=process.argv[4];
const browser=Bun.env.CHROME_EXECUTABLE ?? Bun.which('google-chrome') ?? Bun.which('chromium');
if(!browser)throw Error('An existing Chrome/Chromium is required.');
const html=await Bun.file(resolve(root,'index.html')).text();
const scripts=[...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)].filter(m=>!m[1].includes('src='));
const bootstrap=scripts.at(-1)?.[2];
if(!bootstrap?.includes('new globalThis.__guaGameIntegrity.Engine('))throw Error('Missing gated game bootstrap.');
const cspHash=createHash('sha256').update(bootstrap).digest('base64');
let sdkReads=0;
const server=Bun.serve({hostname:'127.0.0.1',port:0,async fetch(request){
  const url=new URL(request.url);const name=decodeURIComponent(url.pathname==='/index.html'?'/index.html':url.pathname).replace(/^\/+/, '');
  const file=resolve(root,name);
  if(!file.startsWith(root+sep))return new Response('Forbidden',{status:403});
  const headers:Record<string,string>={'Cross-Origin-Opener-Policy':'same-origin','Cross-Origin-Embedder-Policy':'require-corp'};
  if(mode==='csp')headers['Content-Security-Policy']=`script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval' 'sha256-${cspHash}'; object-src 'none'`;
  let selected=file;
  if(name==='gua.wasm'){
    sdkReads++;
    // In the off positive case later reads would receive ON bytes. The gate
    // must reuse its checked response rather than request this changed file.
    if(replacement&&(mode==='swap'||sdkReads>1))selected=resolve(replacement);
    headers['Content-Type']='application/wasm';
  }
  const body=Bun.file(selected);return await body.exists()?new Response(body,{headers}):new Response('Not found',{status:404});
}});
const profile=await mkdtemp(resolve(root,'.owned-browser-'));
const debuggingPort=19596;
const page=`http://127.0.0.1:${server.port}/index.html`;
const chrome=Bun.spawn([browser,'--headless=new','--no-sandbox','--disable-dev-shm-usage','--disable-background-networking','--disable-component-update','--enable-logging=stderr','--no-first-run','--enable-webgl','--ignore-gpu-blocklist','--use-angle=swiftshader','--enable-unsafe-swiftshader','--remote-allow-origins=*',`--remote-debugging-port=${debuggingPort}`,'--remote-debugging-address=127.0.0.1',`--user-data-dir=${profile}`,page],{stdout:'ignore',stderr:'pipe'});
const logReader=chrome.stderr.getReader();
let browserLog='';
const drainLog=(async()=>{try{for(;;){const chunk=await logReader.read();if(chunk.done)break;browserLog=(browserLog+new TextDecoder().decode(chunk.value)).slice(-10000);}}catch{}})();
let passed=false;
let client:ReturnType<typeof createCdpClient>|undefined;
try {
  let target:any;const deadline=Date.now()+20000;
  while(Date.now()<deadline){
    try{const targets=await fetch(`http://127.0.0.1:${debuggingPort}/json/list`).then(r=>r.json()) as any[];target=targets.find(t=>t.type==='page'&&t.url===page);if(target)break;}catch{}
    await Bun.sleep(100);
  }
  if(!target)throw Error('Owned browser did not expose the fixture page.');
  client=createCdpClient(target.webSocketDebuggerUrl);await client.open();await client.send('Runtime.enable');
  await waitForDefaultExecutionContext(client,10000,page);
  const result=await client.send('Runtime.evaluate',{awaitPromise:true,returnByValue:true,expression:`(${async function(mode:string){
    const g:any=globalThis;const deadline=performance.now()+25000;
    if(mode==='csp'){
      while(typeof g.Engine!=='function'&&performance.now()<deadline)await new Promise(r=>setTimeout(r,25));
      await new Promise(r=>setTimeout(r,500));
      if(typeof g.Engine!=='function'||g.__guaGameIntegrity||g.__guaGameFixture||g.__guaGodotWebPort)throw Error('Blocked verifier did not prevent engine startup.');
      return {case:mode,engineDefined:true,gameReady:false,port:false};
    }
    while(performance.now()<deadline){
      const status=g.__guaGameIntegrity?.status();
      if(mode==='swap'&&status?.refused){
        if(g.__guaGameFixture||g.__guaGodotWebPort)throw Error('SDK swap reached game or remote startup.');
        return {case:mode,refused:true,verified:status.verified,gameReady:false,port:false};
      }
      if(mode!=='swap'&&g.__guaGameFixture?.ready)break;
      await new Promise(r=>setTimeout(r,25));
    }
    if(mode==='swap')throw Error('Specific SDK swap refusal not observed.');
    const f=g.__guaGameFixture;const status=g.__guaGameIntegrity?.status();
    if(!f?.ready||f.mode!==mode||!f.localUi||status?.verified!==1||status.refused)throw Error('Verified local UI was not reached: '+JSON.stringify({f,status}));
    if(mode==='off'){
      if(g.__guaGodotWebPort||f.build.remoteEnabled)throw Error('OFF game exposed a remote port.');
      const bytes=await (await fetch('gua.wasm')).arrayBuffer();
      const hash=[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(v=>v.toString(16).padStart(2,'0')).join('');
      return {case:mode,ready:true,consumed:0,port:false,refetchedHash:hash,build:f.build};
    }
    if(!g.__guaGodotWebPort||f.build.remotePermissionCeiling!==(mode==='player'?'player':'debug')||!f.clockAllowed||(mode==='player'&&!f.debugDenied))throw Error('Compiled mode/Player ceiling mismatch.');
    let clockRejected=false;
    try{await g.__guaGodotWebPort.invoke({type:'clock_install',initialTimeMs:0,stepMs:10});}catch(error:any){clockRejected=error?.code==='engine_unsupported';}
    if(!clockRejected)throw Error('Web port unexpectedly exposed remote clock control.');
    const completion=await g.__guaGodotWebPort.invoke({type:'perform_action',request:{action:'click',nodeId:'guarded'}},{timeoutMs:5000});
    if(!completion?.succeeded||!Number.isSafeInteger(completion.requestId)||completion.requestId<=0)throw Error('Missing correlated input completion.');
    const consumedDeadline=performance.now()+2000;
    while(g.__guaGameFixture.consumed===0&&performance.now()<consumedDeadline)await new Promise(r=>setTimeout(r,20));
    if(g.__guaGameFixture.consumed!==1)throw Error('Host did not consume exactly one input.');
    return {case:mode,ready:true,consumed:1,requestId:completion.requestId,debugDenied:f.debugDenied,clockAllowed:f.clockAllowed,build:f.build};
  }.toString()})(${JSON.stringify(mode)})`},35000) as any;
  if(result.exceptionDetails)throw Error(JSON.stringify(result.exceptionDetails));
  const evidence={...result.result.value,sdkReads};
  if((mode==='swap'&&evidence.verified!==0)||(mode==='csp'&&sdkReads!==0)||(mode!=='csp'&&sdkReads!==1))throw Error('Wrong verification/request count: '+JSON.stringify(evidence));
  if(mode==='off'&&evidence.refetchedHash!==createHash('sha256').update(new Uint8Array(await Bun.file(resolve(root,'gua.wasm')).arrayBuffer())).digest('hex'))throw Error('Verified bytes changed between checks and loader reads.');
  await writeFile(resolve(root,`${mode}-browser-result.json`),JSON.stringify(evidence,null,2));
  console.log(JSON.stringify(evidence));
  passed=true;
} finally {
  try{await client?.send('Browser.close',{},3000);}catch{}
  client?.close();
  await Promise.race([chrome.exited,Bun.sleep(4000)]);
  if(chrome.exitCode===null)chrome.kill(); // Only this owned browser/profile.
  await chrome.exited;server.stop(true);
  // An updater subprocess may inherit stderr after the owned browser exits.
  // Close our read end; never wait for or terminate that unrelated subprocess.
  await logReader.cancel();await drainLog;
  if(!passed)console.error(browserLog); // Controlled fixture diagnostics only.
  if(!profile.startsWith(root+sep))throw Error('Unexpected owned profile path.');
  await rm(profile,{recursive:true,force:true});
}
