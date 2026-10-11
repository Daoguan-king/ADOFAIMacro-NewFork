import fs from "node:fs";
import { decodeFrames } from "./sessionread.mjs";

const F = process.argv[2];
const N = parseInt(process.argv[3] || "25");
const txt = decodeFrames(fs.readFileSync(F));
const lines = txt.split(/\r?\n/).filter(l => l.trim());

function blocks(msg) {
  const c = msg?.content;
  if (typeof c === "string") return [{ kind: "text", text: c }];
  if (!Array.isArray(c)) return [];
  return c.map(x => {
    if (typeof x === "string") return { kind: "text", text: x };
    if (x?.type === "text") return { kind: "text", text: x.text };
    if (x?.type === "reasoning") return { kind: "reason", text: x.text };
    if (x?.type === "tool-call") return { kind: "tool", text: `${x.name} ${String(x.arguments).slice(0, 300)}` };
    return null;
  }).filter(Boolean);
}

const ev = [];
for (let i = 0; i < lines.length; i++) {
  let o; try { o = JSON.parse(lines[i]); } catch { continue; }
  if (o.type === "user/message") {
    const t = blocks(o.data?.message ?? o.message).map(b => b.text).join("");
    if (/automatically generated|compacted-summary/.test(t)) continue;
    ev.push({ i, role: "USER", time: o.time, text: t });
  } else if (o.type === "assistant/message") {
    const bs = blocks(o.data?.message ?? o.message);
    const t = bs.filter(b => b.kind === "text").map(b => b.text).join("\n");
    if (t.trim()) ev.push({ i, role: "ASSIST", time: o.time, text: t });
  } else if (o.type === "turn/end") {
    ev.push({ i, role: "--TURN/END", time: o.time, text: JSON.stringify(o.data).slice(0, 400) });
  }
}
console.log(`total events: ${ev.length}`);
const tail = ev.slice(-N);
for (const e of tail) {
  console.log("=".repeat(90));
  console.log(`[${e.role}] L${e.i} ${e.time ? new Date(e.time).toISOString() : ""}`);
  console.log(e.text.slice(0, 2500));
}