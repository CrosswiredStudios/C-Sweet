// Deterministic animatic: every visual depends only on the supplied film time.
export const duration=96;
export const scenes=[
  {id:'idea',start:0,end:5,title:'A little idea. A whole team.',description:'A browser arcade game begins as one sentence. C-Sweet brings the company around it.'},
  {id:'chief',start:5,end:12,title:'Start with your Chief of Staff.',description:'Review Evelyn Brooks, the Chief of Staff, then approve the hire. Provider, Office and permission setup are assumed complete.'},
  {id:'focus',start:12,end:20,title:'You set the direction.',description:'Evelyn asks where the company should focus first. Select Creative & Product, then send the response.'},
  {id:'brief',start:20,end:27,title:'Give the idea a shape.',description:'Ask for a vibrant, one-minute browser arcade game. Keep the first milestone small: one arena, responsive controls and a replay loop.'},
  {id:'director',start:27,end:35,title:'Put someone behind the vision.',description:'A hiring suggestion recommends Naomi Chen as Creative Director. Review her profile and hire her; she owns creative direction and the project team design.'},
  {id:'team',start:35,end:43,title:'A vision becomes a team.',description:'Naomi proposes the team. Gabriel Reyes, Producer, owns delivery; Adrian Silva, Engineer, implements; Victor Lin, Technical Director, reviews; Rohan Desai, QA, checks the result. Hiring and staffing approvals are condensed.'},
  {id:'plan',start:43,end:51,title:'Make the next step concrete.',description:'Gabriel and the team turn the approved pitch into an epic, a story and executable tasks, with owners and acceptance criteria. Scope and sprint activation precede execution.'},
  {id:'board',start:51,end:59,title:'Every ticket has a next move.',description:'An active sprint becomes a visible Kanban board. The paddle-control ticket moves from Ready to In Progress. Technical Review and QA remain separate stages.'},
  {id:'build',start:59,end:66,title:'Build. With purpose.',description:'Adrian implements the controls and publishes a candidate with checks. The task remains under review until independent reviewers and integration gates complete.'},
  {id:'review',start:66,end:73,title:'Another set of eyes.',description:'Victor performs independent technical review of the exact candidate. Approval allows trusted integration into the story branch before QA.'},
  {id:'qa',start:73,end:80,title:'Prove the play.',description:'Rohan checks the integrated build against the criteria. The story then passes regression, technical readiness and Producer acceptance. These are illustrative outcomes, not actual test evidence.'},
  {id:'game',start:80,end:89,title:'From “what if” to “one more go.”',description:'An animated browser preview of the concept, Prism Break. It represents the reviewed result; public deployment is a separate step.'},
  {id:'end',start:89,end:96,title:'What will you build?',description:'C-Sweet. Your idea. Your company. Your workforce.'}
];
export function sceneAt(t){return scenes.find(s=>t>=s.start&&t<s.end)??scenes[t<0?0:scenes.length-1];}

