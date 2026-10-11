import fs from "node:fs";
import path from "node:path";
import { decodeFrames } from "./sessionread.mjs";

const F = process.argv[2] || "C:/Users/22282/.dsh/sessions/--D-Projects-ADOFAIMacro--/session-58d4340b-d438-4af4-961f-77c7d0ddbba9/session.v4.jsonl.zstd";
const txt = decodeFrames(fs.readFileSync(F));
const lines = txt.split(/\r?\n/).filter(l => l.trim());
console.log("lines:", lines.length);
const types = {};
for (const l of lines) {
  let o; try { o = JSON.parse(l); } catch { types["<bad>"] = (types["<bad>"]||0)+1; continue; }
  types[o.type] = (types[o.type]||0)+1;
}
console.log("types:", JSON.stringify(types, null, 1));
console.log("\n--- first 3 raw (truncated) ---");
for (const l of lines.slice(0, 3)) console.log(l.slice(0, 1500) + "\n");
console.log("\n--- last 3 raw (truncated) ---");
for (const l of lines.slice(-3)) console.log(l.slice(0, 1500) + "\n");