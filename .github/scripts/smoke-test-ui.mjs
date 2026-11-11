// Launches the published Segra.exe, attaches to its WebView2 over CDP and fails unless the
// main menu renders, the backend answers over its websocket, and no frontend asset or script fails.
// Usage: node smoke-test-ui.mjs <path to Segra.exe> [screenshot.png]
import { spawn, execSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

const exe = resolve(process.argv[2]);
const screenshotPath = process.argv[3] ?? 'smoke-test.png';
const port = 9222;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function fail(message) {
  console.error(`::error::${message}`);
  process.exitCode = 1;
}

let appExit = null;
const app = spawn(exe, [], {
  env: {
    ...process.env,
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`,
    SEGRA_SKIP_OBS: '1',
  },
  stdio: 'ignore',
});
app.on('error', (err) => (appExit = `Segra failed to start: ${err.message}`));
app.on('exit', (code) => (appExit = `Segra exited early with code ${code}`));

async function waitFor(check, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (appExit) throw new Error(appExit);
    const value = await check();
    if (value) return value;
    await sleep(1000);
  }
  return null;
}

function connect(wsUrl) {
  const ws = new WebSocket(wsUrl);
  let nextId = 1;
  const pending = new Map();
  const listeners = [];
  ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    if (msg.id && pending.has(msg.id)) {
      pending.get(msg.id).resolve(msg);
      pending.delete(msg.id);
    } else if (msg.method) {
      listeners.forEach((l) => l(msg));
    }
  };
  ws.onclose = () => {
    pending.forEach((p) => p.reject(new Error('DevTools connection closed (Segra crashed?)')));
    pending.clear();
  };
  const send = (method, params = {}) =>
    new Promise((resolve, reject) => {
      const id = nextId++;
      const timer = setTimeout(() => {
        pending.delete(id);
        reject(new Error(`${method} timed out`));
      }, 15_000);
      pending.set(id, {
        resolve: (msg) => (clearTimeout(timer), resolve(msg)),
        reject: (err) => (clearTimeout(timer), reject(err)),
      });
      ws.send(JSON.stringify({ id, method, params }));
    });
  return new Promise((resolve, reject) => {
    ws.onopen = () => resolve({ send, on: (l) => listeners.push(l), close: () => ws.close() });
    ws.onerror = () => reject(new Error('Could not connect to the WebView2 DevTools endpoint'));
  });
}

let cdp;
try {
  const page = await waitFor(async () => {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      return targets.find((t) => t.type === 'page' && t.url.includes('localhost:440'));
    } catch {
      return null;
    }
  }, 120_000);
  if (!page) throw new Error('WebView2 page never appeared');
  console.log(`Attached to ${page.url}`);

  cdp = await connect(page.webSocketDebuggerUrl);
  const problems = [];
  const frontendRequests = new Map();
  const backendSockets = new Set();
  let backendState = false;
  cdp.on((msg) => {
    const p = msg.params;
    switch (msg.method) {
      case 'Network.requestWillBeSent':
        if (p.request.url.includes('localhost:440')) frontendRequests.set(p.requestId, p.request.url);
        break;
      case 'Network.responseReceived':
        if (p.response.url.includes('localhost:440') && p.response.status >= 400)
          problems.push(`HTTP ${p.response.status} ${p.response.url}`);
        break;
      case 'Network.loadingFailed':
        if (!p.canceled && frontendRequests.has(p.requestId))
          problems.push(`Request failed: ${p.errorText} ${frontendRequests.get(p.requestId)}`);
        break;
      case 'Network.webSocketCreated':
        if (p.url.includes('localhost:44030')) backendSockets.add(p.requestId);
        break;
      case 'Network.webSocketFrameReceived':
        if (backendSockets.has(p.requestId) && /"method"\s*:\s*"State"/.test(p.response.payloadData))
          backendState = true;
        break;
      case 'Runtime.exceptionThrown':
        problems.push(`Uncaught: ${p.exceptionDetails.exception?.description ?? p.exceptionDetails.text}`);
        break;
      case 'Runtime.consoleAPICalled':
        if (p.type === 'error') {
          const text = p.args.map((a) => a.value ?? a.description ?? '').join(' ');
          console.log(`::warning::console.error: ${text.slice(0, 500)}`);
        }
        break;
    }
  });
  await cdp.send('Network.enable');
  await cdp.send('Runtime.enable');
  await cdp.send('Page.enable');
  // Reload so every request is observed from the start
  await cdp.send('Network.setCacheDisabled', { cacheDisabled: true });
  await cdp.send('Page.reload', { ignoreCache: true });

  const menuCheck = `[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Settings')`;
  // Stops early once an asset or script has failed, since the menu won't recover from that
  const menuResult = await waitFor(async () => {
    if (problems.length > 0) return 'problems';
    const res = await cdp.send('Runtime.evaluate', { expression: menuCheck, returnByValue: true });
    return res.result?.result?.value === true;
  }, 60_000);
  const rendered = menuResult === true;
  const connected = rendered && (await waitFor(async () => backendState || problems.length > 0, 30_000));

  // Menu label -> the page's h1
  const pages = [
    ['Replay Buffer', 'Replay Buffer'],
    ['Clips', 'Clips'],
    ['Highlights', 'Highlights'],
    ['Settings', 'Settings'],
    ['Full Sessions', 'Sessions'],
  ];
  for (const [label, heading] of connected && problems.length === 0 ? pages : []) {
    const open = `(() => {
      const button = [...document.querySelectorAll('button')].find(b => b.textContent.trim().startsWith(${JSON.stringify(label)}));
      button?.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
      return !!button;
    })()`;
    const found = await cdp.send('Runtime.evaluate', { expression: open, returnByValue: true });
    if (found.result?.result?.value !== true) {
      fail(`Menu item "${label}" not found`);
      continue;
    }
    const headingCheck = `[...document.querySelectorAll('h1')].some(h => h.textContent.trim() === ${JSON.stringify(heading)})`;
    const shown = await waitFor(async () => {
      if (problems.length > 0) return 'problems';
      const res = await cdp.send('Runtime.evaluate', { expression: headingCheck, returnByValue: true });
      return res.result?.result?.value === true;
    }, 10_000);
    if (shown === true) console.log(`Page ok: ${label}`);
    else if (shown !== 'problems') fail(`Page "${label}" never showed its "${heading}" heading`);
    if (problems.length > 0) break;
  }

  // The runner has no recordings, so a fake session is merged into the state to open the video page
  if (connected && problems.length === 0) {
    const fake = {
      id: 'smoke-test',
      type: 'Session',
      title: 'Smoke test',
      game: 'Smoke Test',
      bookmarks: [],
      fileName: 'smoke-test.mp4',
      filePath: 'C:/smoke-test/smoke-test.mp4',
      fileSize: '1 MB',
      fileSizeKb: 1024,
      duration: '00:00:10',
      createdAt: new Date().toISOString(),
      isImported: false,
      compressed: false,
    };
    const openVideo = `(() => {
      window.dispatchEvent(new CustomEvent('websocket-message', { detail: { method: 'State', content: { content: [${JSON.stringify(fake)}] } } }));
    })()`;
    await cdp.send('Runtime.evaluate', { expression: openVideo });
    const clicked = await waitFor(async () => {
      if (problems.length > 0) return 'problems';
      const res = await cdp.send('Runtime.evaluate', {
        expression: `(() => { const card = document.querySelector('[data-content-id="smoke-test"]'); card?.click(); return !!card; })()`,
        returnByValue: true,
      });
      return res.result?.result?.value === true;
    }, 10_000);
    if (clicked === true) {
      const shown = await waitFor(async () => {
        if (problems.length > 0) return 'problems';
        const res = await cdp.send('Runtime.evaluate', { expression: `!!document.querySelector('video') && ![...document.querySelectorAll('h1')].some(h => h.textContent.trim() === 'Sessions')`,
          returnByValue: true,
        });
        return res.result?.result?.value === true;
      }, 10_000);
      if (shown === true) console.log('Page ok: Video');
      else if (shown !== 'problems') fail('Video page never showed its player');
    } else if (clicked !== 'problems') {
      fail('Injected session card never appeared');
    }
  }
  await sleep(2000);

  const shot = await cdp.send('Page.captureScreenshot', { format: 'png' });
  if (shot.result?.data) writeFileSync(screenshotPath, Buffer.from(shot.result.data, 'base64'));

  if (!rendered) fail('Main menu never rendered');
  else if (!connected) fail('Backend never sent State over the websocket');
  if (appExit) fail(appExit);
  problems.forEach((p) => fail(p));
  if (process.exitCode !== 1) console.log('UI smoke test passed');
} catch (err) {
  fail(err.message);
} finally {
  cdp?.close();
  try {
    execSync(`taskkill /PID ${app.pid} /T /F`, { stdio: 'ignore' });
  } catch {}
}
