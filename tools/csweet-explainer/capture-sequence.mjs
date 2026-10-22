import {chromium} from '../csweet-promo/node_modules/playwright/index.mjs';
import {writeFile,readFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..'),out=path.join(root,'assets/readme/explainer/captures');
const browser=await chromium.launch({headless:true}),records=[],errors=[];
try{
 const page=await browser.newPage({viewport:{width:1600,height:1400},deviceScaleFactor:2});page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',r=>new URL(r.request().url()).origin==='http://127.0.0.1:5198'?r.continue():r.abort());
 async function snap(selector,name){const el=page.locator(selector).first();await el.scrollIntoViewIfNeeded();await page.evaluate(()=>document.fonts.ready);const bounds=await el.boundingBox();
  const landmarks=await el.locator('img').evaluateAll((imgs,b)=>imgs.filter(i=>i.naturalWidth).map(i=>{const r=i.getBoundingClientRect();return {src:i.getAttribute('src'),x:r.x-b.x,y:r.y-b.y,w:r.width,h:r.height};}),bounds);
  const bytes=await el.screenshot({path:path.join(out,name+'.png')});records.push({file:name+'.png',scene:new URL(page.url()).pathname,selector,bounds,landmarks,scale:2,sha256:createHash('sha256').update(bytes).digest('hex')});
 }
 for(const scene of ['chief','overview','chat-director','director','chat-brief','chat-producer','producer','chat-team','chat-planning','board-empty','board-epic','board-stories','board-tasks','board-progress1','board-progress2','board-progress3','board-complete','chat-ready']){
  await page.goto(`http://127.0.0.1:5198/explainer/${scene}`,{waitUntil:'networkidle'});await page.locator('#capture').waitFor();await page.waitForFunction(()=>!document.querySelector('.hub-centered,.mud-progress-circular,.mud-progress-linear'));
  if(scene==='overview'){
   await snap('.film-overview','overview');await page.getByRole('button',{name:'Choose an answer',exact:true}).click();
   await page.getByText('Creative & Product',{exact:true}).waitFor();
   await page.waitForTimeout(400);
   // Menu portal lies outside the component; capture the same viewport region including it.
   const box=await page.locator('.film-overview').boundingBox();const popup=await page.locator('.mud-popover-open').boundingBox();box.height=Math.max(box.height,popup.y+popup.height-box.y+16);const bytes=await page.screenshot({clip:box,path:path.join(out,'overview-open.png')});
   records.push({...records.at(-1),bounds:box,file:'overview-open.png',sha256:createHash('sha256').update(bytes).digest('hex')});
  }else if(scene==='chief'||scene==='director'||scene==='producer'){
   if(scene==='chief')await page.getByRole('button',{name:'Review Evelyn',exact:true}).click();
   else{await page.locator('.hiring-candidate img').first().waitFor();await snap('.hiring-suggestion-tile',scene+'-card');await page.getByRole('button',{name:'Hire',exact:true}).click();}
   await page.getByRole('button',{name:'Approve and hire',exact:true}).waitFor();await snap('.hire-profile',scene==='chief'?'chief':scene+'-review');
  }else if(scene.startsWith('chat-')){
   await page.locator('.hub-message').first().waitFor();if(['chat-director','chat-producer','chat-team'].includes(scene))await page.locator('.hiring-candidate img').first().waitFor();
   await snap('.hub-main',scene);
   if(scene==='chat-ready')await snap('.hub-message-list','ready-messages');
  }else{
   // The full board is captured for provenance; individual native tickets are motion layers.
   await snap('.work-board',scene);
   const cards=page.locator('.work-ticket');
   if(scene==='board-tasks'||scene==='board-complete'||scene.startsWith('board-progress'))for(let i=0;i<await cards.count();i++){
     const card=cards.nth(i),text=await card.innerText();
     const id=text.match(/#(\d+)/)?.[1];if(!id)throw Error('Ticket identifier missing: '+text);
     const selector=`.work-ticket:nth-match(${i+1})`;
     // Use a temporary selector attribute only to address a node; it has no presentation effects.
     await card.evaluate((el,key)=>el.setAttribute('data-film-ticket',key),id);await snap(`[data-film-ticket="${id}"]`,`${scene==='board-complete'?'done':scene==='board-tasks'?'task':scene.replace('board-','')}-${id}`);
   }
  }
  console.log('Captured sequence '+scene);
 }
 const health=await(await fetch('http://127.0.0.1:5198/health')).json();if(errors.length||health.fixtureFailures.length)throw Error(JSON.stringify({errors,failures:health.fixtureFailures}));
 await writeFile(path.join(out,'sequence-provenance.json'),JSON.stringify({realComponents:true,simplifiedOverviewLayout:true,fixtureFailures:health.fixtureFailures,browserErrors:errors,records},null,2));
}finally{await browser.close();}