const canvas=document.querySelector('canvas'),ctx=canvas.getContext('2d');
const C={ink:'#30263f',muted:'#72667d',accent:'#735090',paper:'#fbfaff',lav:'#c5c1f2',pink:'#eac2dd',teal:'#c6e8ea',green:'#357660',line:'#ded6e7',dark:'#211a32'};
const people=[
  ['Evelyn Brooks','Chief of Staff','chief-of-staff'],['Naomi Chen','Creative Director','video-game-creative-director'],
  ['Gabriel Reyes','Producer','video-game-producer'],['Adrian Silva','Engineer','video-game-engineer'],
  ['Victor Lin','Technical Director','video-game-technical-director'],['Rohan Desai','QA','video-game-qa']
];
const assets={};
const clamp=x=>Math.max(0,Math.min(1,x));
const ease=x=>1-(1-clamp(x))**3;
const mix=(a,b,p)=>a+(b-a)*p;
function box(x,y,w,h,fill=C.paper,r=20,stroke){ctx.beginPath();ctx.roundRect(x,y,w,h,r);ctx.fillStyle=fill;ctx.fill();if(stroke){ctx.strokeStyle=stroke;ctx.lineWidth=1.5;ctx.stroke();}}
function txt(s,x,y,size=24,color=C.ink,weight=400,align='left'){ctx.font=`${weight} ${size}px "Segoe UI",Arial,sans-serif`;ctx.fillStyle=color;ctx.textAlign=align;ctx.fillText(s,x,y);ctx.textAlign='left';}
function lines(s,x,y,size=24,color=C.ink,weight=400,leading=1.25){s.split('\n').forEach((l,i)=>txt(l,x,y+i*size*leading,size,color,weight));}
function line(x1,y1,x2,y2,color=C.line,width=2){ctx.beginPath();ctx.moveTo(x1,y1);ctx.lineTo(x2,y2);ctx.strokeStyle=color;ctx.lineWidth=width;ctx.stroke();}
function dot(x,y,r,fill){ctx.beginPath();ctx.arc(x,y,r,0,Math.PI*2);ctx.fillStyle=fill;ctx.fill();}
function pill(s,x,y,w,fill=C.teal,color=C.ink){box(x,y,w,34,fill,17);txt(s,x+w/2,y+23,17,color,600,'center');}
function button(s,x,y,w=230,active=false){box(x,y,w,52,active?C.green:C.accent,12);txt(s,x+w/2,y+34,21,'#fff',600,'center');}
function portrait(n,x,y,size=76){ctx.save();ctx.beginPath();ctx.roundRect(x,y,size,size,size*.24);ctx.clip();ctx.drawImage(assets[people[n][2]],x,y,size,size);ctx.restore();}
function person(n,x,y,sub=people[n][1],size=64){portrait(n,x,y,size);txt(people[n][0],x+size+18,y+26,24,C.ink,600);txt(sub,x+size+18,y+53,19,C.muted);}
function cursor(x,y,t,clickAt){ctx.save();ctx.translate(x,y);ctx.beginPath();ctx.moveTo(0,0);ctx.lineTo(0,30);ctx.lineTo(8,23);ctx.lineTo(14,36);ctx.lineTo(21,32);ctx.lineTo(15,20);ctx.lineTo(27,19);ctx.closePath();ctx.fillStyle=C.ink;ctx.fill();ctx.strokeStyle='#fff';ctx.lineWidth=2;ctx.stroke();const p=(t-clickAt)/.55;if(p>0&&p<1){ctx.globalAlpha=1-p;ctx.beginPath();ctx.arc(0,0,12+40*p,0,7);ctx.strokeStyle=C.accent;ctx.lineWidth=4;ctx.stroke();}ctx.restore();}
function appear(local,at,draw){const p=ease((local-at)/.6);if(p<=0)return;ctx.save();ctx.globalAlpha*=p;ctx.translate(0,(1-p)*22);draw();ctx.restore();}
function title(kicker,heading,sub){txt(kicker.toUpperCase(),90,172,17,C.accent,600);lines(heading,90,238,48,C.ink,600,1.15);if(sub)txt(sub,90,heading.includes('\n')?330:285,23,C.muted);}
function shell(label='Communications',x=570,y=158,w=940,h=610){box(x,y,w,h,C.paper,24,C.line);box(x,y,w,60,'#f0ebf6',24);box(x,y+30,w,30,'#f0ebf6',0);dot(x+26,y+30,5,'#baa9cb');dot(x+45,y+30,5,'#d4bdd5');dot(x+64,y+30,5,'#aad3cd');txt('Orbit Arcade',x+93,y+38,19,C.ink,600);txt(label,x+w-28,y+38,18,C.muted,400,'right');}
function base(t){ctx.clearRect(0,0,1600,900);const g=ctx.createLinearGradient(0,0,1600,900);g.addColorStop(0,'#ede8f8');g.addColorStop(.55,'#fbfaff');g.addColorStop(1,'#eaf5f2');ctx.fillStyle=g;ctx.fillRect(0,0,1600,900);for(const [x,y,r,c] of [[1350,70,540,C.pink],[180,720,580,C.lav]]){const glow=ctx.createRadialGradient(x,y,0,x,y,r);glow.addColorStop(0,c+'70');glow.addColorStop(1,c+'00');ctx.fillStyle=glow;ctx.fillRect(0,0,1600,900);}ctx.drawImage(assets.logo,75,35,60,60);txt('C-Sweet',145,77,31,C.ink,650);txt('YOUR IDEA. YOUR COMPANY. YOUR WORKFORCE.',1510,73,14,C.muted,600,'right');txt('ANIMATIC 01  /  ILLUSTRATED, CONDENSED WORKFLOW',90,854,14,C.muted,600);txt('PRISM BREAK  ·  A BROWSER GAME',1510,854,14,C.muted,500,'right');box(90,878,1420,3,'#ded6e7',1);box(90,878,1420*clamp(t/duration),3,C.accent,1);}
function ticket(x,y,w,titleText,owner='Adrian Silva',status='TASK · PB-03'){box(x,y,w,154,C.paper,14,C.line);txt(status,x+20,y+29,15,C.accent,600);lines(titleText,x+20,y+65,23,C.ink,600);dot(x+26,y+124,5,C.green);txt(owner,x+42,y+131,16,C.muted);}
function hire(n,l){shell('Review hire');person(n,620,252,people[n][1],110);txt('YOUR NEXT TEAMMATE',620,421,16,C.accent,600);lines(n===0?'Keep the company focused.\nConnect your direction to the right people.':'Own the creative vision.\nTurn a small game idea into a shared pitch.',620,467,26);line(620,534,1460,534);txt('Joining your team',620,576,19,C.muted);txt('Orbit Arcade',620,610,24,C.ink,600);pill('Review access',1210,568,200,'#eee7f5');button(l>4.5?'Hired ✓':'Approve and hire',1180,665,280,l>4.5);if(l>3)cursor(mix(1420,1340,ease((l-3)/1)),mix(755,689,ease((l-3)/1)),l,4.5);}
function recommendation(n,x,y,w,l,at=0){appear(l,at,()=>{box(x,y,w,330,C.paper,20,C.line);pill(l>at+4?'Hired ✓':'Awaiting review',x+24,y+20,170,l>at+4?C.teal:'#eee7f5');person(n,x+24,y+80,people[n][1],78);txt('C-Sweet · first-party agent',x+24,y+193,18,C.muted);txt(n===1?'Own the vision and team design.':'Own delivery of the first playable.',x+24,y+229,21);txt(`Plan proposed by ${n===1?'Evelyn Brooks':'Naomi Chen'}`,x+24,y+266,17,C.muted);button(l>at+4?'Hired ✓':'Hire',x+w-150,y+280,125,l>at+4);});}

