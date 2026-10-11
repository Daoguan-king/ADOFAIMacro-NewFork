import fs from "node:fs";
import { decodeFrames } from "./sessionread.mjs";
const F = process.argv[2];
const txt = decodeFrames(fs.readFileSync(F));
const lines = txt.split(/\r?\n/).filter(l => l.trim());
let shown = 0;
for (let i = 0; i < lines.length; i++) {
  let o; try { o = JSON.parse(lines[i]); } catch { continue; }
  if (o.type !== "user/message") continue;
  shown++;
  if (shown > 3) break;
  console.log("=" .repeat(90));
  console.log(`L${i}`);
  console.log(JSON.stringify(o, null, 1).slice(0, 3000));
}