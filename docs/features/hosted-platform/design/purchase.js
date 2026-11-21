const byId = id => document.getElementById(id);
const pages = ['offer','review','status','account'];
const states = {
  empty:['No order','—','—','Choose a plan to create your first workspace.'],
  cancelled:['Checkout closed','Unpaid','Awaiting purchase','You can return to your order when you are ready.'],
  pending:['Confirming payment','Pending','Waiting for payment','You can close this page. Your order will be updated in My Headquarters.'],
  setup:['Preparing your Headquarters','Paid','Setting up','C-Sweet is installing Headquarters and checking owner access. You can return here anytime.'],
  delayed:['Setup is taking longer','Paid','Delayed','C-Sweet support is reviewing setup. Keep order HQ-1042 for reference. Your payment has been received.'],
  ready:['Your Headquarters is ready','Paid','Ready','Open your workspace to connect an Office and choose a model.'],
  due:['Update your payment method','Past due','Running in grace','Your renewal payment failed. Update your payment method in the billing portal.'],
  ending:['Subscription ends December 20','Paid through Dec 20','Ready','Your workspace remains available through December 20. You can manage your subscription in billing.']
};
const handoffs={
  billing:['Manage billing','The Stripe billing portal opens here for invoices, payment methods and subscription changes.','Back to my account','account'],
  open:['northstar.csweet.app','In the live service, this link opens Northstar Headquarters at https://northstar.csweet.app/ with the owner’s workspace access. This address is a sample for design review.','Back to my account','account']
};
const button=(label,target,secondary=false)=>'<button class="btn '+(secondary?'secondary':'')+'" data-action="'+target+'">'+label+'</button>';
const link=(label,target)=>'<a href="#'+target+'">'+label+'</a>';
const row=(a,b)=>'<div><dt>'+a+'</dt><dd>'+b+'</dd></div>';
const summary=(a,b,c)=>'<dl>'+row('Workspace',a)+row('Billing',b)+row('Setup',c)+'</dl>';
let state='empty';
try {const saved=sessionStorage.getItem('hosting-design-state');if(saved in states)state=saved;}catch{}
function setState(next){state=next;try{sessionStorage.setItem('hosting-design-state',state);}catch{}}
function page(){const key=location.hash.slice(1);return pages.includes(key)?key:'offer';}
const officeTiers = [
 {name:'Standard Office',price:69,storage:100,ram:8,cap:2},
 {name:'Team Office',price:249,storage:250,ram:16,cap:4}
];
function officeCards(){
 const own = '<article><h3>Your own computer</h3><strong>Connect as an Office</strong><p>Use the storage and RAM on your machine. Proposed connection fee: $9/month per linked Office.</p></article>';
 return own + officeTiers.map(t => `<article><h3>${t.name}</h3><strong>+$${t.price}/month</strong><p>${t.storage} GB storage · ${t.ram} GB RAM<br>Up to ${t.cap} agents running at once</p></article>`).join('');
}
function offer(){
 return `<div class="grid"><section><p class="eyebrow">C-Sweet Hosting</p><h1>Hosted Headquarters</h1><p class="lead">Manage your company, team and work in one online workspace. Your agents run in the Offices you connect.</p></section>
 <section class="card"><p class="eyebrow">Headquarters subscription</p><div class="price">$99 <small>/ month</small></div><p class="line">One hosted Headquarters. Add Offices when you’re ready to run agents.</p>${linkButton('Build Plan','review')}</section></div>
 <section class="office-options"><h2>Where will your agents run?</h2><p>Connect your computers, rent hosted Offices, or use both. Office prices and tier sizes below are proposed.</p><div class="strip">${officeCards()}</div>
 <p class="note">Plan for about 1–2 GB of RAM per running agent. Once an Office reaches its tier limit, the next agent waits even if RAM is available.</p></section>`;
}
const linkButton=(label,target)=>'<a class="btn" href="#'+target+'">'+label+'</a>';
const choices=[{name:'Your own computer',price:9},...officeTiers];
function stored(key,fallback){
 try{return JSON.parse(sessionStorage.getItem('hosting-preview-'+key))??fallback;}catch{return fallback;}
}
function save(key,value){
 try{sessionStorage.setItem('hosting-preview-'+key,JSON.stringify(value));}catch{}
}
function quantities(key){
 const values=stored(key,[]);
 return choices.map((_,i)=>Number.isSafeInteger(values?.[i])&&values[i]>=0?values[i]:0);
}
let signedIn=stored('signed-in',false)===true;
let draft=quantities('draft'), order=quantities('order'), checkoutPlan;
const monthly=counts=>99+choices.reduce((sum,item,i)=>sum+counts[i]*item.price,0);
const money=n=>'$'+n.toLocaleString('en-US');
function receipt(counts){
 return row('Hosted Headquarters','$99')+choices.map((item,i)=>counts[i]?row(counts[i]+' × '+item.name,money(counts[i]*item.price)):'').join('');
}
function signIn(){
 return `<div class="card signin"><p class="eyebrow">C-Sweet Market</p><h1>Sign in to continue</h1><p>Your Market account owns your Headquarters subscription.</p><strong>Maya Chen</strong><p>maya@example.test · Preview account</p>${button('Continue as Maya','authenticate')}<div class="actions">${link('Back to hosting','offer')}</div></div>`;
}
function review(){
 const items=choices.map((item,i)=>`<article class="choice"><div><h3>${item.name}</h3><strong>${money(item.price)} / month each</strong><p>${i===0?'Connect a computer you own.':item.storage+' GB storage · '+item.ram+' GB RAM · Up to '+item.cap+' concurrent agents'}</p></div><div class="stepper" role="group" aria-label="${item.name} quantity"><button data-index="${i}" data-delta="-1" aria-label="Remove one ${item.name}">−</button><output id="qty-${i}">${draft[i]}</output><button data-index="${i}" data-delta="1" aria-label="Add one ${item.name}">+</button></div></article>`).join('');
 return `<p class="eyebrow">Hosted Headquarters / Checkout</p><h1>Build your plan</h1><p class="lead">Start with Headquarters for $99/month. Choose the Offices where your agents will run, or add them later.</p><div class="grid"><section class="card"><h2>Choose your Offices</h2>${items}</section>
 <section class="card"><p class="eyebrow">Order summary</p><h2>Your monthly plan</h2><dl id="lines"></dl><div class="price"><span id="total" aria-live="polite"></span> <small>/ month</small></div><p class="note">Renews monthly. Tax is calculated at checkout. Active employee fees and managed model usage are billed separately.</p>${button('Continue to payment','stripe')}<p class="note line">Workspace owner · Maya Chen</p></section></div>`;
}
function refreshQuote(){
 byId('lines').innerHTML=receipt(draft);
 byId('total').textContent=money(monthly(draft));
 choices.forEach((_,i)=>{
  byId('qty-'+i).value=draft[i];
  document.querySelector('[data-index="'+i+'"][data-delta="-1"]').disabled=draft[i]===0;
 });
}
function checkout(){
 if(!signedIn)return;
 checkoutPlan=[...(page()==='review'?draft:order)];
 byId('dialogTitle').textContent='Test checkout';
 byId('dialogBody').textContent='Simulate the first monthly payment of '+money(monthly(checkoutPlan))+' for this plan. No card is charged.';
 byId('dialogActions').innerHTML=button('Pay '+money(monthly(checkoutPlan))+' (test)','finish-pending')+button('Close checkout','finish-cancelled',true);
 byId('handoff').showModal();
}
// Sample of the canonical address supplied by Hosting for this deployment.
const workspaceUrl = new URL('https://northstar.csweet.app/');
function workspaceLink(){
 return `<a class="btn" href="${workspaceUrl.href}" data-action="open" aria-label="Open Headquarters at ${workspaceUrl.hostname}">${workspaceUrl.hostname} ↗</a>`;
}
function status(){
 const [title,bill,setup,copy]=states[state];
 const action=state==='cancelled'?button('Resume checkout','stripe'):state==='due'?button('Update payment method','billing'):['ready','ending'].includes(state)?workspaceLink():'';
 const steps=state==='cancelled'?['Checkout closed','Setup awaits payment']:state==='pending'?['Confirming payment','Setup awaits payment']:['Payment received',setup==='Ready'||state==='due'?'Workspace prepared':'Workspace setup '+(state==='delayed'?'delayed':'in progress'),setup==='Ready'||state==='due'?'Owner access verified':'Owner access check pending'];
 return `<p class="eyebrow">Order HQ-1042</p><div class="grid"><section><h1>${title}</h1><p class="lead">${copy}</p><div class="actions">${action}${link('My Headquarters','account')}</div></section><section class="card"><h2>Order progress</h2>${summary('Northstar',bill,setup)}<dl>${row('Monthly plan',money(monthly(order)))}</dl><ol class="steps">${steps.map((s,i)=>'<li><span class="dot" aria-hidden="true">'+(i+1)+'</span>'+s+'</li>').join('')}</ol></section></div>`;}
