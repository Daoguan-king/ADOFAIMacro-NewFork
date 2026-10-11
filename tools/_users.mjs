import fs from "node:fs";
import { decodeFrames } from "./sessionread.mjs";

const F = process.argv[2];
const FILTER = process.argv[3] || "";
const txt = decodeFrames(fs.readFileSync(F));
const lines = txt.split(/\r?\n/).filter(l => l.trim());

function textOf(o) {
  const c = o?.data?.content ?? o?.data?.message?.content ?? o?.message?.content;
  if (typeof c === "string") return c;
  if (Array.isArray(c)) return c.map(x => typeof x === "string" ? x : (x?.type === "text" ? x.text : "")).join("");
  return "";
}

let n = 0;
for (let i = 0; i < lines.length; i++) {
  let o; try { o = JSON.parse(lines[i]); } catch { continue; }
  if (o.type !== "user/message") continue;
  const kind = o?.data?.source?.kind;
  const t = textOf(o);
  if (!t) continue;
  if (kind !== "user") {
    if (/compacted-summary|This session is being continued|automatically generated/i.test(t)) {
      console.log(`\n##### [压缩摘要 L${i}] #####\n${t.slice(0, 4000)}`);
    }
    continue;
  }
  if (FILTER && !t.includes(FILTER)) continue;
  n++;
  console.log(`\n===== USER #${n} | L${i} | ${new Date(o.time).toISOString()} =====`);
  console.log(t.slice(0, 2000));
}
console.log(`\n[共 ${n} 条真实用户消息]`);