// Authored deterministic browser-game concept, not a recording of generated output.
const gameFrames=[];
{let x=370,y=330,vx=165,vy=-220,score=0;const blocks=Array.from({length:40},(_,i)=>({x:45+(i%10)*70,y:80+Math.floor(i/10)*33,alive:true}));for(let f=0;f<=1080;f++){x+=vx/60;y+=vy/60;if(x<18||x>742){vx=-vx;x=Math.max(18,Math.min(742,x));}if(y<20){vy=Math.abs(vy);y=20;}if(y>405&&vy>0){vy=-Math.abs(vy);y=405;}for(const b of blocks){if(b.alive&&x>b.x-6&&x<b.x+60&&y>b.y-6&&y<b.y+24){b.alive=false;score+=100;vy=-vy;break;}}gameFrames.push({x,y,paddle:Math.max(55,Math.min(705,x)),score,blocks:blocks.map(b=>({...b}))});}}
function game(x,y,w,h,t){ctx.save();ctx.translate(x,y);ctx.scale(w/760,h/460);box(0,0,760,460,C.dark,16);for(let i=0;i<50;i++){dot((i*137)%760,(i*79)%440,1+(i%2),i%3===0?'#736682':'#494157');}const f=gameFrames[Math.min(gameFrames.length-1,Math.floor(Math.max(0,t)*60))];txt('PRISM BREAK',26,39,20,'#eee7fa',600);txt(String(f.score).padStart(4,'0'),730,39,20,C.teal,600,'right');f.blocks.forEach((b,i)=>{if(b.alive){box(b.x,b.y,60,23,[C.lav,C.pink,C.teal,'#e4c88d'][Math.floor(i/10)],5);}});for(let j=5;j>0;j--){const past=gameFrames[Math.max(0,Math.min(gameFrames.length-1,Math.floor(t*60)-j*3))];dot(past.x,past.y,6-j*.65,`rgba(234,194,221,${.14+.08*(5-j)})`);}dot(f.x,f.y,7,'#fff');box(f.paddle-48,416,96,9,C.teal,5);txt('MOVE  ← →     ·     BREAK THE SPECTRUM',380,451,12,'#c9bfd5',500,'center');ctx.restore();}

