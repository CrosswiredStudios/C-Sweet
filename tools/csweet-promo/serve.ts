import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url));
const artifacts=path.resolve(here,'../../artifacts/promo');
const mime:Record<string,string>={'.html':'text/html','.css':'text/css','.ts':'text/javascript','.js':'text/javascript','.png':'image/png','.svg':'image/svg+xml','.jpg':'image/jpeg','.json':'application/json','.wav':'audio/wav','.mp4':'video/mp4'};
export function startServer(port=5199){return createServer(async(req,res)=>{try{
    const url=new URL(req.url!,'http://127.0.0.1');const name=decodeURIComponent(url.pathname);
    const root=name.startsWith('/assets/')?path.join(artifacts,'captures'):name.startsWith('/audio/')?path.join(artifacts,'audio'):name.startsWith('/exports/')?path.join(artifacts,'exports'):here;
    const relative=name.startsWith('/assets/')||name.startsWith('/audio/')||name.startsWith('/exports/')?name.slice(name.indexOf('/',1)+1):name==='/'?'index.html':name.slice(1);
    const target=path.resolve(root,relative);
    if(!target.startsWith(root+path.sep)||!relative){res.writeHead(403);res.end();return;}
    const ext=path.extname(target);let body=await readFile(target);
    if(ext==='.ts')body=Buffer.from(stripTypeScriptTypes(body.toString(),{mode:'strip'}));
    res.writeHead(200,{'Content-Type':mime[ext]||'application/octet-stream','Cache-Control':'no-store'});res.end(body);
}catch(error){res.writeHead(404);res.end(String(error));}}).listen(port,'127.0.0.1');}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){startServer();console.log('Promo studio: http://127.0.0.1:5199');}
