// Run from any directory. Uses the existing promo tool's pinned Playwright.
import { chromium } from '../csweet-promo/node_modules/playwright/index.mjs';
import { mkdir, writeFile, rename } from 'node:fs/promises';
import { startServer } from './serve.mjs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createHash } from 'node:crypto';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const out=path.join(root,'assets/readme/explainer');
const qa=path.join(root,'artifacts/explainer-review');
await mkdir(out,{recursive:true});await mkdir(qa,{recursive:true});
const server=await startServer();
const address=`http://127.0.0.1:${server.address().port}/tools/csweet-explainer/`;
const browser=await chromium.launch({headless:true,args:[`--log-file=${path.join(qa,'chromium.log')}`]});
const errors=[];
try{
 const page=await browser.newPage({viewport:{width:1280,height:720},deviceScaleFactor:1});
 page.on('pageerror',e=>errors.push(e.message));page.on('response',r=>{if(r.status()>=400)errors.push(`${r.status()} ${r.url()}`);});
 await page.goto(address+'animatic.html?render=1');await page.evaluate(()=>window.filmReady);
 const scenes=await page.evaluate(()=>window.filmScenes);
 const frame=async t=>{await page.evaluate(t=>window.renderAt(t),t);return page.screenshot({type:'jpeg',quality:94});};
 for(const [i,s] of scenes.entries()){await frame(s.end-1);await page.screenshot({path:path.join(qa,`${String(i+1).padStart(2,'0')}-${s.id}.png`)});}
 await frame(3.5);await page.screenshot({path:path.join(out,'poster.png')});
 const a=await frame(53.3);await frame(84);const b=await frame(53.3);
 const hash=b=>createHash('sha256').update(b).digest('hex');
 if(hash(a)!==hash(b))throw new Error('Frame depends on prior playback state');
 // Verify review controls and mobile layout before producing the film.
 await page.goto(address+'animatic.html');await page.evaluate(()=>window.filmReady);
 await page.getByRole('button',{name:'Play animatic',exact:true}).click();
 await page.waitForFunction(()=>Number(document.querySelector('#seek').value)>.2);
 await page.getByRole('button',{name:'Pause animatic',exact:true}).click();
 await page.locator('#chapters button').nth(10).click();
 if(await page.locator('#scene-title').textContent()!=='Prove the play.')throw new Error('Chapter navigation failed');
 await page.locator('#seek').fill('27.6');await page.locator('#seek').dispatchEvent('input');
 if(await page.locator('#scene-title').textContent()!=='Put someone behind the vision.')throw new Error('Scrubbing failed');
 await page.setViewportSize({width:390,height:844});
 if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw new Error('Mobile horizontal overflow');
 await page.screenshot({path:path.join(qa,'review-mobile.png'),fullPage:true});
 await page.setViewportSize({width:1280,height:900});await page.screenshot({path:path.join(qa,'review-desktop.png'),fullPage:true});
 await page.setViewportSize({width:1280,height:720});
 await page.goto(address+'animatic.html?render=1');await page.evaluate(()=>window.filmReady);
 const report={width:1280,height:720,fps:24,duration:96,frames:2304,deterministicFrameSha256:hash(a),browserErrors:errors,controls:['play','pause','chapter navigation','scrub','mobile overflow'],scenes};
 if(!process.argv.includes('--inspect-only')){
   const temp=path.join(out,'from-idea-to-play.animatic.partial.mp4');
   const ff=spawn('ffmpeg',['-y','-hide_banner','-loglevel','error','-f','image2pipe','-vcodec','mjpeg','-framerate','24','-i','pipe:0','-an','-vf','scale=in_range=full:out_range=limited','-color_range','tv','-c:v','libx264','-preset','medium','-crf','19','-pix_fmt','yuv420p','-threads','2','-movflags','+faststart',temp],{stdio:['pipe','ignore','pipe'],windowsHide:true});
   let stderr='';ff.stderr.on('data',b=>stderr+=b);ff.stdin.on('error',e=>errors.push(e.message));
   const done=new Promise((resolve,reject)=>{ff.on('error',reject);ff.on('close',c=>c===0?resolve():reject(new Error(stderr)));});
   try{for(let i=0;i<2304;i++){const bytes=await frame(i/24);if(!ff.stdin.write(bytes))await once(ff.stdin,'drain');if(i%240===0)console.log(`Rendering ${Math.round(i/2304*100)}%`);}ff.stdin.end();await done;await rename(temp,path.join(out,'from-idea-to-play.animatic.mp4'));}catch(e){ff.kill();throw e;}
 }
 if(errors.length)throw new Error(errors.join('\n'));
 await writeFile(path.join(qa,'verification.json'),JSON.stringify(report,null,2));
 await writeFile(path.join(out,'chapters.vtt'),'WEBVTT\n\n'+scenes.map((s,i)=>`${i+1}\n${stamp(s.start)} --> ${stamp(s.end)}\n${s.title}\n`).join('\n'));
 console.log(`Verified ${scenes.length} scenes; files in ${out}`);
}finally{await browser.close();server.close();}
function stamp(t){return `00:${String(Math.floor(t/60)).padStart(2,'0')}:${String(t%60).padStart(2,'0')}.000`;}
