// Tiny headless-browser driver over the Chrome DevTools Protocol (Edge or Chrome, no npm dependency), used to
// smoke-test the UI against the mock host when no interactive browser is available.
//   node mock/cdp.mjs <steps.mjs> [--url http://127.0.0.1:5199/] [--out <dir>] [--browser <exe>]
// steps.mjs exports `default async function (b)` where b = { go, eval, click, type, key, shot, wait, logs }.
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, existsSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';

const argv = process.argv.slice(2);
const opt = (k, d) => { const i = argv.indexOf('--' + k); return i >= 0 ? argv[i + 1] : d; };
const stepsFile = argv[0];
const url = opt('url', 'http://127.0.0.1:5199/');
const out = resolve(opt('out', join(tmpdir(), 'botui-shots')));
const candidates = [opt('browser'), 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Google/Chrome/Application/chrome.exe', '/usr/bin/chromium', '/usr/bin/google-chrome'].filter(Boolean);
const exe = candidates.find(p => existsSync(p));
if (!exe) { console.error('no Chromium-based browser found'); process.exit(2); }
mkdirSync(out, { recursive: true });
const port = 9300 + Math.floor(Math.random() * 500);
const profile = join(tmpdir(), `botui-cdp-${port}`);
const proc = spawn(exe, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--window-size=1600,1000',
  '--no-first-run', '--no-default-browser-check', '--disable-extensions', 'about:blank'], { stdio: 'ignore' });

const sleep = ms => new Promise(r => setTimeout(r, ms));
let ws, seq = 0; const pending = new Map(); const logs = [];
async function connect() {
  for (let i = 0; i < 50; i++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const page = list.find(t => t.type === 'page');
      if (page) { ws = new WebSocket(page.webSocketDebuggerUrl); break; }
    } catch { /* starting */ }
    await sleep(200);
  }
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); }
    else if (m.method === 'Runtime.consoleAPICalled') logs.push(`[console.${m.params.type}] ` + m.params.args.map(a => a.value ?? a.description ?? '').join(' '));
    else if (m.method === 'Runtime.exceptionThrown') logs.push('[exception] ' + (m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text));
    else if (m.method === 'Log.entryAdded') logs.push(`[log.${m.params.entry.level}] ${m.params.entry.text} ${m.params.entry.url ?? ''}`);
  };
}
const send = (method, params = {}) => new Promise((res, rej) => { const id = ++seq; pending.set(id, { res, rej }); ws.send(JSON.stringify({ id, method, params })); });

const b = {
  async go(path) { await send('Page.navigate', { url: new URL(path, url).href }); await sleep(1200); },
  async eval(expr) {
    const r = await send('Runtime.evaluate', { expression: `(async () => { ${expr} })()`, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
    return r.result.value;
  },
  async click(x, y) {
    for (const type of ['mousePressed', 'mouseReleased']) await send('Input.dispatchMouseEvent', { type, x, y, button: 'left', clickCount: 1 });
    await sleep(150);
  },
  async drag(x1, y1, x2, y2) {
    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x1, y: y1 });
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: x1, y: y1, button: 'left', clickCount: 1 });
    for (let i = 1; i <= 12; i++) { await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x1 + (x2 - x1) * i / 12, y: y1 + (y2 - y1) * i / 12, button: 'left', buttons: 1 }); await sleep(16); }
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: x2, y: y2, button: 'left', clickCount: 1 });
    await sleep(200);
  },
  async type(text) { await send('Input.insertText', { text }); await sleep(100); },
  async key(key, code = key, keyCode = 0) {
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key, code, windowsVirtualKeyCode: keyCode });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key, code, windowsVirtualKeyCode: keyCode });
    await sleep(100);
  },
  async shot(name) {
    const r = await send('Page.captureScreenshot', { format: 'png' });
    const p = join(out, name + '.png'); writeFileSync(p, Buffer.from(r.data, 'base64')); console.log('shot', p); return p;
  },
  wait: sleep,
  logs: () => logs.splice(0),
};

try {
  await connect();
  await send('Runtime.enable'); await send('Log.enable'); await send('Page.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
  const steps = (await import(pathToFileURL(resolve(stepsFile)).href)).default;
  await steps(b);
  const rest = logs.splice(0);
  if (rest.length) console.log('--- console ---\n' + rest.join('\n'));
} catch (e) {
  console.error('FAILED', e.stack || e);
  process.exitCode = 1;
} finally {
  try { await Promise.race([send('Browser.close'), sleep(1500)]); } catch { /* already gone */ }
  try { ws?.close(); } catch { /* */ }
  proc.kill();
  await sleep(800);
  try { rmSync(profile, { recursive: true, force: true }); } catch { /* still locked */ }
}
