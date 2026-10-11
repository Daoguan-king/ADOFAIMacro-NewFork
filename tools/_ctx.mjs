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
const target = "我感觉 这个解决方法 很好 但是丢弃了独有的强弱拍手法";
for (const f of walk(ROOT)) {
  let txt;
  try { txt = decodeFrames(fs.readFileSync(f)); } catch { continue; }
  if (!txt.includes(target)) continue;
  const lines = txt.split(/\r?\n/);
  console.log(`### 命中文件: ${f}`);
  console.log(`### 总行数: ${lines.length}`);
  let idx = lines.findIndex(l => l.includes(target));
  console.log(`### 命中行: ${idx}`);
  console.log("");
  // 打印前 40 行到后 5 行
  const from = Math.max(0, idx - 40), to = Math.min(lines.length - 1, idx + 3);
  for (let i = from; i <= to; i++) {
    const l = lines[i];
    if (!l) continue;
    let o; try { o = JSON.parse(l); } catch { console.log(`  L${i} [raw] ${l.slice(0,200)}`); continue; }
    const role = o?.message?.role ?? o?.data?.role ?? o?.type ?? "?";
    const c = o?.message?.content ?? o?.data?.content ?? o?.content;
    let text = "";
    if (typeof c === "string") text = c;
    else if (Array.isArray(c)) text = c.map(x => (typeof x === "string" ? x : x?.text || x?.name || JSON.stringify(x).slice(0,120))).join("\n");
    else if (c) text = JSON.stringify(c).slice(0, 800);
    const mark = i === idx ? "  <<<<< 用户的问题" : "";
    console.log(`\n----- L${i} [${role}]${mark} -----`);
    console.log(text.slice(0, 2500));
  }
  break;
}
