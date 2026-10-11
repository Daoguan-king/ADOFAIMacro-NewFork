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
const kw = /强弱|独有/i;
let hits = 0;
for (const f of all) {
  let txt;
  try { txt = decodeFrames(fs.readFileSync(f)); } catch { continue; }
  for (const line of txt.split(/\r?\n/)) {
    if (!line || !kw.test(line)) continue;
    let o; try { o = JSON.parse(line); } catch { continue; }
    const s = JSON.stringify(o);
    if (/自动生成|automatically generated checkpoint|compacted-summary/.test(s)) continue;
    const c = o?.message?.content ?? o?.data?.content ?? o?.content;
    let text = "";
    if (typeof c === "string") text = c;
    else if (Array.isArray(c)) text = c.map(x => (typeof x === "string" ? x : x?.text || "")).join("");
    if (!text || !kw.test(text)) continue;
    if (text.length > 3000) continue;
    hits++;
    const ts = o?.timestamp ?? o?.time ?? "";
    console.log(`\n===== ${path.basename(path.dirname(f))} | ${o?.message?.role ?? o?.type ?? "?"} | ${ts} =====`);
    console.log(text.slice(0, 1500));
  }
}
console.log(`\n命中(粗筛后): ${hits}`);
