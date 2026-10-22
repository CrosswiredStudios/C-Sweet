import {chromium} from '../csweet-promo/node_modules/playwright/index.mjs';
import {startServer} from './serve.mjs';
import {readFile,writeFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..'),qa=path.join(root,'artifacts/explainer-final');
const server=await startServer(),base=`http://127.0.0.1:${server.address().port}`,browser=await chromium.launch({headless:true});const errors=[];
try{
 const page=await browser.newPage({viewport:{width:1440,height:1100}});page.on('pageerror',e=>errors.push(e.message));
 for(const p of ['/README.md','/.env','/.git/config','/src/CSweet.Api/appsettings.json'])if((await page.request.get(base+p)).status()!==404)throw Error(`Unexpected exposure ${p}`);
 const playback=[];
 for(const [name,duration,width,height] of (process.argv.includes('--controls-only')?[]:[['from-idea-to-play.mp4',112,1920,1080],['from-idea-to-play.vertical.mp4',30,1080,1920]])){
  await page.setContent(`<style>body{margin:0;background:#110e20}video{display:block;max-width:100vw;max-height:100vh;margin:auto}</style><video controls playsinline src="${base}/assets/readme/explainer/${name}"></video>`);
  await page.waitForFunction(()=>document.querySelector('video').readyState>=2);
  const info=await page.evaluate(()=>{const v=document.querySelector('video');return {duration:v.duration,width:v.videoWidth,height:v.videoHeight};});
  if(Math.abs(info.duration-duration)>.05||info.width!==width||info.height!==height)throw Error('Unexpected video metadata');
  const rate=process.argv.includes('--normal-speed')?1:2;
  await page.evaluate(async rate=>{const v=document.querySelector('video');v.playbackRate=rate;await v.play();},rate);
  if(duration/rate>60){await page.waitForFunction(()=>document.querySelector('video').currentTime>=56,{}, {timeout:60000});console.log('Normal-speed playback: halfway through landscape film.');}
  await page.waitForFunction(()=>document.querySelector('video').ended,{}, {timeout:60000});
  const result=await page.evaluate(()=>{const v=document.querySelector('video'),q=v.getVideoPlaybackQuality();return {ended:v.ended,error:v.error?.message??null,totalFrames:q.totalVideoFrames,droppedFrames:q.droppedVideoFrames};});
  if(result.error)throw Error(result.error);playback.push({name,playbackRate:rate,...info,...result});console.log(`Complete browser playback: ${name}`);
 }
 await page.goto(base+'/tools/csweet-explainer/index.html');await page.waitForFunction(()=>document.querySelector('video').readyState>=1);
 await page.getByRole('button',{name:'1:36 · Play',exact:true}).click();await page.waitForFunction(()=>{const v=document.querySelector('video');return v.currentTime>=96&&v.currentTime<98&&!v.seeking&&!v.paused&&v.readyState>=2;});await page.evaluate(()=>document.querySelector('video').pause());
 for(const width of [1440,390]){await page.setViewportSize({width,height:1000});if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Player overflow');await page.screenshot({path:path.join(qa,`player-${width}.png`),fullPage:true});}
 await page.goto(base+'/tools/csweet-explainer/game.html');await page.waitForFunction(()=>window.gameTest);
 await page.getByRole('button',{name:'Play',exact:true}).click();
 await page.keyboard.down('ArrowRight');await page.waitForFunction(()=>window.gameTest.game.paddle>850);await page.keyboard.up('ArrowRight');
 await page.getByRole('button',{name:'Pause',exact:true}).click();const before=await page.evaluate(()=>window.gameTest.game.time);await page.waitForTimeout(200);if(await page.evaluate(()=>window.gameTest.game.time)!==before)throw Error('Pause did not stop game');
 await page.getByRole('button',{name:'Restart',exact:true}).click();await page.waitForFunction(()=>window.gameTest.game.time<1&&window.gameTest.game.score===0&&window.gameTest.game.lives===3);
 if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Game mobile overflow');
 await page.screenshot({path:path.join(qa,'game-mobile.png'),fullPage:true});
 if(errors.length)throw Error(errors.join('\n'));
 const file=path.join(qa,'verification.json'),report=JSON.parse(await readFile(file,'utf8'));if(playback.length)report.browserPlayback=playback;report.playerChecks=['chapter seek','desktop/mobile layout','game keyboard movement','game pause','game restart','server allowlist'];report.browserErrors=errors;
 await writeFile(file,JSON.stringify(report,null,2));console.log(JSON.stringify({playback,checks:report.playerChecks}));
}finally{await browser.close();server.close();}
