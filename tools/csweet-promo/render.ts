import { chromium } from 'playwright';
import { mkdir,writeFile,rename } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { campaigns,formats,shotsFor } from './manifest.ts';
import type { Campaign,Format } from './manifest.ts';

const here=path.dirname(fileURLToPath(import.meta.url));
const root=path.resolve(here,'../../artifacts/promo');
const output=path.join(root,'exports');
const qa=path.join(root,'qa');
await mkdir(output,{recursive:true});await mkdir(qa,{recursive:true});
const filter=process.argv.find(a=>a.startsWith('--campaign='))?.split('=')[1];
const formatFilter=process.argv.find(a=>a.startsWith('--format='))?.split('=')[1];
const browser=await chromium.launch({headless:true,args:['--disable-background-timer-throttling','--disable-renderer-backgrounding']});
const jobs=campaigns.filter(c=>!filter||c.id===filter).flatMap(c=>(['landscape','vertical'] as const).filter(f=>!formatFilter||f===formatFilter).map(f=>({c,f})));
if(!jobs.length)throw new Error('No matching campaign/format.');
const results:object[]=[];

async function render(c:Campaign,f:Format){
    const spec=formats[f];const name=`${c.id}-${f}`;const temp=path.join(output,`${name}.partial.mp4`);const target=path.join(output,`${name}.mp4`);
    const page=await browser.newPage({viewport:{width:spec.width,height:spec.height},deviceScaleFactor:1});
    const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));page.on('response',r=>{if(r.status()>=400)errors.push(`${r.status()} ${r.url()}`);});
    await page.goto(`http://127.0.0.1:5199/?render=1&campaign=${c.id}&format=${f}`);await page.evaluate(()=>window.promoReady);
    const frame=async(t:number)=>page.evaluate(async t=>{window.renderAt(t);await Promise.all([...document.images].map(i=>i.decode()));},t);
    await frame(1.8);await page.screenshot({path:path.join(output,`${name}.poster.png`)});
    // Re-enter the same frame after crossing scene boundaries: no wall-clock animation or random state.
    await frame(spec.duration*.27);const first=await page.screenshot();await frame(spec.duration*.7);await frame(spec.duration*.27);const second=await page.screenshot();
    const hash=(b:Buffer)=>createHash('sha256').update(b).digest('hex');
    if(hash(first)!==hash(second))throw new Error(`${name}: non-deterministic frame`);
    const ffmpeg=spawn('ffmpeg',['-y','-hide_banner','-loglevel','error','-f','image2pipe','-vcodec','mjpeg','-framerate',String(spec.fps),'-i','pipe:0','-i',path.join(root,'audio',`${name}.wav`),'-map','0:v:0','-map','1:a:0','-c:v','libx264','-preset','medium','-crf','18','-pix_fmt','yuv420p','-threads','2','-c:a','aac','-b:a','192k','-ar','48000','-t',String(spec.duration),'-movflags','+faststart',temp],{stdio:['pipe','ignore','pipe'],windowsHide:true});
    let encodingError='';ffmpeg.stderr.on('data',chunk=>encodingError+=chunk.toString());
    const done=new Promise<number>((resolve,reject)=>{ffmpeg.on('error',reject);ffmpeg.on('close',code=>resolve(code??1));});
    const start=Date.now();
    try{
        for(let i=0;i<spec.duration*spec.fps;i++){
            await frame(i/spec.fps);
            const image=await page.screenshot({type:'jpeg',quality:96});
            if(!ffmpeg.stdin.write(image))await once(ffmpeg.stdin,'drain');
            if(i%150===0)console.log(`${name}: ${Math.round(i/(spec.duration*spec.fps)*100)}%`);
        }
        ffmpeg.stdin.end();const code=await done;if(code)throw new Error(encodingError||`FFmpeg exited ${code}`);
        if(errors.length)throw new Error(errors.join('\n'));
        await rename(temp,target);
        const report={name,width:spec.width,height:spec.height,duration:spec.duration,fps:spec.fps,frames:spec.duration*spec.fps,deterministicFrameSha256:hash(first),browserErrors:errors,renderSeconds:Math.round((Date.now()-start)/1000),shots:shotsFor(c,f)};
        await writeFile(path.join(qa,`${name}.render.json`),JSON.stringify(report,null,2));
        results.push(report);console.log(`Finished ${name}`);
    }catch(error){ffmpeg.kill();throw error;}finally{await page.close();}
}
// Two encoders keep resource use bounded while independent videos render concurrently.
async function worker(){while(jobs.length){const job=jobs.shift()!;await render(job.c,job.f);}}
try{await Promise.all([worker(),worker()]);}finally{await browser.close();}
await writeFile(path.join(qa,'render-index.json'),JSON.stringify(results,null,2));
