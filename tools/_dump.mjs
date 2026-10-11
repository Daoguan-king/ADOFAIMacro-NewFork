import fs from "node:fs";
import { decodeFrames } from "./sessionread.mjs";

// 用法: node _dump.mjs <file> [fromLine] [toLine] [mode]
// mode: all | chat | user
const F = process.argv[2];
const FROM = parseInt(process.argv[3] || "0");
const TO = parseInt(process.argv[4] || "999999");
const MODE = process.argv[5] || "chat";
const txt = decodeFrames(fs.readFileSync(F));
const lines = txt.split(/\r?\n/);

function textOf(o) {
  const c = o?.data?.content ?? o?.data?.message?.content ?? o?.message?.content;
  if (typeof c === "string") return c;
  if (Array.isArray(c)) {
    return c.map(x => {
      if (typeof x === "string") return x;
      if (x?.type === "text") return x.text;
      if (x?.type === "reasoning") return "[reason] " + x.text;
      if (x?.type === "tool-call") return `[tool ${x.name}] ` + String(x.arguments).slice(0, 400);
      return "";
    }).join("\n");
  }
  return "";
}

for (let i = FROM; i < Math.min(TO, lines.length); i++) {
  if (!lines[i] || !lines[i].trim()) continue;
  let o; try { o = JSON.parse(lines[i]); } catch { console.log(`L${i} [unparsed] ${lines[i].slice(0,300)}`); continue; }
  const t = o.type;
  if (MODE === "user" && t !== "user/message") continue;
  if (MODE === "chat" && !["user/message","assistant/message","turn/end","session/title","tool/call","tool/result"].includes(t)) continue;

  if (t === "tool/call") {
    console.log(`\n----- L${i} TOOLCALL ${o.data?.name} -----\n${String(o.data?.arguments).slice(0,500)}`);
    continue;
  }
  if (t === "tool/result") {
    const s = JSON.stringify(o.data).slice(0, 700);
    console.log(`\n----- L${i} TOOLRESULT -----\n${s}`);
    continue;
  }
  if (t === "session/title") { console.log(`\n### L${i} TITLE: ${JSON.stringify(o.data)}`); continue; }
  if (t === "turn/end") { console.log(`\n----- L${i} TURN/END ----- ${JSON.stringify(o.data).slice(0,500)}`); continue; }

  const kind = o?.data?.source?.kind;
  const body = textOf(o);
  const role = t === "user/message" ? `USER(${kind})` : "ASSIST";
  if (!body.trim()) continue;
  console.log(`\n===== L${i} ${role} ${o.time ? new Date(o.time).toISOString() : ""} =====`);
  console.log(body.slice(0, 3000));
}