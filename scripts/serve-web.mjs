// Serves the built web version (site/, from build-web.ps1) at http://localhost:8080 for trying it out locally.
// Browsers only let pages write to folders from https:// or localhost, and never from file://.
// Usage: node scripts/serve-web.mjs [folder] [port]
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(process.argv[2] ?? path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'site'));
const port = Number(process.argv[3] ?? 8080);
const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript',
  '.mjs': 'text/javascript',
  '.css': 'text/css',
  '.json': 'application/json',
  '.wasm': 'application/wasm',
  '.png': 'image/png',
  '.txt': 'text/plain; charset=utf-8',
  '.webmanifest': 'application/manifest+json',
};

http.createServer((request, response) => {
  const url = decodeURIComponent(new URL(request.url ?? '/', 'http://localhost').pathname);
  const file = path.join(root, url.endsWith('/') ? `${url}index.html` : url);
  if (!file.startsWith(root) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    response.writeHead(404).end('Not found');
    return;
  }
  response.writeHead(200, { 'Content-Type': types[path.extname(file)] ?? 'application/octet-stream' });
  fs.createReadStream(file).pipe(response);
}).listen(port, '0.0.0.0', () => console.log(`GameFerry: http://localhost:${port}/  (serving ${root}; Ctrl+C to stop)`));
