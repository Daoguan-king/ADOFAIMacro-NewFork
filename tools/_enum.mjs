import fs from "node:fs";
import path from "node:path";
import { decodeFrames } from "./sessionread.mjs";

const ROOT = "C:/Users/22282/.dsh/sessions/--D-Projects-ADOFAIMacro--";
for (const d of fs.readdirSync(ROOT)) {
  const dir = path.join(ROOT, d);
  if (!fs.statSync(dir).isDirectory()) continue;
  const files = fs.readdirSync(dir).filter(f => /^session(\.v\d+)?\.jsonl\.zstd$/.test(f));
  for (const f of files) {
    const p = path.join(dir, f);
    let txt = "";
    try { txt = decodeFrames(fs.readFileSync(p)); } catch (e) { console.log(d, f, "DECODE FAIL", e.message); continue; }
    const lines = txt.split(/\r?\n/).filter(l => l.trim());
    const st = fs.statSync(p);
    let title = "";
    const userMsgs = [];
    for (const l of lines) {
      let o; try { o = JSON.parse(l); } catch { continue; }
      const t = o?.type;
      if (t === "session/title" || t === "title") title = o?.title ?? o?.data?.title ?? title;
      const role = o?.message?.role;
      if (role === "user") {
        const c = o.message.content;
        let s = "";
        if (typeof c === "string") s = c;
        else if (Array.isArray(c)) s = c.map(x => typeof x === "string" ? x : (x?.text || "")).join("");
        if (s && !s.startsWith("<system") && !s.includes("automatically generated")) userMsgs.push(s.replace(/\s+/g, " ").slice(0, 120));
      }
    }
    console.log("=" + "=".repeat(100));
    console.log(`DIR ${d}  FILE ${f}  size=${st.size}  mtime=${st.mtime.toISOString()}`);
    console.log(`TITLE: ${title}`);
    console.log(`LINES: ${lines.length}  USERMSGS: ${userMsgs.length}`);
    console.log(`FIRST: ${userMsgs[0] ?? ""}`);
    console.log(`LAST : ${userMsgs[userMsgs.length-1] ?? ""}`);
  }
}
