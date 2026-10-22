import { chromium } from '../csweet-promo/node_modules/playwright/index.mjs';
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { startServer } from './serve.mjs';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const server=await startServer();
const base=`http://127.0.0.1:${server.address().port}`;
const browser=await chromium.launch({headless:true,args:[`--log-file=${path.join(root,'artifacts/explainer-review/chromium.log')}`]});
try{
 const page=await browser.newPage({viewport:{width:1280,height:720}});
 // The review server must not serve repository content outside its exact allowlist.
 for(const p of ['/README.md','/.env','/.git/config','/src/CSweet.Api/appsettings.json']){
  const response=await page.request.get(base+p);if(response.status()!==404)throw new Error(`Unexpected exposure: ${p}`);
 }
 await page.setContent(`<style>body{margin:0;background:#eeeaf3}video{width:100vw;height:100vh}</style><video muted playsinline controls src="${base}/assets/readme/explainer/from-idea-to-play.animatic.mp4"></video>`);
 await page.waitForFunction(()=>document.querySelector('video').readyState>=2);
 const metadata=await page.evaluate(()=>{const v=document.querySelector('video');return {duration:v.duration,width:v.videoWidth,height:v.videoHeight};});
 if(metadata.duration!==96||metadata.width!==1280||metadata.height!==720)throw new Error('Unexpected media metadata');
 await page.evaluate(async()=>{const v=document.querySelector('video');v.playbackRate=2;await v.play();});
 await page.waitForFunction(()=>document.querySelector('video').ended,{},{timeout:60000});
 const playback=await page.evaluate(()=>{const v=document.querySelector('video'),q=v.getVideoPlaybackQuality();return {ended:v.ended,error:v.error?.message??null,totalFrames:q.totalVideoFrames,droppedFrames:q.droppedVideoFrames,playbackRate:v.playbackRate};});
 if(playback.error)throw new Error(playback.error);
 await page.evaluate(async()=>{const v=document.querySelector('video');await new Promise(resolve=>{v.addEventListener('seeked',resolve,{once:true});v.currentTime=84;});v.controls=false;});
 await page.screenshot({path:path.join(root,'artifacts/explainer-review/encoded-game.png')});
 await page.goto(base+'/tools/csweet-explainer/animatic.html?render=1&t=84');await page.evaluate(()=>window.filmReady);
 await page.screenshot({path:path.join(root,'artifacts/explainer-review/source-game.png')});
 const file=path.join(root,'artifacts/explainer-review/verification.json');
 const report=JSON.parse(await readFile(file,'utf8'));report.browserPlayback={...metadata,...playback};report.previewAllowlistChecked=true;
 await writeFile(file,JSON.stringify(report,null,2));console.log(JSON.stringify(report.browserPlayback));
}finally{await browser.close();server.close();}
