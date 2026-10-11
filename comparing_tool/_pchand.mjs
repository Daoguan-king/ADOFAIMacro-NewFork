// 逐片比对「参照实现 vs 我们实现」的片划分与 hand，找出第一次手性分歧。
import fs from "node:fs";

function load(f) {
  const out = [];
  for (const l of fs.readFileSync(f, "utf8").split(/\r?\n/)) {
    const m = l.match(/^P (\d+) cnt=(\d+) hand=(\d+) len=([\d.]+) start=([\d.]+) end=([\d.]+) evStart=(\d+) mult=(\d+)/);
    if (m) out.push({ i: +m[1], cnt: +m[2], hand: +m[3], len: +m[4], start: +m[5], end: +m[6], evStart: +m[7], mult: +m[8] });
  }
  return out;
}

const A = load(process.argv[2]);   // 参照
const B = load(process.argv[3]);   // 我们
console.log(`${process.argv[2]}: ${A.length} 片`);
console.log(`${process.argv[3]}: ${B.length} 片`);

// 手性序列（只取 cnt>0 的片，因为只有它们真正按出音符）
function sig(P) {
  const o = [];
  for (const p of P) if (p.cnt > 0) o.push({ i: p.i, hand: p.hand, cnt: p.cnt, start: p.start, mult: p.mult });
  return o;
}
const sa = sig(A), sb = sig(B);
console.log(`\n有音符的片: 参照=${sa.length}  我们=${sb.length}`);

let first = -1;
const n = Math.min(sa.length, sb.length);
for (let k = 0; k < n; k++) {
  if (sa[k].hand !== sb[k].hand) { first = k; break; }
}
console.log(`\n★ 第一次手性分歧：第 ${first} 个有音符的片`);
if (first < 0) { console.log("（前 " + n + " 个全部一致）"); process.exit(0); }

console.log("\n分歧前后 12 个片（参照 | 我们）：");
for (let k = Math.max(0, first - 6); k < Math.min(n, first + 12); k++) {
  const a = sa[k], b = sb[k];
  const mk = k === first ? "  <<< 分歧" : "";
  console.log(`  [${String(k).padStart(4)}] 参照 P${String(a.i).padStart(5)} cnt=${String(a.cnt).padStart(2)} hand=${a.hand} mult=${a.mult} t=${a.start.toFixed(4)}   |   我们 P${String(b.i).padStart(5)} cnt=${String(b.cnt).padStart(2)} hand=${b.hand} mult=${b.mult} t=${b.start.toFixed(4)}${mk}`);
}

// 分歧点的时间与「上一个 restart(mark)」关系：统计分歧点前后 cnt=0 空片数量
function emptyBefore(P, i) {
  let c = 0;
  for (const p of P) { if (p.i >= i) break; if (p.cnt === 0) c++; }
  return c;
}
console.log(`\n分歧点：参照 P${sa[first].i}（此片前共有 ${emptyBefore(A, sa[first].i)} 个空片）`);
console.log(`          我们 P${sb[first].i}（此片前共有 ${emptyBefore(B, sb[first].i)} 个空片）`);

// 全局统计：手性翻转次数 vs 片数
function flips(P) {
  let f = 0, prev = null;
  for (const p of P) { if (p.cnt === 0) continue; if (prev !== null && p.hand !== prev) f++; prev = p.hand; }
  return f;
}
console.log(`\n有音符片之间的手性翻转次数: 参照=${flips(A)}  我们=${flips(B)}`);
console.log(`翻转率（翻转/有音符片）: 参照=${(flips(A) / sa.length * 100).toFixed(1)}%  我们=${(flips(B) / sb.length * 100).toFixed(1)}%`);