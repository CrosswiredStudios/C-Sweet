const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');

const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
  '.css': 'text/css', '.json': 'application/json', '.wasm': 'application/wasm', '.svg': 'image/svg+xml',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.ogg': 'audio/ogg', '.mp3': 'audio/mpeg', '.woff2': 'font/woff2' };

function resolveRequest(root, url) {
  const relative = decodeURIComponent(url.split('?')[0]);
  if (relative.includes('\0') || relative.includes('\\')) throw new Error('invalid-path');
  const target = path.resolve(root, '.' + relative);
  if (target !== root && !target.startsWith(root + path.sep)) throw new Error('invalid-path');
  return relative.endsWith('/') ? path.join(target, 'index.html') : target;
}

function serverFor(root) {
  return http.createServer(async (request, response) => {
    try {
      if (request.method !== 'GET' && request.method !== 'HEAD') {
        response.writeHead(405).end();
        return;
      }
      const file = resolveRequest(root, request.url);
      const bytes = await fs.readFile(file);
      response.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream',
        'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
      response.end(request.method === 'HEAD' ? undefined : bytes);
    } catch (error) {
      response.writeHead(error.code === 'ENOENT' || error.code === 'EISDIR' ? 404 : 400).end();
    }
  });
}

async function run(request) {
  const { chromium } = require('playwright');
  const server = serverFor('/tmp/build');
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  const report = { requestId: request.requestId, sourceCommitSha: request.sourceCommitSha,
    buildSha256: request.buildSha256, browserVersion: null, passed: false, viewports: [] };
  try {
    browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--use-gl=angle',
      '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
    report.browserVersion = browser.version();
    if (request.requireWebGl) {
      const probe = await browser.newPage();
      const available = await probe.evaluate(() => {
        const canvas = document.createElement('canvas');
        return Boolean(canvas.getContext('webgl2') || canvas.getContext('webgl'));
      });
      await probe.close();
      if (!available) throw new Error('Browser compute WebGL is unavailable; this is an environment failure.');
    }
    const origin = `http://127.0.0.1:${server.address().port}`;
    for (const width of request.viewportWidths) {
      const context = await browser.newContext({ viewport: { width, height: 720 }, serviceWorkers: 'block' });
      const errors = [];
      const record = value => { if (errors.length < 12) errors.push(String(value).slice(0, 160)); };
      const viewport = { width, height: 720, passed: false, canvas: false, webGl: false, errors };
      await context.route('**/*', route => {
        if (new URL(route.request().url()).origin === origin) return route.continue();
        record('External request blocked: ' + route.request().url());
        return route.abort('blockedbyclient');
      });
      await context.tracing.start({ screenshots: false, snapshots: false, sources: false });
      const page = await context.newPage();
      page.on('pageerror', error => record(error.message));
      page.on('console', message => { if (message.type() === 'error') record(message.text()); });
      page.on('requestfailed', item => record('Request failed: ' + item.url()));
      page.on('response', response => { if (response.status() >= 400) record(`HTTP ${response.status()}: ${response.url()}`); });
      page.on('websocket', socket => record('Unsupported WebSocket: ' + socket.url()));
      try {
        await page.addInitScript(() => {
          window.__csweetWebGlContexts = 0;
          const original = HTMLCanvasElement.prototype.getContext;
          HTMLCanvasElement.prototype.getContext = function(kind, ...args) {
            const context = original.call(this, kind, ...args);
            if (context && ['webgl', 'webgl2', 'experimental-webgl'].includes(kind)) window.__csweetWebGlContexts++;
            return context;
          };
        });
        await page.goto(origin + '/', { waitUntil: 'load', timeout: 5000 });
        await page.waitForTimeout(1000);
        viewport.canvas = await page.locator('canvas').first().isVisible();
        viewport.webGl = await page.evaluate(() => window.__csweetWebGlContexts > 0);
        await page.screenshot({ path: `/evidence/viewport-${width}.png`, timeout: 3000 });
        if (request.requireWebGl && (!viewport.canvas || !viewport.webGl)) record('No visible game canvas with an initialized WebGL context.');
        viewport.passed = errors.length === 0;
      } catch (error) { record(error.message); }
      finally {
        await context.tracing.stop({ path: `/evidence/trace-${width}.zip` });
        await context.close();
      }
      report.viewports.push(viewport);
    }
    report.passed = report.viewports.length === request.viewportWidths.length && report.viewports.every(item => item.passed);
    await fs.writeFile('/evidence/result.json', JSON.stringify(report));
  } finally {
    if (browser) await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
}

module.exports = { resolveRequest, serverFor };
if (require.main === module) run(JSON.parse(process.argv[2])).catch(error => { console.error(error.message); process.exitCode = 1; });
