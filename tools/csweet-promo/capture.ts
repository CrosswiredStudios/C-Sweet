import { chromium } from 'playwright';
import { mkdir, writeFile, copyFile, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../..');
const output = path.join(repo, 'artifacts/promo/captures');
await mkdir(output, { recursive: true });
const base = process.env.DEMO_URL || 'http://127.0.0.1:5198';
const browser = await chromium.launch({ headless: true });
type CaptureSource={campaign:string;scene:string;selector:string;bounds?:{x:number;y:number;width:number;height:number}|null;capturedFrom?:string;fixture?:string;sampleData:boolean;viewport?:string};
const sources:CaptureSource[] = [];
const campaignFilter = process.argv.find(a=>a.startsWith('--campaign='))?.split('=')[1];
const sceneFilter = process.argv.find(a=>a.startsWith('--scene='))?.split('=')[1];
try {
for (const campaign of ['software', 'youtube', 'game'].filter(c=>!campaignFilter||c===campaignFilter)) {
    const context = await browser.newContext({ viewport: { width: 1600, height: 1080 }, deviceScaleFactor: 1 });
    // This renderer is restricted to the fixture server. No product/provider network is permitted.
    await context.route('**/*', route => new URL(route.request().url()).origin === new URL(base).origin ? route.continue() : route.abort());
    await context.addInitScript(theme => { localStorage.setItem('csweet.appearance', theme); }, campaign === 'game' ? 'gold' : 'aurora');
    const page = await context.newPage();
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    const scenes = (campaign === 'youtube' ? ['chat', 'plan', 'team', 'board', 'calendar', 'approval', 'report'] : ['chat', 'plan', 'team', 'board', 'document','evidence']).filter(s=>!sceneFilter||s===sceneFilter);
    for (const scene of scenes) {
        await page.goto(`${base}/demo/${campaign}/${scene}`, { waitUntil: 'networkidle' });
        await page.locator('#capture').waitFor();
        await page.waitForFunction(() => !document.querySelector('.hub-centered,.mud-progress-circular,.mud-progress-linear'));
        await page.evaluate(async () => { await document.fonts.ready; await Promise.all([...document.images].map(img => img.decode().catch(() => {}))); });
        // Allow browser interop (portraits, board scroll, chat scroll) to complete before freezing a capture.
        await page.waitForTimeout(650);
        const failure = await page.locator('[role="alert"]').allTextContents();
        if (failure.some(t => /could not|unavailable|unmapped/i.test(t))) throw new Error(`${campaign}/${scene}: ${failure.join('; ')}`);
        if (errors.length) throw new Error(`${campaign}/${scene}: ${errors.join('; ')}`);
        const selector = ({ chat: '.hub-main', plan: '.project-overview-grid', team: '.employee-directory', board: '.work-board', document: '.artifact-workspace', report: '.artifact-workspace', approval: '.demo-approval > .mud-paper', calendar: '.calendar-page', evidence:'.resource-section' } as Record<string,string>)[scene];
        await page.screenshot({ path: path.join(output, `${campaign}-${scene}-screen.png`) });
        let target = page.locator(selector);
        if (!await target.count()) throw new Error(`Missing capture selector ${selector}`);
        await target.screenshot({ path: path.join(output, `${campaign}-${scene}.png`) });
        const bounds = await target.boundingBox();
        const focusSelector = scene === 'chat' ? '.hub-message.mine' : scene === 'plan' ? '.brief-panel' : scene === 'team' ? '.employee-directory-card' : scene === 'board' ? '.work-ticket' : null;
        if (focusSelector && await page.locator(focusSelector).count()) await page.locator(focusSelector).first().screenshot({ path: path.join(output, `${campaign}-${scene}-focus.png`) });
        if(scene==='team') for(let i=0;i<await page.locator('.employee-directory-card').count();i++) await page.locator('.employee-directory-card').nth(i).screenshot({path:path.join(output,`${campaign}-employee-${i}.png`)});
        if(scene==='board') for(let i=0;i<Math.min(3,await page.locator('.work-ticket').count());i++) await page.locator('.work-ticket').nth(i).screenshot({path:path.join(output,`${campaign}-ticket-${i}.png`)});
        sources.push({ campaign, scene, selector, bounds, capturedFrom: `${base}/demo/${campaign}/${scene}`, fixture: 'DemoData.cs', sampleData: true });
        console.log(`Captured ${campaign}/${scene}`);
    }
    await context.close();
    const mobile = await browser.newContext({ viewport: { width: 430, height: 1000 }, deviceScaleFactor: 3 });
    await mobile.route('**/*', route => new URL(route.request().url()).origin === new URL(base).origin ? route.continue() : route.abort());
    await mobile.addInitScript(theme => { localStorage.setItem('csweet.appearance', theme); }, campaign === 'game' ? 'gold' : 'aurora');
    const phone = await mobile.newPage();
    phone.on('pageerror',error=>errors.push(error.message));
    for (const scene of scenes) {
        await phone.goto(`${base}/demo/${campaign}/${scene}${scene==='calendar'?'?view=Agenda':''}`, { waitUntil: 'networkidle' });
        await phone.locator('#capture').waitFor();
        await phone.waitForFunction(() => !document.querySelector('.hub-centered,.mud-progress-circular,.mud-progress-linear'));
        await phone.evaluate(async () => { await document.fonts.ready; await Promise.all([...document.images].map(img => img.decode().catch(() => {}))); });
        await phone.waitForTimeout(500);
        if(errors.length)throw new Error(`${campaign}/${scene}/mobile: ${errors.join('; ')}`);
        const selector = ({ chat: '.hub-main', plan: '.brief-panel', team: '.employee-directory', board: '.work-board', document: '.artifact-workspace', report: '.artifact-workspace', approval: '.demo-approval > .mud-paper', calendar: '.calendar-page', evidence:'.resource-section' } as Record<string,string>)[scene];
        await phone.locator(selector).screenshot({path:path.join(output,`${campaign}-${scene}-mobile.png`)});
        if(scene==='team') {
            // Stage geometry only: each original card gets its full mobile width.
            await phone.addStyleTag({content:'.employee-directory { grid-template-columns:1fr; }'});
            for(let i=0;i<await phone.locator('.employee-directory-card').count();i++) {
                const card=phone.locator('.employee-directory-card').nth(i);await card.scrollIntoViewIfNeeded();const b=await card.boundingBox();
                if(!b)throw new Error('Missing employee card bounds');
                await phone.screenshot({path:path.join(output,`${campaign}-employee-${i}-mobile.png`),clip:{x:b.x,y:b.y,width:b.width,height:Math.min(112,b.height)}});
            }
        }
        if(scene==='document'||scene==='report') await phone.locator('.artifact-preview-shell').screenshot({path:path.join(output,`${campaign}-${scene}-content-mobile.png`)});
        if(scene==='board') await phone.locator('.work-ticket').first().screenshot({path:path.join(output,`${campaign}-board-focus-mobile.png`)});
        if(scene==='calendar') await phone.locator('.calendar-agenda').screenshot({path:path.join(output,`${campaign}-calendar-agenda-mobile.png`)});
        sources.push({campaign,scene,selector,viewport:'430 CSS pixels / 3x',sampleData:true});
        console.log(`Captured mobile ${campaign}/${scene}`);
    }
    await mobile.close();
}
const health = await (await fetch(`${base}/health`)).json();
if (health.fixtureFailures.length) throw new Error(`Fixture coverage incomplete: ${JSON.stringify(health.fixtureFailures)}`);
const prior = campaignFilter||sceneFilter ? JSON.parse(await readFile(path.join(output,'capture-index.json'),'utf8').catch(()=>'[]')) : [];
const records = [...prior.filter((p:{campaign:string,scene:string,viewport?:string})=>!sources.some(s=>s.campaign===p.campaign&&s.scene===p.scene&&('viewport' in s?s.viewport:undefined)===p.viewport)),...sources];
await writeFile(path.join(output, 'capture-index.json'), JSON.stringify(records, null, 2));
await copyFile(path.join(repo, 'src/CSweet.App/wwwroot/images/icon_dark.svg'), path.join(output, 'logo.svg'));
await copyFile(path.join(repo, 'src/CSweet.App/wwwroot/images/icon.svg'), path.join(output, 'logo-light.svg'));
for (const name of ['chief-of-staff','product-manager','software-architect','software-developer','software-qa','youtube-account-manager','video-game-creative-director','video-game-producer','video-game-engineer','video-game-qa']) await copyFile(path.join(repo,`src/CSweet.UI/wwwroot/images/agents/${name}-v1.jpg`),path.join(output,`${name}.jpg`));
} finally { await browser.close(); }
