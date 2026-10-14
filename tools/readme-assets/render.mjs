import { createRequire } from 'node:module';
import { mkdir,writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url)),repo=path.resolve(here,'../..');
const { chromium }=createRequire(path.resolve(repo,'tools/csweet-promo/package.json'))('playwright');
const browser=await chromium.launch({headless:true});
const out=path.join(repo,'artifacts/readme');await mkdir(out,{recursive:true});
const reports=[];
try{
    for(const [name,width,height] of [['hero',1600,940],['hero-mobile',800,1170],['workspace',1600,960],['workspace-mobile',800,3000]]){
        const page=await browser.newPage({viewport:{width,height},deviceScaleFactor:1});
        const errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('response',r=>{if(r.status()>=400)errors.push(`${r.status()} ${r.url()}`);});
        await page.goto(`http://127.0.0.1:5277/artifacts/readme/${name}.html`);
        await page.evaluate(async()=>{await document.fonts.ready;await Promise.all([...document.images].map(i=>i.decode()));});
        await page.evaluate(()=>{
            const title=document.querySelector('h1').getBoundingClientRect();
            const subtitle=document.querySelector('.intro,.subtitle').getBoundingClientRect();
            if(title.bottom>subtitle.top)throw new Error('Artwork title overlaps supporting copy');
            const first=document.querySelector('.feature');
            if(first&&subtitle.bottom>first.getBoundingClientRect().top)throw new Error('Artwork copy overlaps feature cards');
            for(const box of document.querySelectorAll('.feature.people .box,.feature.work .box')){
                const limit=box.getBoundingClientRect().bottom;
                for(const image of box.querySelectorAll('img'))if(image.getBoundingClientRect().bottom>limit)throw new Error('App card is clipped');
            }
        });
        if(errors.length)throw new Error(errors.join('\n'));
        const filename=`csweet-aurora-${name}.png`;
        await page.screenshot({path:path.join(repo,'assets/readme',filename)});
        reports.push({asset:filename,width,height,errors});await page.close();
    }
    for(const filename of ['csweet-company-workflow','csweet-company-workflow-mobile','csweet-security','csweet-security-mobile']){
        const page=await browser.newPage();await page.goto(`http://127.0.0.1:5277/assets/readme/${filename}.svg`);
        const result=await page.evaluate(()=>{
            const svg=document.querySelector('svg'),box=svg.viewBox.baseVal;
            const overflow=[...svg.querySelectorAll('text')].filter(t=>{const b=t.getBBox();return b.x<0||b.y<0||b.x+b.width>box.width||b.y+b.height>box.height}).map(t=>t.textContent);
            return {width:box.width,height:box.height,overflow};
        });
        if(result.overflow.length)throw new Error(`${filename}: text overflow ${result.overflow}`);
        await page.setViewportSize({width:result.width,height:result.height});
        await page.screenshot({path:path.join(out,`${filename}.png`)});reports.push({asset:filename,...result});await page.close();
    }
}finally{await browser.close();}
await writeFile(path.join(out,'artwork-verification.json'),JSON.stringify(reports,null,2));
console.log('Rendered four app compositions and verified four SVG diagrams.');
