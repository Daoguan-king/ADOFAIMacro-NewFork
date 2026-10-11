import fs from "node:fs";
import path from "node:path";
import { decodeFrames } from "./sessionread.mjs";

const ROOT = "C:/Users/22282/.dsh/sessions";
function* walk(root) {
  let ents = [];
  try { ents = fs.readdirSync(root, { withFileTypes: true }); } catch { return; }
  for (const e of ents) {
    const p = path.join(root, e.name);
    if (e.isDirectory()) yield* walk(p);
    else if (/^session(\.v\d+)?\.jsonl\.zstd$/.test(e.name)) yield p;
  }
}
const all = [...walk(ROOT)];
console.log(`会话文件数: ${all.length}`);
const kw = /强弱|强拍|弱拍|重音|accent/i;
let hits = 0;
for (const f of all) {
  let txt;
  try { txt = decodeFrames(fs.readFileSync(f)); } catch { continue; }
  const lines = txt.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    if (!lines[i] || !kw.test(lines[i])) continue;
    let o;
    try { o = JSON.parse(lines[i]); } catch { continue; }
    const s = JSON.stringify(o);
    if (!/"role":"user"/.test(s)) continue;
    const c = o?.message?.content ?? o?.data?.content ?? o?.content;
    let text = "";
    if (typeof c === "string") text = c;
    else if (Array.isArray(c)) text = c.map(x => (typeof x === "string" ? x : x?.text || "")).join("");
    if (!text || !kw.test(text)) continue;
    hits++;
    console.log(`\n--- ${path.basename(path.dirname(f))} L${i} ---`);
    console.log(text.slice(0, 1200));
  }
}
console.log(`\n命中用户消息数: ${hits}`);
