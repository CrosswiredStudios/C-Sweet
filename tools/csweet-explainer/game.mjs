// A real, standalone browser game, authored as the explainer's illustrative payoff.
// The film and playable page use the same physics and renderer.
export const W=1280,H=720;
const colors=['#bd9dff','#d0b6ff','#fa9ecf','#75e6df','#ffd479'];
const clamp=(x,a,b)=>Math.max(a,Math.min(b,x));
function rect(c,x,y,w,h,r,fill){c.beginPath();c.roundRect(x,y,w,h,r);c.fillStyle=fill;c.fill();}
function text(c,v,x,y,size,color,weight=500,align='left'){c.font=`${weight} ${size}px "Segoe UI",sans-serif`;c.fillStyle=color;c.textAlign=align;c.fillText(v,x,y);c.textAlign='left';}
export class PrismGame {
 constructor(auto=false){this.auto=auto;this.reset();}
 reset(){this.time=0;this.score=0;this.combo=0;this.lives=3;this.hits=0;this.status='playing';this.paddle=640;this.target=640;this.ball={x:640,y:570,vx:245,vy:-405};this.trail=[];this.particles=[];this.events=[];this.flash=0;this.bricks=Array.from({length:50},(_,i)=>({x:92+(i%10)*110,y:152+Math.floor(i/10)*41,w:98,h:27,row:Math.floor(i/10),alive:true}));}
 burst(x,y,color){for(let i=0;i<20;i++){const angle=i*2.39996+this.hits*.71;const speed=80+((i*37+this.hits*11)%180);this.particles.push({x,y,vx:Math.cos(angle)*speed,vy:Math.sin(angle)*speed,life:.4+(i%5)*.12,max:.4+(i%5)*.12,color,size:2+i%4});}}
 update(dt,input=0){if(this.status!=='playing')return;this.time+=dt;this.flash=Math.max(0,this.flash-dt*3);const b=this.ball;
  if(this.auto){let prediction=b.x;if(b.vy>0){prediction=b.x+b.vx*((630-b.y)/b.vy);while(prediction<40||prediction>1240)prediction=prediction<40?80-prediction:2480-prediction;}this.target=clamp(prediction+Math.sin(this.time*2.1)*24,88,1192);}
  else if(input)this.target=clamp(this.paddle+input*850*dt,88,1192);
  this.paddle+=clamp(this.target-this.paddle,-1000*dt,1000*dt);
  b.x+=b.vx*dt;b.y+=b.vy*dt;
  if(b.x<28||b.x>1252){b.vx=-b.vx;b.x=clamp(b.x,28,1252);this.burst(b.x,b.y,'#75e6df');}
  if(b.y<100){b.vy=Math.abs(b.vy);b.y=100;}
  if(b.vy>0&&b.y>=633&&b.y<653&&Math.abs(b.x-this.paddle)<92){const speed=Math.min(760,Math.hypot(b.vx,b.vy)+12);const angle=clamp((b.x-this.paddle)/90,-.9,.9);b.vx=angle*speed*.8+(this.auto?Math.sin(this.hits*1.8)*95:0);b.vy=-Math.sqrt(Math.max(90000,speed*speed-b.vx*b.vx));b.y=632;this.combo=0;this.burst(b.x,b.y,'#75e6df');}
  for(const brick of this.bricks){if(brick.alive&&b.x>brick.x-8&&b.x<brick.x+brick.w+8&&b.y>brick.y-8&&b.y<brick.y+brick.h+8){brick.alive=false;b.vy=-b.vy;this.combo++;this.hits++;this.score+=100*Math.min(this.combo,4);this.flash=.6;this.burst(brick.x+49,brick.y+14,colors[brick.row]);this.events.push({time:this.time,x:brick.x+49,y:brick.y,score:100*Math.min(this.combo,4)});break;}}
  if(b.y>760){this.lives--;if(this.lives===0)this.status='over';else {this.ball={x:this.paddle,y:570,vx:245,vy:-405};this.combo=0;}}
  if(this.bricks.every(b=>!b.alive))this.status='won';
  this.trail.unshift({x:b.x,y:b.y});if(this.trail.length>26)this.trail.pop();
  for(const p of this.particles){p.x+=p.vx*dt;p.y+=p.vy*dt;p.vy+=120*dt;p.life-=dt;}this.particles=this.particles.filter(p=>p.life>0);this.events=this.events.filter(e=>this.time-e.time<.8);
 }
 snapshot(){return {time:this.time,score:this.score,combo:this.combo,lives:this.lives,hits:this.hits,status:this.status,paddle:this.paddle,ball:{...this.ball},trail:this.trail.map(p=>({...p})),particles:this.particles.map(p=>({...p})),events:this.events.map(p=>({...p})),flash:this.flash,bricks:this.bricks.map(b=>({...b}))};}
}
export function drawGame(c,s,w=W,h=H){c.save();c.scale(w/W,h/H);const t=s.time;
 const g=c.createLinearGradient(0,0,1280,720);g.addColorStop(0,'#151022');g.addColorStop(1,'#080e20');c.fillStyle=g;c.fillRect(0,0,W,H);
 for(const [x,y,col] of [[250,240,'#5d2b99'],[1100,540,'#105661']]){const g=c.createRadialGradient(x,y,10,x,y,520);g.addColorStop(0,col+'65');g.addColorStop(1,col+'00');c.fillStyle=g;c.fillRect(0,0,W,H);}
 for(let i=0;i<110;i++){const x=(i*193.27)%W,y=(i*83.77+t*(5+i%4))%H;const alpha=.2+.3*(Math.sin(i+t*.8)*.5+.5);c.fillStyle=`rgba(210,215,255,${alpha})`;c.fillRect(x,y,i%9===0?2:1,i%9===0?2:1);}
 c.strokeStyle='#ffffff09';c.lineWidth=1;for(let x=-720;x<1800;x+=80){c.beginPath();c.moveTo(640+(x-640)*.3,415);c.lineTo(x,720);c.stroke();}for(let y=450;y<720;y+=45){c.beginPath();c.moveTo(0,y);c.lineTo(W,y);c.stroke();}
 text(c,'PRISM',42,47,28,'#fff',750);text(c,'BREAK',140,47,28,'#bda2ff',750);text(c,'A C-SWEET DEMO',43,72,10,'#9c91b2',600);
 text(c,'SCORE',640,33,10,'#9c91b2',600,'center');text(c,String(s.score).padStart(6,'0'),640,64,27,'#fff',600,'center');
 text(c,'LIVES',1237,31,10,'#9c91b2',600,'right');for(let i=0;i<3;i++){c.fillStyle=i<s.lives?'#75e6df':'#494052';c.beginPath();c.arc(1190+i*20,52,5,0,7);c.fill();}
 c.strokeStyle='#a795c52f';c.beginPath();c.moveTo(38,90);c.lineTo(1242,90);c.stroke();
 for(const b of s.bricks)if(b.alive){c.shadowColor=colors[b.row];c.shadowBlur=14;rect(c,b.x,b.y,b.w,b.h,6,colors[b.row]+'d9');c.shadowBlur=0;rect(c,b.x+3,b.y+2,b.w-6,3,1,'#ffffff66');}
 for(let i=s.trail.length-1;i>=0;i--){const p=s.trail[i];c.globalAlpha=(1-i/s.trail.length)*.5;c.fillStyle='#e2bbff';c.beginPath();c.arc(p.x,p.y,Math.max(1,8-i*.25),0,7);c.fill();}c.globalAlpha=1;
 for(const p of s.particles){c.globalAlpha=p.life/p.max;c.fillStyle=p.color;c.save();c.translate(p.x,p.y);c.rotate(p.life*5);c.fillRect(-p.size/2,-p.size/2,p.size,p.size);c.restore();}c.globalAlpha=1;
 c.shadowColor='#f5d8ff';c.shadowBlur=24;c.fillStyle='#fff';c.beginPath();c.arc(s.ball.x,s.ball.y,8,0,7);c.fill();
 c.shadowColor='#75e6df';c.shadowBlur=28;rect(c,s.paddle-82,646,164,11,6,'#75e6df');c.shadowBlur=0;rect(c,s.paddle-70,648,140,2,1,'#ffffffbb');
 for(const e of s.events){const age=t-e.time;c.globalAlpha=1-age/.8;text(c,'+'+e.score,e.x,e.y-age*55,22,'#fff',700,'center');}c.globalAlpha=1;
 if(s.combo>1){text(c,`${s.combo}× COMBO`,640,414,22,'#ffd479',700,'center');}
 text(c,'MOVE  ← →  /  A D  /  POINTER',42,698,11,'#9c91b2',500);text(c,'ONE MORE GO.',1238,698,11,'#9c91b2',500,'right');
 if(s.status!=='playing'){c.fillStyle='#0b1024cc';c.fillRect(0,0,W,H);text(c,s.status==='won'?'SPECTRUM CLEARED':'ONE MORE GO?',640,332,55,'#fff',700,'center');text(c,`Score ${s.score} · press Restart to play again`,640,388,22,'#bda2ff',500,'center');}
 c.restore();}

let frames;
export function gameAt(seconds){if(!frames){frames=[];const game=new PrismGame(true);for(let i=0;i<=1800;i++){frames.push(game.snapshot());game.update(1/120);game.update(1/120);}}return frames[clamp(Math.floor(seconds*60),0,frames.length-1)];}
