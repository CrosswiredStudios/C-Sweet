// Exact file allowlist: no directory browsing or arbitrary repository reads.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { once } from 'node:events';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const files=new Set([
 'tools/csweet-explainer/index.html','tools/csweet-explainer/review.css',
 'tools/csweet-explainer/film.mjs','tools/csweet-explainer/README.md',
 ...['studio.html','final-film.mjs','game.html','game.mjs','animatic.html'].map(n=>`tools/csweet-explainer/${n}`),
 ...['chief','focus','focus-selected','chat','director','director-card','director-review','hired','staffing','team','suggestion-0','suggestion-1','suggestion-2','suggestion-3','employee-0','employee-1','employee-2','employee-3','plan','ticket','ticket-card','board-ready','board-work','board-review','board-qa','board-done','implementation','technical','quality'].map(n=>`assets/readme/explainer/captures/${n}.png`),
 ...['from-idea-to-play.mp4','from-idea-to-play.vertical.mp4','final-poster.png','final-teaser.gif','final-storyboard.jpg','final-chapters.vtt','transcript.txt'].map(n=>`assets/readme/explainer/${n}`),
 'src/CSweet.App/wwwroot/images/icon.svg',
 'src/CSweet.App/wwwroot/images/icon_dark.svg',
 'assets/readme/explainer/captures/sequence-provenance.json',
 ...['overview','overview-open','chat-director','chat-brief','chat-producer','chat-team','chat-planning','chat-ready','ready-messages','producer-card','producer-review','board-empty','board-epic','board-stories','board-tasks','board-progress1','board-progress2','board-progress3','board-complete',...['task','done','progress1','progress2','progress3'].flatMap(p=>Array.from({length:7},(_,i)=>`${p}-${String(i+1).padStart(2,'0')}`))].map(n=>`assets/readme/explainer/captures/${n}.png`),
 ...['chief-of-staff','video-game-creative-director','video-game-producer','video-game-engineer','video-game-technical-director','video-game-qa'].map(n=>`src/CSweet.UI/wwwroot/images/agents/${n}-v1.jpg`),
 ...['poster.png','teaser.gif','storyboard.jpg','chapters.vtt','from-idea-to-play.animatic.mp4'].map(n=>`assets/readme/explainer/${n}`),
 ...['light','dark'].map(t=>`artifacts/readme/README-${t}.html`),
 ...['csweet-aurora-hero.png','csweet-aurora-hero-mobile.png','csweet-aurora-workspace.png','csweet-aurora-workspace-mobile.png','csweet-company-workflow.svg','csweet-company-workflow-mobile.svg','csweet-security.svg','csweet-security-mobile.svg'].map(n=>`assets/readme/${n}`)
]);
const mime={'.html':'text/html; charset=utf-8','.mjs':'text/javascript; charset=utf-8','.css':'text/css','.jpg':'image/jpeg','.png':'image/png','.gif':'image/gif','.svg':'image/svg+xml','.md':'text/plain; charset=utf-8','.txt':'text/plain; charset=utf-8','.mp4':'video/mp4','.vtt':'text/vtt; charset=utf-8'};
export async function startServer(port=0){
 const server=createServer(async(req,res)=>{try{
  if(req.method!=='GET'&&req.method!=='HEAD'){res.writeHead(405).end();return;}
  const file=decodeURIComponent(new URL(req.url,'http://localhost').pathname).slice(1);
  if(!files.has(file)){res.writeHead(404).end();return;}
  const data=await readFile(path.join(root,file));
  const headers={'Content-Type':mime[path.extname(file)]||'application/octet-stream','Accept-Ranges':'bytes','Cache-Control':'no-store'};
  const range=req.headers.range?.match(/^bytes=(\d+)-(\d*)$/);
  if(range){const start=Number(range[1]),end=range[2]?Math.min(Number(range[2]),data.length-1):data.length-1;if(start>end||start>=data.length){res.writeHead(416).end();return;}res.writeHead(206,{...headers,'Content-Range':`bytes ${start}-${end}/${data.length}`,'Content-Length':end-start+1});res.end(req.method==='HEAD'?undefined:data.subarray(start,end+1));return;}
  res.writeHead(200,{...headers,'Content-Length':data.length});res.end(req.method==='HEAD'?undefined:data);
 }catch{res.writeHead(404).end();}});
 server.listen(port,'127.0.0.1');await once(server,'listening');return server;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
 const server=await startServer(5277);
 console.log(`Review only: http://127.0.0.1:${server.address().port}/tools/csweet-explainer/index.html`);
}
