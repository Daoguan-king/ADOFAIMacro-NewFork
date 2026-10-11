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

const kw = /强弱|强拍|弱拍|重音|独有|accent|downbeat/i;
for (const f of walk(ROOT)) {
  let txt; try { txt = decodeFrames(fs.readFileSync(f)); } catch { continue; }
  for (const l of txt.split(/\r?\n/)) {
    if (!l.trim() || !kw.test(l)) continue;
    let o; try { o = JSON.parse(l); } catch { continue; }
    if (o.type !== "user/message") continue;
    if (o?.data?.source?.kind !== "user") continue;
    const t = textOf(o);
    if (!t || !kw.test(t)) continue;
    console.log("=".repeat(85));
    console.log(`${path.basename(path.dirname(f))}  ${new Date(o.time).toISOString()}`);
    console.log(t.slice(0, 1600));
  }
}
console.log("\n[done]");