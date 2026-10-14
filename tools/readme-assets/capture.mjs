import { createRequire } from 'node:module';
import { mkdir,writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const { chromium }=createRequire(path.resolve(repo,'tools/csweet-promo/package.json'))('playwright');
const output=path.join(repo,'assets/readme/screenshots');await mkdir(output,{recursive:true});
const browser=await chromium.launch({headless:true});const records=[];
try{
    const context=await browser.newContext({viewport:{width:1600,height:1080},deviceScaleFactor:3});
    await context.route('**/*',r=>new URL(r.request().url()).origin==='http://127.0.0.1:5198'?r.continue():r.abort());
    const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
    for(const scene of ['chat','team','board','document']){
        await page.goto(`http://127.0.0.1:5198/demo/software/${scene}`,{waitUntil:'networkidle'});
        await page.locator('#capture').waitFor();await page.waitForFunction(()=>!document.querySelector('.hub-centered,.mud-progress-circular,.mud-progress-linear'));
        await page.evaluate(async()=>{await document.fonts.ready;await Promise.all([...document.querySelectorAll('#capture img')].map(i=>i.decode()));});await page.waitForTimeout(400);
        const capture=async(selector,filename,index=0,cropHeight=null)=>{
            const element=page.locator(selector).nth(index);await element.scrollIntoViewIfNeeded();
            const bounds=await element.boundingBox();
            if(cropHeight)await page.screenshot({path:path.join(output,filename),clip:{...bounds,height:Math.min(cropHeight,bounds.height)}});
            else await element.screenshot({path:path.join(output,filename)});
            records.push({scene,selector,index,file:filename,deviceScaleFactor:3,bounds,cropHeight,sampleData:true});
        };
        if(scene==='chat'){await capture('.hub-main','software-chat.png');await capture('.hub-message.mine .hub-message-bubble','software-chat-focus.png');}
        if(scene==='team'){
            for(let i=0;i<2;i++)await capture('.employee-directory-card',`software-employee-${i}.png`,i);
            await page.setViewportSize({width:430,height:1000});
            await page.addStyleTag({content:'.employee-directory{grid-template-columns:1fr}'});
            for(let i=0;i<2;i++)await capture('.employee-directory-card',`software-employee-${i}-mobile.png`,i,112);
            await page.setViewportSize({width:1600,height:1080});
        }
        if(scene==='board'){await capture('.work-board','software-board.png');for(let i=0;i<3;i++)await capture('.work-ticket',`software-ticket-${i}.png`,i);}
        if(scene==='document'){
            await page.setViewportSize({width:430,height:1000});await page.waitForTimeout(400);
            await capture('.artifact-preview-shell','software-document-content-mobile.png');
        }
        if(errors.length)throw new Error(errors.join('\n'));
    }
    const health=await(await fetch('http://127.0.0.1:5198/health')).json();if(health.fixtureFailures.length)throw new Error(JSON.stringify(health.fixtureFailures));
    await writeFile(path.join(output,'capture-sources.json'),JSON.stringify({campaign:'Northstar Software',theme:'Aurora',fixture:'tools/CSweet.Promo.Demo/DemoData.cs',browserErrors:errors,fixtureFailures:health.fixtureFailures,records},null,2));
    console.log(`Captured ${records.length} current app elements at 3x; zero fixture/runtime failures.`);
}finally{await browser.close();}
