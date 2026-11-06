import {test, expect} from 'bun:test';
import {readFileSync} from 'node:fs';
import {runInNewContext} from 'node:vm';
import {webcrypto, createHash} from 'node:crypto';

const template=readFileSync(new URL('../bindings/godot/game-build/web_guard.js.txt', import.meta.url),'utf8');
const original=new Uint8Array([0,97,115,109,1,0,0,0]);
const hash=createHash('sha256').update(original).digest('hex');
function host(bytes=original, mutation=false) {
  let reads=0, initialized=0;
  const source=template.replace('/*EXPECTED*/',JSON.stringify({'gua.wasm':hash}))
    .replace('/*MODE*/','"off"').replace('/*SOURCE*/','"source"').replace('/*BUILD*/','"build"');
  const context:any={document:{baseURI:'https://game.test/index.html'},URL,Request,Response,Uint8Array,Map,Set,
    crypto:webcrypto,console:{info(){},error(){}}, fetch:async()=>{reads++;return new Response(bytes)},
    Engine:function(){initialized++;}};
  runInNewContext(mutation?source.replace('if (actual !== file.hash)','if (false)'):source,context);
  return {context, reads:()=>reads, initialized:()=>initialized};
}
test('verified response bytes are pinned, copied and shared by concurrent readers',async()=>{
  const h=host();const [a,b]=await Promise.all([h.context.fetch('gua.wasm'),h.context.fetch('gua.wasm')]);
  const first=new Uint8Array(await a.arrayBuffer());first[0]=255;
  expect(new Uint8Array(await b.arrayBuffer())).toEqual(original);expect(h.reads()).toBe(1);
  expect(h.context.__guaGameIntegrity.acceptGame('off','source','build')).toBe(true);
  expect(h.context.__guaGameIntegrity.acceptGame('testing','source','build')).toBe(false);
});
test('mismatched native bytes never reach the loader, including retries',async()=>{
  const h=host(new Uint8Array([9,9]));let loaded=0;
  await expect(h.context.fetch('gua.wasm').then(()=>loaded++)).rejects.toThrow('integrity refused');
  await expect(h.context.fetch('gua.wasm')).rejects.toThrow('integrity refused');
  expect(loaded).toBe(0);expect(h.reads()).toBe(1);
  expect(h.context.__guaGameIntegrity.status().refused).toBe(true);
});
test('the negative oracle detects an intentional hash-check bypass',async()=>{
  const h=host(new Uint8Array([9,9]),true);let loaded=0;
  await h.context.fetch('gua.wasm').then(()=>loaded++);
  expect(()=>{if(loaded!==0)throw Error('native initialized before refusal');}).toThrow('native initialized before refusal');
});
test('alternate origin/query cannot bypass the SDK request gate',async()=>{
  for(const path of ['https://other.test/gua.wasm','gua.wasm?alternate=1']) {
    const h=host();await expect(h.context.fetch(path)).rejects.toThrow('integrity refused');expect(h.reads()).toBe(0);
  }
});
test('missing crypto and a missing configured SDK module fail closed',async()=>{
  const h=host();h.context.crypto=undefined;
  await expect(h.context.fetch('gua.wasm')).rejects.toThrow('integrity refused');
  const a=host();expect(()=>new a.context.__guaGameIntegrity.Engine({gdextensionLibs:[]})).toThrow('integrity refused');
  expect(a.initialized()).toBe(0);
});
test('ordinary non-SDK requests are preserved and the verifier cannot be replaced',async()=>{
  const h=host();await h.context.fetch('index.wasm');expect(h.reads()).toBe(1);
  expect(Object.getOwnPropertyDescriptor(h.context,'fetch')?.writable).toBe(false);
  expect(Object.getOwnPropertyDescriptor(h.context,'__guaGameIntegrity')?.configurable).toBe(false);
});