function account(){
 const [,bill,setup]=states[state];return `<p class="eyebrow">Market account · Maya Chen</p><h1>My Headquarters</h1>${state==='empty'?'<section class="card workspace"><div><h2>Your first workspace</h2><p>Choose a hosting plan to get started.</p></div>'+button('View hosting plan','offer')+'</section>':'<section class="card workspace"><div><p class="eyebrow">Hosted Headquarters</p><h2>Northstar</h2><p>Order HQ-1042 · '+money(monthly(order))+' / month</p></div><div><strong>Billing</strong><p>'+bill+'</p></div><div><strong>Workspace</strong><p>'+setup+'</p></div>'+(['ready','ending','due'].includes(state)?workspaceLink():button('View order','status'))+'</section><div class="actions">'+link('Order details','status')+(['pending','cancelled'].includes(state)?'':button('Manage billing ↗','billing',true))+'</div>'}`;}
function render(){const current=page();const gated=current!=='offer'&&!signedIn;if(!gated&&current==='status'&&state==='empty')setState('pending');
 byId('page').value=current;byId('session').textContent=signedIn?'Sign out of preview':'Signed out';byId('session').disabled=!signedIn;byId('scenarioLabel').hidden=gated||!['status','account'].includes(current);
 byId('scenario').innerHTML=Object.entries(states).filter(([k])=>current!=='status'||k!=='empty').map(([k,v])=>'<option value="'+k+'">'+v[0]+'</option>').join('');byId('scenario').value=state;
 byId('content').innerHTML=gated?signIn():({offer,review,status,account}[current])();if(!gated&&current==='review')refreshQuote();document.title=byId('content').querySelector('h1').textContent+' · Design review';
 document.querySelectorAll('nav a').forEach(a=>a.toggleAttribute('aria-current',a.hash==='#'+(['review','offer'].includes(current)?'offer':'account')));}
