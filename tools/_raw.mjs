import fs from "node:fs";
import { decodeFrames } from "./sessionread.mjs";

const f = "C:/Users/22282/.dsh/sessions/--D-Projects-ADOFAIMacro--/session-2cad7a93-7409-4bc1-a026-dd8730096a6b/session.v4.jsonl.zstd";
const txt = decodeFrames(fs.readFileSync(f));
const lines = txt.split(/\r?\n/);
const target = "强弱拍";

// 找所有提到强弱拍的 raw 行
const hits = [];
for (let i = 0; i < lines.length; i++) {
  if (lines[i] && lines[i].includes(target)) hits.push(i);
}
console.log("提到强弱拍的行:", hits.join(", "));
console.log("");

for (const i of hits) {
  console.log("=".repeat(90));
  console.log(`RAW L${i}:`);
  console.log(lines[i].slice(0, 4000));
  console.log("");
}
