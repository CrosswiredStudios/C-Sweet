import { chromium } from '../csweet-promo/node_modules/playwright/index.mjs';
import { mkdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const out=path.join(root,'assets/readme/explainer/captures');await mkdir(out,{recursive:true});
const browser=await chromium.launch({headless:true,args:[`--log-file=${path.join(root,'artifacts/explainer-review/chromium.log')}`]});
const errors=[],records=[];
try{
 const context=await browser.newContext({viewport:{width:1600,height:1200},deviceScaleFactor:2});
 await context.route('**/*',r=>new URL(r.request().url()).origin==='http://127.0.0.1:5198'?r.continue():r.abort());
 const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
 const capture=async(selector,name,index=0)=>{
  const el=page.locator(selector).nth(index);await el.scrollIntoViewIfNeeded();
  await page.evaluate(async()=>{await document.fonts.ready;await Promise.all([...document.images].filter(i=>i.complete&&i.naturalWidth).map(i=>i.decode()));});
  const box=await el.boundingBox();const bytes=await el.screenshot({path:path.join(out,name+'.png')});
  records.push({file:name+'.png',scene:new URL(page.url()).pathname,selector,index,bounds:box,scale:2,sha256:createHash('sha256').update(bytes).digest('hex')});
 };
 for(const scene of ['chief','focus','chat','director','hired','staffing','team','plan','board-ready','board-work','board-review','board-qa','board-done','ticket','implementation','technical','quality']){
  await page.goto(`http://127.0.0.1:5198/explainer/${scene}`,{waitUntil:'networkidle'});
  await page.locator('#capture').waitFor();
  await page.waitForFunction(()=>!document.querySelector('.hub-centered,.mud-progress-circular,.mud-progress-linear'));
  if(scene==='chief'){
   await page.getByRole('button',{name:'Review Evelyn',exact:true}).click();
   await page.getByRole('button',{name:'Approve and hire',exact:true}).waitFor();
   await capture('.hire-profile','chief');
  }else if(scene==='focus'){
   await page.locator('.question-card summary').click();
   await capture('.question-card','focus');
   await page.getByRole('radio').first().check();
   await page.waitForFunction(()=>[...document.querySelectorAll('button')].some(b=>b.textContent.trim()==='Send response'&&!b.disabled));
   await capture('.question-card','focus-selected');
  }else if(scene==='director'||scene==='hired'||scene==='staffing'){
   await page.locator('.hiring-candidate img').first().waitFor();
   await capture('.hiring-suggestion-carousel',scene);
   if(scene==='director')await capture('.hiring-suggestion-tile','director-card');
   if(scene==='director'){
    await page.getByRole('button',{name:'Hire',exact:true}).click();await page.getByRole('button',{name:'Approve and hire',exact:true}).waitFor();
    await capture('.hire-profile','director-review');
   }
   if(scene==='staffing')for(let i=0;i<4;i++)await capture('.hiring-suggestion-tile',`suggestion-${i}`,i);
  }else if(scene==='team'){
   await capture('.employee-directory','team');for(let i=0;i<4;i++)await capture('.employee-directory-card',`employee-${i}`,i);
  }else if(scene.startsWith('board-')){
   await capture('.work-board',scene);if(scene==='board-work')await capture('.work-ticket','ticket-card');
  }else if(scene==='ticket')await capture('.editorial-ticket','ticket');
  else if(scene==='chat')await capture('.hub-main','chat');
  else {await page.locator('.artifact-preview-shell').waitFor();await capture('.artifact-preview-shell',scene);}
  if(errors.length)throw new Error(errors.join('\n'));
  console.log(`Captured ${scene}`);
 }
 const health=await(await fetch('http://127.0.0.1:5198/health')).json();
 if(health.fixtureFailures.length)throw new Error(JSON.stringify(health.fixtureFailures));
 await writeFile(path.join(out,'provenance.json'),JSON.stringify({fixture:'tools/CSweet.Promo.Demo/DemoData.Explainer.cs',theme:'Aurora',sampleData:true,componentStylesUnmodified:true,browserErrors:errors,fixtureFailures:health.fixtureFailures,records},null,2));
 console.log(`${records.length} captures; zero fixture/runtime failures.`);
}finally{await browser.close();}