function drawScene(s,l){
 switch(s.id){
 case 'idea':{
   txt('FROM AN IDEA TO PLAY',800,209,19,C.accent,600,'center');
   appear(l,0,()=>{txt('A little idea.',800,345,88,C.ink,650,'center');txt('A whole team.',800,448,88,C.accent,650,'center');});
   appear(l,.7,()=>{box(390,516,820,82,C.paper,22,C.line);txt('“Let’s make a game people want to play again.”',800,568,28,C.ink,400,'center');});
   people.forEach((p,i)=>appear(l,1+i*.18,()=>portrait(i,540+i*88,654,64)));break;}
 case 'chief':title('01 / Find your first ally','Start with your\nChief of Staff.','Someone to connect the dots.');hire(0,l);break;
 case 'focus':{
   title('02 / Choose your focus','You set\nthe direction.','The company follows your lead.');shell();person(0,610,242);
   lines('Where would you like the company\nto focus first?',610,346,31,C.ink,600);
   ['Creative & Product','Player & Market Research','Financial','Legal & IP'].forEach((v,i)=>{const selected=i===0&&l>2.2;box(610,400+i*57,850,48,selected?'#e5dcf1':'#f5f2f8',10,selected?C.accent:undefined);dot(634,424+i*57,8,selected?C.accent:'#d8cee2');txt(v,656,432+i*57,22,C.ink,selected?600:400);});
   button(l>5.5?'Response sent ✓':'Send response',1215,665,245,l>5.5);if(l>1.4)cursor(l<4?mix(1300,900,ease((l-1.4)/.8)):mix(900,1340,ease((l-4)/1)),l<4?mix(680,424,ease((l-1.4)/.8)):mix(424,692,ease((l-4)/1)),l,l<4?2.2:5.5);break;}
 case 'brief':{
   title('03 / Name the possibility','One small game.\nOne clear ambition.','Prism Break · the first playable');shell();
   appear(l,.2,()=>{box(755,250,700,146,'#e9e1f2',18);txt('YOU',783,283,15,C.accent,600);lines('A vibrant browser arcade game.\nOne minute to learn. One more go to master.',783,329,25);});
   appear(l,1.8,()=>{person(0,610,447);lines('Let’s start with creative direction,\nthen bring in the team to make it real.',610,550,29);});
   appear(l,3.1,()=>pill('One arena  /  Great controls  /  Replay',610,650,620,C.teal));break;}
 case 'director':{
   title('04 / Hire creative leadership','Put someone\nbehind the vision.','Creative direction, with ownership.');shell('Hiring suggestions');txt('HIRING PLAN',610,263,15,C.accent,600);txt('Hiring suggestions',610,305,32,C.ink,600);recommendation(1,610,335,850,Math.min(l,3.9));if(l>1.7&&l<3.2)cursor(mix(1400,1360,ease((l-1.7)/1)),mix(740,641,ease((l-1.7)/1)),l,2.7);
   if(l>3.2){box(610,235,850,510,C.paper,20,C.line);person(1,640,269,people[1][1],90);txt('Review your next teammate',640,421,29,C.ink,600);txt('Orbit Arcade · Creative Director',640,471,24);pill('Review access',640,513,210,'#eee7f5');button(l>6.1?'Hired ✓':'Approve and hire',1130,646,290,l>6.1);if(l>4.7)cursor(mix(1450,1280,ease((l-4.7)/1)),mix(740,670,ease((l-4.7)/1)),l,6.1);if(l>6.1)txt('Naomi owns the vision and team design.',640,601,23,C.green,600);}break;}
 case 'team':{
   title('05 / Assemble the company','A vision becomes a team.','Naomi’s team plan → reviewed hires → clear responsibilities');
   const roles=['Delivery & planning','Implementation','Architecture & review','Independent QA'];
   for(let i=0;i<4;i++)appear(l,.3+i*.55,()=>{const x=90+i*360;box(x,365,335,330,C.paper,22,C.line);portrait(i+2,x+28,393,92);txt(people[i+2][0],x+28,526,27,C.ink,600);txt(people[i+2][1],x+28,566,23,C.accent,600);txt(roles[i],x+28,607,19,C.muted);pill(l>4+i*.3?'Hired ✓':'Suggested',x+28,637,150,l>4+i*.3?C.teal:'#eee7f5');});
   appear(l,3.5,()=>{txt('One shared project. Different responsibilities.',800,766,27,C.ink,500,'center');});break;}
 case 'plan':{
   title('06 / Plan the first playable','Make the next\nstep concrete.','A plan you can see taking shape.');shell('Project · Prism Break');person(2,610,246);pill('Approved scope',1260,260,205,C.teal);
   const rows=[['EPIC','Prism Break · first playable','A responsive arcade loop in the browser.'],['STORY','Play a complete round','Launch → control → score → replay.'],['TASKS','Controls · collisions · score · restart','Owners, criteria and independent review.']];
   rows.forEach((v,i)=>appear(l,.5+i*1.1,()=>{box(610+i*22,344+i*112,850-i*22,98,i===2?'#e9f3f1':'#f1ecf7',12);txt(v[0],632+i*22,372+i*112,14,C.accent,600);txt(v[1],632+i*22,401+i*112,24,C.ink,600);txt(v[2],632+i*22,428+i*112,18,C.muted);}));
   appear(l,4.4,()=>button('Activate sprint ✓',1215,700,245,true));break;}
 case 'board':{
   title('07 / Put the plan in motion','Every ticket has a next move.','Prism Break / Sprint 1 · Active');
   const labels=['Ready','In Progress','Technical Review','QA','Done'];
   labels.forEach((v,i)=>{box(90+i*288,349,271,390,'#ece7f2',14);txt(v,110+i*288,387,22,C.ink,600);});
   ticket(105,578,241,'Score &\nrestart','Adrian Silva','TASK · PB-05');
   ticket(105,407,241,'Ball & brick\ncollisions','Adrian Silva','TASK · PB-04');
   const p=ease((l-1.7)/1.5);ticket(mix(105,393,p),mix(407,422,p)-Math.sin(p*Math.PI)*60,241,'Responsive\npaddle controls');
   // Reveal the next Ready ticket only after the moving card clears its slot.
   if(p<.75){box(105,407,241,154,'#ece7f2',14);ticket(mix(105,393,p),mix(407,422,p)-Math.sin(p*Math.PI)*60,241,'Responsive\npaddle controls');}
   appear(l,4,()=>{pill('Adrian is implementing PB-03',393,616,530,C.teal);});break;}
 case 'build':{
   title('08 / Implement','Build.\nWith purpose.','A bounded task. A reviewable change.');shell('PB-03 · In Progress');person(3,610,246);box(610,343,850,271,C.dark,16);
   const code=['// PB-03 · responsive paddle controls','const input = readPlayerInput();','paddle.move(input.axis, deltaTime);','paddle.clampTo(arena.bounds);','','test("paddle stays inside the arena", () => {','  expect(paddle.x).toBeWithin(arena.bounds);','});'];
   code.forEach((v,i)=>appear(l,.2+i*.22,()=>txt(v,637,379+i*27,20,i===0?'#aaa0b7':i%2?C.teal:C.lav,400)));
   appear(l,2.6,()=>pill('Controls check passed',610,642,310,C.teal));appear(l,3.6,()=>pill('Candidate published',945,642,300,C.teal));appear(l,4.7,()=>txt('Next: independent technical review',610,723,24,C.accent,600));break;}
 case 'review':{
   title('09 / Review & integrate','Another set\nof eyes.','Technical Director · independent review');shell('PB-03 · Technical Review');person(4,610,246);
   const checks=['Controls follow the approved design','Boundary behavior is covered','Exact candidate approved'];
   checks.forEach((s,i)=>appear(l,.5+i*.8,()=>{box(610,350+i*69,850,57,'#f0ebf6',12);txt('✓',632,389+i*69,27,C.green,600);txt(s,677,388+i*69,25);}));
   appear(l,3.6,()=>{line(650,622,1420,622,C.accent,3);pill('Task candidate',610,596,250,'#e5dcf1');pill('Story branch',1180,596,280,C.teal);dot(mix(870,1180,ease((l-3.6)/1.2)),622,8,C.accent);});
   appear(l,4.7,()=>txt('Approved → trusted integration → QA',610,723,25,C.green,600));break;}
 case 'qa':{
   title('10 / Test & accept','Prove the play.','QA reviews the integrated build.');shell('PB-03 · QA');person(5,610,246);
   ['Keyboard controls respond','Paddle stays inside the arena','Restart begins a clean round'].forEach((v,i)=>appear(l,.4+i*.85,()=>{txt('✓',615,381+i*61,28,C.green,600);txt(v,658,380+i*61,25);}));
   appear(l,3,()=>{line(610,550,1460,550);txt('THEN, ACROSS THE PLAYABLE',610,590,16,C.accent,600);});
   appear(l,3.7,()=>pill('Regression ✓',610,620,235,C.teal));appear(l,4.3,()=>pill('Technical readiness ✓',864,620,310,C.teal));appear(l,4.9,()=>pill('Producer acceptance ✓',610,683,350,C.teal));break;}
 case 'game':{
   txt('11 / THE FIRST PLAYABLE',90,166,17,C.accent,600);txt('From “what if” to “one more go.”',90,230,49,C.ink,600);
   const p=ease(l/.8);ctx.save();ctx.translate(800,540);ctx.scale(.96+.04*p,.96+.04*p);ctx.translate(-800,-540);box(290,282,1020,510,C.paper,20,C.line);box(290,282,1020,43,'#e9e3f0',20);txt('Prism Break  /  Browser preview',320,310,17,C.muted);pill('Running',1150,289,128,C.teal);game(310,335,980,438,l+.5);ctx.restore();break;}
 case 'end':{
   ctx.drawImage(assets.logo,711,173,178,178);appear(l,.15,()=>txt('What will you build?',800,461,83,C.ink,650,'center'));appear(l,.9,()=>txt('Your idea. Your company. Your workforce.',800,542,30,C.accent,500,'center'));appear(l,1.6,()=>{txt('C-Sweet',800,655,38,C.ink,650,'center');txt('Start with a conversation.',800,711,24,C.muted,400,'center');});break;}
 }
}

