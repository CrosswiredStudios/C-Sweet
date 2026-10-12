import { spawn } from 'node:child_process';
import { readFile,writeFile,mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { campaigns,formats,shotsFor } from './manifest.ts';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../../artifacts/promo');
const qa=path.join(root,'qa');await mkdir(qa,{recursive:true});
function command(exe:string,args:string[]):Promise<{out:string;err:string}>{return new Promise((resolve,reject)=>{
    const p=spawn(exe,args,{windowsHide:true});let out='',err='';p.stdout.on('data',b=>out+=b);p.stderr.on('data',b=>err+=b);p.on('error',reject);p.on('close',code=>code?reject(new Error(`${exe}: ${err}`)):resolve({out,err}));
});}
const jobs=campaigns.flatMap(c=>(['landscape','vertical'] as const).map(f=>({c,f,name:`${c.id}-${f}`})));
const reports:Record<string,unknown>[]=[];
for(const {c,f,name} of jobs){
    const expected=formats[f],file=path.join(root,'exports',`${name}.mp4`);
    const probe=JSON.parse((await command('ffprobe',['-v','error','-show_streams','-show_format','-of','json',file])).out);
    const v=probe.streams.find((s:any)=>s.codec_type==='video'),a=probe.streams.find((s:any)=>s.codec_type==='audio');
    const assert=(ok:boolean,message:string)=>{if(!ok)throw new Error(`${name}: ${message}`);};
    assert(v.width===expected.width&&v.height===expected.height,'incorrect dimensions');
    assert(v.codec_name==='h264'&&['yuv420p','yuvj420p'].includes(v.pix_fmt)&&a.codec_name==='aac','incorrect playback codecs');
    assert(v.avg_frame_rate==='30/1'&&Number(v.nb_frames)===expected.duration*30,'incorrect frame rate/count');
    assert(Math.abs(Number(probe.format.duration)-expected.duration)<.04,'incorrect duration');
    assert(a.sample_rate==='48000'&&a.channels===2,'incorrect audio format');
    const bytes=await readFile(file);let offset=0,moov=-1,mdat=-1;
    while(offset+8<=bytes.length){const size=bytes.readUInt32BE(offset);const type=bytes.toString('ascii',offset+4,offset+8);if(type==='moov')moov=offset;if(type==='mdat')mdat=offset;if(size<8)break;offset+=size;}
    assert(moov>=0&&mdat>moov,'missing faststart metadata');
    const decoded=await command('ffmpeg',['-hide_banner','-v','error','-i',file,'-f','null','-']);assert(decoded.err.trim()==='','decode errors');
    const measure=await command('ffmpeg',['-hide_banner','-i',file,'-af','loudnorm=I=-16:TP=-1:LRA=9:print_format=json','-f','null','-']);
    const jsonStart=measure.err.lastIndexOf('{');
    const loudness=JSON.parse(measure.err.slice(jsonStart,measure.err.indexOf('}',jsonStart)+1));
    assert(Math.abs(Number(loudness.input_i)+16)<=.5,'integrated loudness outside -16 ±0.5 LUFS');
    assert(Number(loudness.input_tp)<=-1,'true peak exceeds -1 dBTP');
    const visual=await command('ffmpeg',['-hide_banner','-i',file,'-vf','blackdetect=d=0.033:pix_th=0.01:pic_th=0.98','-an','-f','null','-']);
    assert(!visual.err.includes('black_start:'),'blank/black frame interval');
    const deterministic=JSON.parse(await readFile(path.join(qa,`${name}.render.json`),'utf8'));
    assert(!!deterministic.deterministicFrameSha256&&deterministic.browserErrors.length===0,'render consistency failed');
    // Save decoded holds and transition midpoints so QA examines the delivered codec as well as the compositor.
    for(const shot of shotsFor(c,f))for(const [kind,t] of [['hold',shot.start+Math.min(1.8,(shot.end-shot.start)*.7)],['transition',shot.start+.36]] as const){
        if(kind==='transition'&&shot.start===0)continue;
        await command('ffmpeg',['-y','-hide_banner','-v','error','-ss',String(t),'-i',file,'-frames:v','1',path.join(qa,`${name}-encoded-${kind}-${shot.id}.png`)]);
    }
    reports.push({name,width:v.width,height:v.height,duration:Number(probe.format.duration),fps:v.avg_frame_rate,frames:Number(v.nb_frames),video:v.codec_name,pixelFormat:v.pix_fmt,audio:a.codec_name,faststart:true,decodeErrors:0,blackIntervals:0,integratedLUFS:Number(loudness.input_i),truePeakDbTP:Number(loudness.input_tp),deterministicFrameSha256:deterministic.deterministicFrameSha256});
    console.log(`Verified ${name}: ${loudness.input_i} LUFS / ${loudness.input_tp} dBTP`);
}
await writeFile(path.join(qa,'verification.json'),JSON.stringify(reports,null,2));

// Continuous real-time browser playback of each final file, with sound enabled and muted.
// This verifies decoding, synchronization and completion; listening quality remains an editorial judgment.
const browser=await chromium.launch({headless:true,args:['--autoplay-policy=no-user-gesture-required','--disable-background-timer-throttling']});
const queue=[...jobs];const playback:object[]=[];
async function watch(){while(queue.length){const job=queue.shift()!;const page=await browser.newPage();
    try{
        await page.goto('http://127.0.0.1:5199/gallery.html');
        for(const muted of [false,true]){
            const result=await page.evaluate(async({name,muted})=>{
                const video=document.createElement('video');document.body.replaceChildren(video);video.style.width='100%';video.src=`/exports/${name}.mp4`;video.muted=muted;video.controls=true;video.preload='auto';
                let stalls=0;video.addEventListener('waiting',()=>stalls++);
                const ended=new Promise<void>((resolve,reject)=>{video.addEventListener('ended',()=>resolve(),{once:true});video.addEventListener('error',()=>reject(new Error(video.error?.message)),{once:true});setTimeout(()=>reject(new Error('Playback timed out')),75000);});
                await video.play();await ended;
                const q=video.getVideoPlaybackQuality();
                return {duration:video.duration,currentTime:video.currentTime,width:video.videoWidth,height:video.videoHeight,decodedFrames:q.totalVideoFrames,droppedFrames:q.droppedVideoFrames,stalls,ended:video.ended,muted};
            },{name:job.name,muted});
            if(!result.ended||Math.abs(result.currentTime-formats[job.f].duration)>.05)throw new Error(`${job.name}: playback incomplete`);
            playback.push({name:job.name,...result});console.log(`Played ${job.name} ${muted?'muted':'with audio'}`);
        }
    }finally{await page.close();}
}}
try{await Promise.all([watch(),watch(),watch()]);}finally{await browser.close();}
await writeFile(path.join(qa,'playback.json'),JSON.stringify(playback,null,2));
console.log('All six exports passed file checks and continuous audio/muted playback.');
