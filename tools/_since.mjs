import fs from "node:fs";
import { decodeFrames } from "./sessionread.mjs";

// 用法: node _since.mjs <file> <isoFrom> [isoTo]
const F = process.argv[2];
const FROM = Date.parse(process.argv[3]);
const TO = process.argv[4] ? Date.parse(process.argv[4]) : Infinity;
const txt = decodeFrames(fs.readFileSync(F));
const lines = txt.split(/\r?\n/).filter(l => l.trim());

function textOf(o) {
  const c = o?.data?.content ?? o?.data?.message?.content ?? o?.message?.content;
  if (typeof c === "string") return c;
  if (Array.isArray(c)) {
    return c.map(x => {
      if (typeof x === "string") return x;
      if (x?.type === "text") return x.text;
      return "";
    }).join("");
  }
  return "";
}

let n = 0;
for (let i = 0; i < lines.length; i++) {
  let o; try { o = JSON.parse(lines[i]); } catch { continue; }
  if (o.time < FROM || o.time > TO) continue;
  if (o.type === "user/message") {
    const kind = o?.data?.source?.kind ?? "?";
    const t = textOf(o);
    if (!t) continue;
    const tag = kind === "user" ? "★USER" : `user(${kind})`;
    console.log(`\n===== L${i} ${tag} ${new Date(o.time).toISOString()} =====`);
    console.log(t.slice(0, 1800));
  } else if (o.type === "assistant/message") {
    const t = textOf(o);
    if (!t.trim()) continue;
    n++;
    console.log(`\n----- L${i} ASSIST ${new Date(o.time).toISOString()} -----`);
    console.log(t.slice(0, 1500));
  }
}
console.log(`\n[assistant text blocks: ${n}]`);