let activeId='',current=0,playing=false,last=0;
export function renderAt(t){current=Math.max(0,Math.min(duration,t));const s=sceneAt(current),local=current-s.start;base(current);ctx.save();ctx.globalAlpha=ease(local/.38);drawScene(s,local);ctx.restore();if(activeId!==s.id){activeId=s.id;document.querySelector('#scene-title').textContent=s.title;document.querySelector('#scene-description').textContent=s.description;document.querySelectorAll('#chapters button').forEach(b=>b.setAttribute('aria-current',String(b.dataset.id===s.id)));}document.querySelector('#seek').value=String(current);document.querySelector('#time').textContent=`${Math.floor(current/60)}:${String(Math.floor(current%60)).padStart(2,'0')} / 1:36`;return s.id;}
window.renderAt=renderAt;window.filmScenes=scenes;window.filmDuration=duration;
for(const s of scenes){const b=document.createElement('button');b.textContent=`${Math.floor(s.start/60)}:${String(s.start%60).padStart(2,'0')} ${s.title}`;b.dataset.id=s.id;b.onclick=()=>renderAt(s.start+.6);document.querySelector('#chapters').append(b);}
function setPlaying(v){playing=v;document.querySelector('#play').textContent=v?'Pause':'Play';document.querySelector('#play').setAttribute('aria-label',v?'Pause animatic':'Play animatic');last=performance.now();}
document.querySelector('#play').onclick=()=>{if(current>=duration)renderAt(0);setPlaying(!playing);};
document.querySelector('#restart').onclick=()=>{renderAt(0);setPlaying(true);};
document.querySelector('#seek').oninput=e=>renderAt(Number(e.target.value));
document.querySelector('#full').onclick=()=>canvas.requestFullscreen();
document.addEventListener('keydown',e=>{if(e.target.matches('button,input,a'))return;if(e.code==='Space'){e.preventDefault();document.querySelector('#play').click();}if(e.code==='ArrowRight')renderAt(current+1);if(e.code==='ArrowLeft')renderAt(current-1);});
const params=new URLSearchParams(location.search);if(params.has('render'))document.body.classList.add('export');
window.filmReady=Promise.all([...people.map(p=>[p[2],`../../src/CSweet.UI/wwwroot/images/agents/${p[2]}-v1.jpg`]),['logo','../../src/CSweet.App/wwwroot/images/icon.svg']].map(async([key,url])=>{const img=new Image();img.src=url;await img.decode();assets[key]=img;})).then(()=>{renderAt(Number(params.get('t')||0));if(!params.has('render')){function tick(now){if(playing){renderAt(current+(now-last)/1000);if(current>=duration)setPlaying(false);}last=now;requestAnimationFrame(tick);}requestAnimationFrame(tick);}});
