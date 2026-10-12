import { campaigns, formats, shotsFor } from './manifest.ts';
import type { Campaign, Format, Shot } from './manifest.ts';
import { atTime, clamp, ease, transitionDuration } from './timeline.ts';

declare global { interface Window { promoReady: Promise<void>; renderAt:(time:number)=>void; promoState:()=>object; } }
const params = new URLSearchParams(location.search);
const isRender = params.get('render') === '1';
let campaign = campaigns.find(c=>c.id===params.get('campaign')) || campaigns[0];
let format:Format = params.get('format') === 'vertical' ? 'vertical' : 'landscape';
let shots = shotsFor(campaign,format);
let time=0,playing=false,last=0,sceneKey='';
const stage=document.querySelector<HTMLElement>('#stage')!;
const viewport=document.querySelector<HTMLElement>('#viewport')!;
const campaignSelect=document.querySelector<HTMLSelectElement>('#campaign')!;
const formatSelect=document.querySelector<HTMLSelectElement>('#format')!;
const scrub=document.querySelector<HTMLInputElement>('#scrub')!;
const audio=document.querySelector<HTMLAudioElement>('#audio')!;
const sound=document.querySelector<HTMLInputElement>('#sound')!;
if(isRender) document.body.classList.add('render');
const escape=(text:string)=>text.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('"','&quot;');

function rail(c:Campaign,s:Shot){return `<div class="rail"><div class="rail-brand"><img src="/assets/logo.svg" alt="">C-Sweet</div><span class="rail-label">${c.company.toUpperCase()}</span><span class="chapter-mark">${s.chapter===0?'THE POSSIBILITY':s.chapter===5?'YOUR NEXT CHAPTER':`0${s.chapter} / 04`}</span></div>`;}
function orbit(c:Campaign){
    const spots=c.portraits.length===2?[[55,55],[442,360]]:[[38,30],[440,80],[465,430],[0,415]];
    return `<div class="orbit"><svg viewBox="0 0 660 660" fill="none"><circle cx="330" cy="330" r="255" stroke="${c.accent}" stroke-opacity=".2"/><path d="M110 100 L330 330 L510 160 M330 330 L515 490 M330 330 L70 480" stroke="${c.accent}" stroke-opacity=".35" stroke-width="1.5"/></svg><div class="orbit-center"><img src="/assets/logo.svg" alt="C-Sweet"></div>${c.portraits.map((p,i)=>`<div class="orbit-person" style="left:${spots[i][0]}px;top:${spots[i][1]}px"><img src="/assets/${p}.jpg" alt="${escape(c.roles[i])}"></div><div class="orbit-tag" style="left:${spots[i][0]-10}px;top:${spots[i][1]+145}px">${escape(c.roles[i])}</div>`).join('')}</div>`;
}
function scene(c:Campaign,s:Shot){
    const vertical=format==='vertical';
    let asset=s.asset;
    if(vertical&&s.id==='plan') asset=`/assets/${c.id}-plan-focus.png`;
    if(vertical&&s.id==='team') asset=`/assets/${c.id}-employee-0.png`;
    const media=asset?`<div class="screen-shell"><div class="screen-chrome"><span class="chrome-dots"><i></i><i></i><i></i></span><span>${escape(c.company)} · ${s.id==='report'||s.id==='report-close'?'Sample channel report':escape(c.project)}</span><span>C-Sweet</span></div><div class="screen-window"><img class="screen-image" src="${asset}" alt="${escape(s.focal)}"></div></div>`:'';
    const strip=s.id==='team'?`<div class="portrait-strip">${c.portraits.map((p,i)=>`<div class="portrait-role"><img src="/assets/${p}.jpg" alt=""><span>${escape(c.roles[i])}</span></div>`).join('')}</div>`:'';
    const task=s.id==='task'?`<div class="task-focus-label">ASSIGNED WORK · FIRST MILESTONE</div><div class="task-focus"><img src="/assets/${c.id}-board-focus.png" alt="Assigned work card"></div>`:'';
    return `<section class="scene ${c.theme==='gold'?'gold':''} ${vertical?'vertical':''} ${s.id}" style="--accent:${c.accent}"><div class="atmosphere"></div><div class="grid"></div><div class="halo"></div><div class="halo small"></div>${rail(c,s)}<div class="copy"><div class="eyebrow">${s.id==='hook'?c.eyebrow:['','DIRECTION','TEAM','WORK','REVIEW',''][s.chapter]}</div><h1 class="headline">${s.heading.split('\n').map(line=>`<span>${escape(line)}</span>`).join('')}</h1><p class="detail">${escape(s.detail)}</p><div class="underline"></div></div>${s.id==='hook'?orbit(c):''}${s.id==='end'?'<div class="brand-ending"><img src="/assets/logo.svg" alt="C-Sweet"><strong>C-Sweet</strong></div>':''}${media}${strip}${task}<div class="bottom"><span>${s.id==='end'?'Your company. Your control.':escape(c.company)+' / '+escape(c.project)}</span><span class="sample">Sample data · condensed workflow</span><span class="steps">${[1,2,3,4].map(i=>`<i class="${s.chapter>=i?'active':''}"></i>`).join('')}</span></div></section>`;
}

