import {chromium} from '../csweet-promo/node_modules/playwright/index.mjs';
import {startServer} from './serve.mjs';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import {mkdir,writeFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..'),qa=path.join(root,'artifacts/explainer-final/social');await mkdir(qa,{recursive:true});
const server=await startServer(),browser=await chromium.launch({headless:true});const errors=[];
try{
 const page=await browser.newPage({viewport:{width:1080,height:1920}});page.on('pageerror',e=>errors.push(e.message));
 await page.goto(`http://127.0.0.1:${server.address().port}/tools/csweet-explainer/studio.html`);await page.evaluate(()=>window.filmReady);
 const frame=async t=>{await page.evaluate(t=>window.renderVerticalAt(t),t);return page.screenshot({type:'jpeg',quality:93});};
 for(const t of [2,5,8.5,12,16,20,24,28]){await frame(t);await page.screenshot({path:path.join(qa,`${t}.png`)});}
 if(!process.argv.includes('--inspect-only')){
  const filter='[1:a]asplit=3[a][b][c];[a]atrim=0:23,asetpts=PTS-STARTPTS,afade=t=out:st=22.9:d=0.1[a1];[b]atrim=96:100,asetpts=PTS-STARTPTS,afade=t=in:d=0.05,afade=t=out:st=3.9:d=0.1[b1];[c]atrim=109:112,asetpts=PTS-STARTPTS,afade=t=in:d=0.05[c1];[a1][b1][c1]concat=n=3:v=0:a=1[music]';
  const ff=spawn('ffmpeg',['-y','-hide_banner','-loglevel','error','-f','image2pipe','-vcodec','mjpeg','-framerate','30','-i','pipe:0','-i',path.join(root,'artifacts/explainer-review/music/final-score.wav'),'-filter_complex',filter,'-map','0:v','-map','[music]','-vf','scale=in_range=full:out_range=limited','-color_range','tv','-c:v','libx264','-preset','medium','-crf','18','-pix_fmt','yuv420p','-threads','3','-c:a','aac','-b:a','192k','-t','30','-movflags','+faststart',path.join(root,'assets/readme/explainer/from-idea-to-play.vertical.mp4')],{stdio:['pipe','ignore','pipe'],windowsHide:true});
  let stderr='';ff.stderr.on('data',b=>stderr+=b);ff.stdin.on('error',e=>errors.push(e.message));const done=new Promise((resolve,reject)=>{ff.on('error',reject);ff.on('close',n=>n?reject(Error(stderr)):resolve());});
  for(let i=0;i<900;i++){const bytes=await frame(i/30);if(!ff.stdin.write(bytes))await once(ff.stdin,'drain');if(i%150===0)console.log(`Vertical ${i/30}/30 seconds`);}ff.stdin.end();await done;
 }
 if(errors.length)throw Error(errors.join('\n'));await writeFile(path.join(qa,'verification.json'),JSON.stringify({width:1080,height:1920,fps:30,duration:30,browserErrors:errors},null,2));
 console.log('Vertical cut verified.');
}finally{await browser.close();server.close();}
