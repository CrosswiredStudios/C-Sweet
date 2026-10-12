import { chromium } from 'playwright';
import { mkdir,writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { campaigns,formats,shotsFor } from './manifest.ts';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../../artifacts/promo');
await mkdir(path.join(root,'qa'),{recursive:true});
const browser=await chromium.launch({headless:true});
const results=[];
// Match FFmpeg's first frame at or after each requested timestamp.
const frameTime=(time:number)=>Math.ceil(time*30-1e-8)/30;
try{for(const campaign of campaigns)for(const format of ['landscape','vertical'] as const){
    const size=formats[format];const page=await browser.newPage({viewport:{width:size.width,height:size.height},deviceScaleFactor:1});
    const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));page.on('response',r=>{if(r.status()>=400)errors.push(`${r.status()} ${r.url()}`);});
    await page.goto(`http://127.0.0.1:5199/?render=1&campaign=${campaign.id}&format=${format}`);
    await page.evaluate(()=>window.promoReady);
    const shots=shotsFor(campaign,format);
    for(const shot of shots){const t=frameTime(shot.start+Math.min(1.8,(shot.end-shot.start)*.7));await page.evaluate(async t=>{window.renderAt(t);await Promise.all([...document.images].map(i=>i.decode()));window.renderAt(t);await Promise.all([...document.images].map(i=>i.decode()));},t);
        if(shot.id==='end')await page.evaluate(()=>{
            const scene=document.querySelector('#incoming .scene')!;
            const brand=scene.querySelector('.brand-ending')!.getBoundingClientRect();
            const copy=scene.querySelector('.copy')!.getBoundingClientRect();
            if(brand.bottom+30>copy.top)throw new Error('Closing logo overlaps the invitation');
        });
        await page.screenshot({path:path.join(root,'qa',`${campaign.id}-${format}-${shot.id}.png`)});}
    // Explicitly examine the midpoint of each transition too.
    for(const shot of shots.slice(1)){await page.evaluate(async t=>{window.renderAt(t);await Promise.all([...document.images].map(i=>i.decode()));window.renderAt(t);await Promise.all([...document.images].map(i=>i.decode()));},frameTime(shot.start+.36));await page.screenshot({path:path.join(root,'qa',`${campaign.id}-${format}-transition-${shot.id}.png`)});}
    if(errors.length)throw new Error(errors.join('\n'));
    results.push({campaign:campaign.id,format,holds:shots.length,transitions:shots.length-1,errors});
    console.log(`Inspected ${campaign.id}/${format}`);await page.close();
}}finally{await browser.close();}
await writeFile(path.join(root,'qa','inspection.json'),JSON.stringify(results,null,2));