function animateScene(root:HTMLElement,s:Shot,local:number){
    const p=clamp(local/(s.end-s.start));
    const reveal=ease(local/.85);
    root.querySelectorAll<HTMLElement>('.headline span').forEach((line,i)=>{ const r=ease((local-i*.11)/.65);line.style.opacity=String(.25+.75*r);line.style.transform=`translateY(${(1-r)*30}px)`; });
    const detail=root.querySelector<HTMLElement>('.detail')!; detail.style.opacity=String(ease((local-.22)/.65));detail.style.transform=`translateY(${(1-reveal)*18}px)`;
    const shell=root.querySelector<HTMLElement>('.screen-shell');
    if(shell){ const base=s.id==='task'?.94:1; shell.style.transform=`perspective(2200px) translate3d(${(1-reveal)*90-p*12}px,${(1-reveal)*25-p*8}px,0) rotateY(${(1-reveal)*-7}deg) rotateZ(${(1-reveal)*1.1+(p-.5)*.25}deg) scale(${base+p*.016})`; }
    root.querySelectorAll<HTMLElement>('.orbit-person').forEach((node,i)=>{const r=ease((local-.12-i*.12)/.85);node.style.opacity=String(r);node.style.transform=`translateY(${(1-r)*60+Math.sin(local*.8+i*1.3)*8}px) rotate(${Math.sin(local*.35+i)*2}deg) scale(${.82+.18*r})`;});
    root.querySelectorAll<HTMLElement>('.orbit-tag').forEach((node,i)=>{node.style.opacity=String(ease((local-.45-i*.12)/.5));node.style.transform=`translateY(${Math.sin(local*.8+i*1.3)*6}px)`;});
    const focus=root.querySelector<HTMLElement>('.task-focus');if(focus)focus.style.transform=`perspective(1800px) translateY(${(1-reveal)*45-p*10}px) rotateY(${(1-reveal)*-5}deg) scale(${.94+.06*reveal+p*.015})`;
    const atmosphere=root.querySelector<HTMLElement>('.atmosphere')!;atmosphere.style.transform=`translate(${Math.sin(local*.11)*22}px,${Math.cos(local*.12)*18}px) scale(1.03)`;
    // Show every employee in the vertical composition, two readable cards at a time.
    if(format==='vertical'&&s.id==='team'){
        const n=campaign.portraits.length;
        const pick=n===4&&local>(s.end-s.start)/2?2:0;
        const image=root.querySelector<HTMLImageElement>('.screen-image')!;
        image.src=`/assets/${campaign.id}-employee-${pick}.png`;
        image.style.width='100%';
        const window=root.querySelector<HTMLElement>('.screen-window')!;window.style.height='auto';
        if(!window.querySelector('.second-employee')){const second=document.createElement('img');second.className='screen-image second-employee';second.style.cssText='width:100%;display:block;margin-top:20px';window.append(second);}
        root.querySelector<HTMLImageElement>('.second-employee')!.src=`/assets/${campaign.id}-employee-${Math.min(pick+1,n-1)}.png`;
    }
}
// A source element bridges scene boundaries; the surrounding frame then reveals the next actual screen.
function bridge(s:Shot,p:number){
    if(!s.anchor||p>=1||s.id==='team'||s.id==='end') return '';
    const v=format==='vertical';
    const begin=v?{x:170,y:950,w:700}:{x:1000,y:380,w:600};
    const end=v?{x:82,y:820,w:866}:{x:734,y:250,w:1080};
    const mix=(a:number,b:number)=>a+(b-a)*p;
    return `<div class="transition-anchor" style="left:${mix(begin.x,end.x)}px;top:${mix(begin.y,end.y)}px;width:${mix(begin.w,end.w)}px;opacity:${Math.sin(Math.PI*p)*.8};transform:perspective(1800px) rotateY(${(1-p)*-4}deg)"><img src="${s.anchor}" alt=""></div>`;
}
function renderAt(t:number){
    time=clamp(t,0,formats[format].duration-1/30);
    const state=atTime(shots,time);
    const key=campaign.id+'-'+format+'-'+state.index;
    if(key!==sceneKey){
        stage.innerHTML=(state.previous?`<div id="outgoing">${scene(campaign,state.previous)}</div>`:'')+`<div id="incoming">${scene(campaign,state.shot)}</div><div id="bridge"></div>`;
        sceneKey=key;
    }
    const incoming=stage.querySelector<HTMLElement>('#incoming')!;
    const root=incoming.firstElementChild as HTMLElement;
    incoming.style.cssText=`position:absolute;inset:0;opacity:${state.transition};transform:translateX(${(1-state.transition)*(format==='vertical'?35:65)}px);clip-path:inset(0 ${(1-state.transition)*13}% 0 0)`;
    animateScene(root,state.shot,state.local);
    const outgoing=stage.querySelector<HTMLElement>('#outgoing');
    if(outgoing&&state.previous){outgoing.style.cssText=`position:absolute;inset:0;opacity:${1-state.transition};transform:translateX(${-state.transition*45}px) scale(${1+state.transition*.025});filter:blur(${state.transition*2}px)`;animateScene(outgoing.firstElementChild as HTMLElement,state.previous,state.previous.end-state.previous.start);}
    stage.querySelector<HTMLElement>('#bridge')!.innerHTML=bridge(state.shot,state.transition);
    scrub.value=String(time);
    const stamp=(s:number)=>`${Math.floor(s/60)}:${Math.floor(s%60).toString().padStart(2,'0')}`;
    document.querySelector('#time')!.textContent=`${stamp(time)} / ${stamp(formats[format].duration)}`;
}
function resize(){const f=formats[format];stage.style.width=f.width+'px';stage.style.height=f.height+'px';const scale=isRender?1:Math.min((innerWidth-48)/f.width,(innerHeight-220)/f.height);stage.style.transform=`scale(${scale})`;viewport.style.width=f.width*scale+'px';viewport.style.height=f.height*scale+'px';}
async function configure(){playing=false;audio.pause();sceneKey='';campaignSelect.value=campaign.id;formatSelect.value=format;scrub.max=String(formats[format].duration);audio.src=`/audio/${campaign.id}-${format}.wav`;shots=shotsFor(campaign,format);resize();await preload();renderAt(0);}
async function preload(){const assets=new Set<string>(['/assets/logo.svg',...campaign.portraits.map(p=>`/assets/${p}.jpg`)]);for(const shot of shots){if(shot.asset)assets.add(shot.asset);if(shot.anchor)assets.add(shot.anchor);}assets.add(`/assets/${campaign.id}-plan-focus.png`);for(let i=0;i<campaign.portraits.length;i++)assets.add(`/assets/${campaign.id}-employee-${i}.png`);await Promise.all([...assets].map(src=>new Promise<void>((resolve,reject)=>{const image=new Image();image.onload=()=>image.decode().then(()=>resolve(),reject);image.onerror=()=>reject(new Error('Missing promo asset '+src));image.src=src;})));await document.fonts.ready;}
function toggle(){playing=!playing;last=performance.now();document.querySelector('#play')!.textContent=playing?'Pause':'Play';if(playing&&sound.checked){audio.currentTime=time;audio.play().catch(()=>{});}else audio.pause();}
function tick(now:number){if(playing){time+=(now-last)/1000;if(time>=formats[format].duration){time=0;audio.currentTime=0;}renderAt(time);}last=now;requestAnimationFrame(tick);}
campaignSelect.onchange=()=>{campaign=campaigns.find(c=>c.id===campaignSelect.value)!;window.promoReady=configure();};
formatSelect.onchange=()=>{format=formatSelect.value as Format;window.promoReady=configure();};
scrub.oninput=()=>{renderAt(Number(scrub.value));audio.currentTime=time;};
sound.onchange=()=>{if(sound.checked&&playing){audio.currentTime=time;audio.play().catch(()=>{});}else audio.pause();};
document.querySelector<HTMLButtonElement>('#play')!.onclick=toggle;
addEventListener('resize',resize);
addEventListener('keydown',e=>{if((e.target as HTMLElement).tagName==='SELECT')return;if(e.code==='Space'){e.preventDefault();toggle();}if(e.code==='ArrowRight'||e.code==='ArrowLeft'){e.preventDefault();playing=false;audio.pause();renderAt(time+(e.code==='ArrowRight'?1:-1)/30);}});
window.renderAt=renderAt;
window.promoState=()=>({campaign:campaign.id,format,time,shot:atTime(shots,time).shot.id});
window.promoReady=configure();
requestAnimationFrame(tick);
