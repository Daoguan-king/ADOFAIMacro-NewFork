import fs from 'node:fs';
import { decodeFrames } from './sessionread.mjs';

const F = process.argv[2];
const FROM = parseInt(process.argv[3] || '0');
const TO = parseInt(process.argv[4] || '999999');
const KWS = (process.argv[5] || '兼容,分摊,多键,强弱,方案,解法,思路,solution,preserve').split(',');

const txt = decodeFrames(fs.readFileSync(F));
const lines = txt.split(/\r?\n/).filter(l => l.trim());

function textOf(o) {
  const c = o?.data?.content ?? o?.data?.message?.content ?? o?.message?.content;
  if (typeof c === 'string') return c;
  if (Array.isArray(c)) return c.map(x => typeof x === 'string' ? x : (x?.type === 'text' ? x.text : '')).join('');
  return '';
}

let n = 0;
for (let i = FROM; i < Math.min(TO, lines.length); i++) {
  let o; try { o = JSON.parse(lines[i]); } catch { continue; }
  const t = textOf(o);
  if (!t) continue;
  if (o.type === 'user/message') {
    if (o?.data?.source?.kind !== 'user') continue;
    console.log('\n##### L' + i + ' USER ' + new Date(o.time).toISOString() + ' #####\n' + t.slice(0, 1500));
    continue;
  }
  if (o.type !== 'assistant/message') continue;
  if (!KWS.some(k => t.includes(k))) continue;
  n++;
  console.log('\n----- L' + i + ' ASSIST ' + new Date(o.time).toISOString() + ' -----\n' + t.slice(0, 900));
}
console.log('\n[命中 ' + n + ']');