function go(target){if(page()===target)render();else location.hash=target;}
function act(target){
 if(target==='authenticate'){signedIn=true;save('signed-in',true);render();byId('content').focus();return;}
 if(target==='stripe'){checkout();return;}
 if(handoffs[target]){const [title,body,label,action]=handoffs[target];byId('dialogTitle').textContent=title;byId('dialogBody').textContent=body;byId('dialogActions').innerHTML=button(label,'finish-'+action);byId('handoff').showModal();return;}
 if(target.startsWith('finish-')){target=target.slice(7);byId('handoff').close();if(['pending','cancelled'].includes(target)){order=[...checkoutPlan];save('order',order);setState(target);go('status');return;}}
 go(target);}
document.addEventListener('click',e=>{const b=e.target.closest('[data-action]');if(b){e.preventDefault();act(b.dataset.action);}});
byId('page').onchange=e=>go(e.target.value);byId('scenario').onchange=e=>{setState(e.target.value);render();};
byId('theme').onchange=e=>{document.body.classList.toggle('gold',e.target.value==='gold');};
window.addEventListener('hashchange',()=>{render();byId('content').focus();});render();

byId('session').onclick=()=>{signedIn=false;save('signed-in',false);render();};
document.addEventListener('click',event=>{
 const b=event.target.closest('button[data-delta]');
 if(!b||!signedIn||page()!=='review')return;
 const index=Number(b.dataset.index);
 draft[index]=Math.max(0,draft[index]+Number(b.dataset.delta));
 save('draft',draft);
 refreshQuote();
});
