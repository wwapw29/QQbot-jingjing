const fs = require('fs');
const t = fs.readFileSync('G:/workbuddy_PlayGround/QQBot-share/src/QQBot/appsettings.json', 'utf8').replace(/^\uFEFF/, '');
let out = '', i = 0, inStr = false, esc = false;
while (i < t.length) {
  const c = t[i];
  if (esc) { out += c; esc = false; i++; continue; }
  if (inStr) { out += c; if (c === '\\') esc = true; else if (c === '"') inStr = false; i++; continue; }
  if (c === '"') { inStr = true; out += c; i++; continue; }
  if (c === '/' && t[i + 1] === '/') { while (i < t.length && t[i] !== '\n') i++; continue; }
  if (c === '/' && t[i + 1] === '*') { i += 2; while (i < t.length && !(t[i] === '*' && t[i + 1] === '/')) i++; i += 2; continue; }
  out += c; i++;
}
const j = JSON.parse(out);
const p = j.Bot.Prompt;
for (const [k, v] of Object.entries(p)) {
  if (typeof v === 'string' && v.length > 0) {
    console.log('### [' + k + '] (' + v.length + '字)');
    console.log(v.slice(0, 250) + (v.length > 250 ? '…' : ''));
    console.log('');
  } else if (typeof v === 'object' && v) {
    console.log('### [' + k + '] (对象)');
    for (const [k2, v2] of Object.entries(v)) {
      if (typeof v2 === 'string' && v2.length > 0) console.log('  - ' + k2 + ': ' + v2.slice(0, 150));
    }
    console.log('');
  }
}
