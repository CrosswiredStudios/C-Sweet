import {chromium} from '../csweet-promo/node_modules/playwright/index.mjs';
import {mkdir,writeFile,rename} from 'node:fs/promises';
import {startServer} from './serve.mjs';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import {createHash} from 'node:crypto';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const out=path.join(root,'assets/readme/explainer'),qa=path.join(root,'artifacts/explainer-final');
await mkdir(qa,{recursive:true});
const server=await startServer(),browser=await chromium.launch({headless:true});
const errors=[];
try{
 const page=await browser.newPage({viewport:{width:1920,height:1080},deviceScaleFactor:1});
 page.on('pageerror',e=>errors.push(e.message));page.on('response',r=>{if(r.status()>=400)errors.push(`${r.status()} ${r.url()}`);});
 await page.goto(`http://127.0.0.1:${server.address().port}/tools/csweet-explainer/studio.html`);await page.evaluate(()=>window.filmReady);
 const scenes=await page.evaluate(()=>window.filmScenes);
 const duration=scenes.at(-1).end,frames=duration*30;
 const frame=async t=>{await page.evaluate(t=>window.renderAt(t),t);return page.screenshot({type:'jpeg',quality:94});};
 for(const [i,s] of scenes.entries()){await frame(s.start+(s.end-s.start)*.6);await page.screenshot({path:path.join(qa,`${String(i+1).padStart(2,'0')}-${s.id}.png`)});}
 for(const t of [1,5,9.9,10.7,16.9,17.7,20.5,23,28,31.8,35.8,43,47,51,55,59,64,68,71.5,74,77,80,84,87.9,88.8,93,95.8,96.6,103,105.7,108]){await frame(t);await page.screenshot({path:path.join(qa,`detail-${t}.png`)});}
 await frame(2);await page.screenshot({path:path.join(out,'final-poster.png')});
 const hash=b=>createHash('sha256').update(b).digest('hex');const a=hash(await frame(84.2));await frame(30);if(hash(await frame(84.2))!==a)throw Error('Rendering depends on previous timestamp');
 if(errors.length)throw Error(errors.join('\n'));
 if(!process.argv.includes('--inspect-only')){
  const temp=path.join(out,'from-idea-to-play.partial.mp4');
  const ff=spawn('ffmpeg',['-y','-hide_banner','-loglevel','error','-f','image2pipe','-vcodec','mjpeg','-framerate','30','-i','pipe:0','-i',path.join(root,'artifacts/explainer-review/music/final-score.wav'),'-map','0:v','-map','1:a','-vf','scale=in_range=full:out_range=limited','-color_range','tv','-c:v','libx264','-preset','medium','-crf','18','-pix_fmt','yuv420p','-threads','4','-c:a','aac','-b:a','192k','-t',String(duration),'-movflags','+faststart',temp],{stdio:['pipe','ignore','pipe'],windowsHide:true});
  let stderr='';ff.stderr.on('data',b=>stderr+=b);ff.stdin.on('error',e=>errors.push(e.message));const done=new Promise((resolve,reject)=>{ff.on('error',reject);ff.on('close',n=>n===0?resolve():reject(Error(stderr)));});
  try{for(let i=0;i<frames;i++){const bytes=await frame(i/30);if(!ff.stdin.write(bytes))await once(ff.stdin,'drain');if(i%150===0)console.log(`Rendering ${(i/30).toFixed(0)}/${duration} seconds`);}ff.stdin.end();await done;await rename(temp,path.join(out,'from-idea-to-play.mp4'));}catch(e){ff.kill();throw e;}
 }
 const report={width:1920,height:1080,fps:30,duration,frames,deterministicFrameSha256:a,browserErrors:errors,scenes};
 await writeFile(path.join(qa,'verification.json'),JSON.stringify(report,null,2));
 const stamp=t=>`00:${String(Math.floor(t/60)).padStart(2,'0')}:${String(t%60).padStart(2,'0')}.000`;
 await writeFile(path.join(out,'final-chapters.vtt'),'WEBVTT\n\n'+scenes.map((s,i)=>`${i+1}\n${stamp(s.start)} --> ${stamp(s.end)}\n${s.title}\n`).join('\n'));
 await writeFile(path.join(out,'transcript.txt'),'C-Sweet — From an idea to play\n112 seconds · Original instrumental score; no spoken dialogue.\nReal app components with fictional project data. Condensed illustrative workflow.\nPrism Break is a playable example authored for this film, not a recorded autonomous delivery.\n\n'+scenes.map(s=>`${stamp(s.start)} – ${stamp(s.end)}\n${s.title}\n${s.description}\n`).join('\n'));
 console.log('Final film inspection passed.');
}finally{await browser.close();server.close();}
