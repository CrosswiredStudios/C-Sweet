import { createRequire } from 'node:module';
import { readFile,writeFile,access } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const { chromium }=createRequire(path.resolve(repo,'tools/csweet-promo/package.json'))('playwright');
const browser=await chromium.launch({headless:true});const results=[];
try{
    for(const theme of ['light','dark'])for(const size of ['desktop','mobile']){
        const page=await browser.newPage({viewport:size==='desktop'?{width:1280,height:1000}:{width:390,height:844},deviceScaleFactor:1});
        const errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('response',r=>{if(r.status()>=400)errors.push(`${r.status()} ${r.url()}`);});
        await page.goto(`http://127.0.0.1:5277/artifacts/readme/README-${theme}.html`);
        await page.evaluate(async()=>{await document.fonts.ready;await Promise.all([...document.images].map(i=>i.decode()));});
        const content=await page.evaluate(()=>({
            overflow:document.documentElement.scrollWidth>innerWidth,
            images:[...document.images].map(i=>({src:i.currentSrc,width:i.naturalWidth,alt:i.alt})),
            links:[...document.querySelectorAll('a[href]')].map(a=>a.getAttribute('href')),
            fragments:[...document.querySelectorAll('a[href^="#"]')].filter(a=>!document.getElementById(a.getAttribute('href').slice(1))).map(a=>a.getAttribute('href')),
            sections:[...document.querySelectorAll('h2')].map(h=>h.textContent)
        }));
        if(content.overflow||content.fragments.length||errors.length)throw new Error(JSON.stringify({theme,size,...content,errors}));
        for(const link of content.links.filter(l=>l.startsWith('/')&&!l.startsWith('//'))){
            const target=path.join(repo,decodeURIComponent(link.split('#')[0].slice(1)));await access(target);
        }
        if(content.images.some(i=>!i.width||!i.alt))throw new Error('Missing image or alt text');
        if(size==='mobile'&&content.images.some(i=>i.src.includes('csweet-')&&!i.src.includes('-mobile')))throw new Error('Mobile artwork was not selected');
        for(const [label,id] of [['top',null],['startup','run-c-sweet-locally'],['first-session','your-first-session'],['features','what-you-can-do'],['control','your-company-your-control']]){
            await page.evaluate(id=>window.scrollTo(0,id?document.getElementById(id).getBoundingClientRect().top+scrollY-24:0),id);
            await page.screenshot({path:path.join(repo,'artifacts/readme',`README-${theme}-${size}-${label}.png`)});
        }
        await page.evaluate(()=>document.querySelectorAll('details').forEach(d=>d.open=true));
        await page.screenshot({path:path.join(repo,'artifacts/readme',`README-${theme}-${size}-full.png`),fullPage:true});
        results.push({theme,size,localLinks:content.links.filter(l=>l.startsWith('/')).length,images:content.images,missingFragments:content.fragments,overflow:content.overflow,errors,sections:content.sections});
        console.log(`Verified ${theme}/${size}: ${content.images.length} responsive images, valid links/anchors, no page overflow.`);await page.close();
    }
    // All image variants are local files, including those not selected by a viewport.
    const readme=await readFile(path.join(repo,'README.md'),'utf8');
    for(const match of readme.matchAll(/(?:src|srcset)="([^"]+)"/g))await access(path.join(repo,match[1]));
}finally{await browser.close();}
await writeFile(path.join(repo,'artifacts/readme','README-verification.json'),JSON.stringify(results,null,2));
