// Tiny static server for previewing the tablet UI in a desktop browser.
// The UI falls back to its mock-data harness whenever window.DeckNative is absent, so the
// entire dashboard can be designed and reviewed without deploying an APK.
//
// Two query parameters drive states that are otherwise awkward to reach:
//   ?setup=new|half|done        the first-run guide, at each stage
//   ?caps=-fans,-gpu,-brightness  drops capabilities, to check that nothing
//                                 unsupported is drawn

const http = require('http');
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..', 'tablet', 'app', 'src', 'main', 'assets');
const PORT = 5173;

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.json': 'application/json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
};

http.createServer((req, res) => {
  let rel = decodeURIComponent(req.url.split('?')[0]);
  if (rel === '/' || rel === '') rel = '/index.html';

  // Keep the server inside the assets directory.
  const file = path.normalize(path.join(ROOT, rel));
  if (!file.startsWith(ROOT)) { res.writeHead(403).end('forbidden'); return; }

  fs.readFile(file, (err, buf) => {
    if (err) { res.writeHead(404).end('not found'); return; }
    res.writeHead(200, {
      'Content-Type': MIME[path.extname(file).toLowerCase()] || 'application/octet-stream',
      'Cache-Control': 'no-store',
    });
    res.end(buf);
  });
}).listen(PORT, () => console.log(`UI preview on http://localhost:${PORT}`));
