// Evaluate JavaScript inside the tablet's WebView over the Chrome DevTools Protocol. The tablet
// enables web-contents debugging, so its WebView exposes a devtools endpoint on an abstract
// socket.

const PORT = process.env.CDP_PORT || 9222;
// `--file script.js` reads the code from a file, which is the only safe way to pass anything with
// quotes through PowerShell: it strips embedded double quotes from native arguments.
const args = process.argv.slice(2);
const expression = args[0] === '--file'
  ? require('fs').readFileSync(args[1], 'utf8')
  : args.join(' ');

if (!expression) {
  console.error('usage: node tools/cdp.js "<javascript expression>"  |  node tools/cdp.js --file script.js');
  process.exit(2);
}

const main = async () => {
  const listRes = await fetch(`http://127.0.0.1:${PORT}/json/list`);
  const targets = await listRes.json();

  const page = targets.find(t => t.type === 'page' && t.webSocketDebuggerUrl);
  if (!page) {
    console.error('no debuggable page found:', JSON.stringify(targets, null, 2));
    process.exit(3);
  }

  const ws = new WebSocket(page.webSocketDebuggerUrl);

  const done = new Promise((resolve, reject) => {
    ws.addEventListener('open', () => {
      ws.send(JSON.stringify({
        id: 1,
        method: 'Runtime.evaluate',
        params: { expression, returnByValue: true, awaitPromise: true },
      }));
    });

    ws.addEventListener('message', (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.id !== 1) return;
      if (msg.result?.exceptionDetails) {
        reject(new Error(JSON.stringify(msg.result.exceptionDetails, null, 2)));
      } else {
        resolve(msg.result?.result?.value);
      }
      ws.close();
    });

    ws.addEventListener('error', reject);
    setTimeout(() => reject(new Error('timed out')), 10000);
  });

  const value = await done;
  console.log(typeof value === 'string' ? value : JSON.stringify(value, null, 2));
};

main().catch(e => { console.error(String(e.message || e)); process.exit(1); });
