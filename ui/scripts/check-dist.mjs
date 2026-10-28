// Post-build guard for the CSP (3.3 spec sections 6 and 10): index.html must not contain inline
// scripts or styles, and the initial JavaScript should stay under ~250 KB gzipped (a warning).
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';

const dist = new URL('../dist/', import.meta.url).pathname;
const html = readFileSync(join(dist, 'index.html'), 'utf8');
const problems = [];

for (const match of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
  const [, attrs, body] = match;
  if (!/\bsrc=/.test(attrs) || body.trim() !== '') problems.push(`inline <script>: ${match[0].slice(0, 80)}`);
}
if (/<style\b/i.test(html)) problems.push('inline <style> element');
if (/\sstyle=/i.test(html)) problems.push('inline style attribute');
if (/\son[a-z]+=/i.test(html)) problems.push('inline event handler attribute');

if (problems.length > 0) {
  console.error('index.html breaks the CSP:\n  ' + problems.join('\n  '));
  process.exit(1);
}

const entryScripts = [...html.matchAll(/<script\b[^>]*\bsrc="\/([^"]+)"/gi)].map((m) => m[1]);
const preloads = [...html.matchAll(/<link\b[^>]*rel="modulepreload"[^>]*href="\/([^"]+)"/gi)].map((m) => m[1]);
const initial = [...new Set([...entryScripts, ...preloads])];
const gzipped = initial.reduce((sum, file) => sum + gzipSync(readFileSync(join(dist, file))).length, 0);
const kb = (gzipped / 1024).toFixed(1);
if (gzipped > 250 * 1024) console.warn(`warning: initial JavaScript is ${kb} KB gzipped (guideline: 250 KB)`);
else console.log(`initial JavaScript: ${kb} KB gzipped; index.html has no inline script or style`);
