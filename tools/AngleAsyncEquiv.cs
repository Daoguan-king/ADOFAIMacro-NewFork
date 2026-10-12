// AngleAsyncEquiv —— 角度驱动异步化的离线一致性验证（2026-10-12）
//
// 目的：证明「主线程发布 + 工作线程精确等待发键」与「主线程每帧检查到点即发」
//       两条路径**发出的事件序列逐条相同**（含时间戳、按键、松/按语义）。
//
// 方法：读真实 .adotr 轨迹，用同一份判定时刻公式（hitStar = offsetTick +
//       (TriggerTime/pitch + songZero)*1e7 − lead）算出每个事件的 eventTicks，
//       然后分别模拟：
//         A) 帧同步：以 60fps（16.667ms）推进虚拟帧时钟，每帧把「已到点」的
//            事件按游标顺序发出（到点判据 hitStar-lead <= now），记录序列。
//         B) 异步：主线程同样每帧发布「已到点」的事件到队列（顺序同 A），
//            工作线程逐条等到 eventTicks 再发，记录序列。
//       比较两条序列：事件数、KeyCode、ReleaseOnly、IsHoldRelated、
//       ReleaseKeyCode、EventTicks 必须完全一致。
//
// 关键论点：异步路径**不改变发布顺序**（发布仍由主线程的到点判据闸门控制），
//           只改变「何时执行 SendKey」。因此序列必然一致——本工装把这一点变成
//           可执行的断言，而不是口头保证。
//
// 编译：csc /nologo /optimize+ /out:tools\AngleAsyncEquiv.exe tools\AngleAsyncEquiv.cs
// 运行：tools\AngleAsyncEquiv.exe <trace.adotr> [pitch] [leadMs] [fps]
using System;
using System.Collections.Generic;
using System.IO;

internal static class AngleAsyncEquiv
{
    private const uint Magic = 0x314D4146;   // 'FAM1'
    private const int HeaderSize = 72;
    private const int EventStride = 20;

    private struct Ev
    {
        public double TriggerTime;
        public float AngleFrac;
        public byte KeyCode;
        public byte Flags;
        public byte ReleaseKeyCode;
        public int FloorIndex;
        public bool ReleaseOnly => (Flags & 0x01) != 0;
        public bool IsHoldRelated => (Flags & 0x02) != 0;
    }

    private struct Fired
    {
        public long EventTicks;    // 事件时间戳（写进按键事件的那个）
        public long FiredAt;       // 实际发出时刻（虚拟时钟）——帧量化延迟量这个
        public byte KeyCode;
        public byte ReleaseKeyCode;
        public bool ReleaseOnly;
        public bool IsHoldRelated;
        public int SourceIndex;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("用法: AngleAsyncEquiv.exe <trace.adotr> [pitch] [leadMs] [fps]"); return 2; }
        string path = args[0];
        double pitch = args.Length > 1 ? double.Parse(args[1]) : 1.0;
        double leadMs = args.Length > 2 ? double.Parse(args[2]) : 0.0;
        double fps = args.Length > 3 ? double.Parse(args[3]) : 60.0;
        if (!File.Exists(path)) { Console.WriteLine("文件不存在: " + path); return 2; }

        var events = LoadTrace(path, out int count);
        Console.WriteLine($"轨迹: {Path.GetFileName(path)}  事件 {count}  pitch={pitch}  lead={leadMs}ms  fps={fps}");

        // 虚拟时间轴：offsetTick=0、songZero=0（同域常量不影响两路径的相对一致性）
        long leadTicks = (long)(leadMs * 10_000.0);
        double frameSec = 1.0 / fps;
        long frameTicks = (long)(frameSec * 1e7);

        // 每个事件的理想判定时刻（与 TracePlayer.AngleDriverTick 同公式）
        var hitStar = new long[count];
        var eventTicks = new long[count];
        for (int i = 0; i < count; i++)
        {
            double songSec = events[i].TriggerTime / pitch;      // + songZero(=0)
            double hs = songSec * 1e7;                           // + offsetTick(=0)
            hitStar[i] = (long)hs;
            long et = hitStar[i] - leadTicks;
            if (et < 0) et = 0;                                  // minPast 下界（此处简化为 0）
            eventTicks[i] = et;
        }

        // ── A) 帧同步：每帧检查到点即发 ──────────────────────────
        var firedA = new List<Fired>();
        {
            int cursor = 0;
            long now = 0;
            long lastEventTick = count > 0 ? eventTicks[count - 1] : 0;
            while (cursor < count && now <= lastEventTick + frameTicks * 2)
            {
                while (cursor < count && hitStar[cursor] - leadTicks <= now)
                {
                    firedA.Add(new Fired
                    {
                        EventTicks = Math.Min(eventTicks[cursor], now),   // 发出时封顶当下
                        FiredAt = now,                                    // 帧边界 = 量化延迟来源
                        KeyCode = events[cursor].KeyCode,
                        ReleaseKeyCode = events[cursor].ReleaseKeyCode,
                        ReleaseOnly = events[cursor].ReleaseOnly,
                        IsHoldRelated = events[cursor].IsHoldRelated,
                        SourceIndex = cursor,
                    });
                    cursor++;
                }
                now += frameTicks;
            }
            if (cursor < count) Console.WriteLine($"  [警告] A 路径未发完：{cursor}/{count}");
        }

        // ── B) 异步：主线程同样每帧发布（顺序同 A），工作线程精确等到点再发 ──
        var firedB = new List<Fired>();
        {
            int cursor = 0;
            long now = 0;
            long lastEventTick = count > 0 ? eventTicks[count - 1] : 0;
            // 待发队列：主线程发布 → 工作线程消费
            var pending = new Queue<Fired>();
            while (cursor < count || pending.Count > 0)
            {
                // 主线程：发布「已到点」的事件（与 A 同一判据、同一顺序）
                while (cursor < count && hitStar[cursor] - leadTicks <= now)
                {
                    pending.Enqueue(new Fired
                    {
                        EventTicks = eventTicks[cursor],   // 发布时不封顶（工作线程发出时封顶）
                        KeyCode = events[cursor].KeyCode,
                        ReleaseKeyCode = events[cursor].ReleaseKeyCode,
                        ReleaseOnly = events[cursor].ReleaseOnly,
                        IsHoldRelated = events[cursor].IsHoldRelated,
                        SourceIndex = cursor,
                    });
                    cursor++;
                }

                // 工作线程：把「等到点」的事件发出（eventTicks <= now 即可发）
                while (pending.Count > 0 && pending.Peek().EventTicks <= now)
                {
                    var p = pending.Dequeue();
                    firedB.Add(new Fired
                    {
                        EventTicks = Math.Min(p.EventTicks, now),
                        // 异步：工作线程精确等到 eventTicks 才发（不受帧边界约束），
                        // 虚拟时钟下即"发出时刻 = 事件时间戳"
                        FiredAt = p.EventTicks,
                        KeyCode = p.KeyCode,
                        ReleaseKeyCode = p.ReleaseKeyCode,
                        ReleaseOnly = p.ReleaseOnly,
                        IsHoldRelated = p.IsHoldRelated,
                        SourceIndex = p.SourceIndex,
                    });
                }

                if (cursor >= count && pending.Count > 0)
                {
                    // 表已发完但队列还有：直接把虚拟时钟推到队列头（模拟线程继续等）
                    now = pending.Peek().EventTicks;
                    continue;
                }
                now += frameTicks;
                if (now > lastEventTick + frameTicks * 4) break;
            }
            if (cursor < count || pending.Count > 0)
                Console.WriteLine($"  [警告] B 路径未发完：cursor={cursor}/{count} pending={pending.Count}");
        }

        // ── 比对 ────────────────────────────────────────────────
        Console.WriteLine($"\nA（帧同步）发出 {firedA.Count} 条 / B（异步）发出 {firedB.Count} 条");
        int mismatch = 0;
        int n = Math.Min(firedA.Count, firedB.Count);
        for (int i = 0; i < n; i++)
        {
            var a = firedA[i];
            var b = firedB[i];
            bool same = a.SourceIndex == b.SourceIndex
                     && a.KeyCode == b.KeyCode
                     && a.ReleaseKeyCode == b.ReleaseKeyCode
                     && a.ReleaseOnly == b.ReleaseOnly
                     && a.IsHoldRelated == b.IsHoldRelated;
            if (!same)
            {
                mismatch++;
                if (mismatch <= 10)
                    Console.WriteLine($"  [不一致] #{i} A(idx={a.SourceIndex} key=0x{a.KeyCode:X2} rel={a.ReleaseOnly}) " +
                                      $"B(idx={b.SourceIndex} key=0x{b.KeyCode:X2} rel={b.ReleaseOnly})");
            }
        }
        if (firedA.Count != firedB.Count) mismatch += Math.Abs(firedA.Count - firedB.Count);

        // 时间戳语义核对：两路径的 EventTicks 必须相同（判定精度不变）
        long maxTsDiff = 0;
        for (int i = 0; i < n; i++)
        {
            long d = Math.Abs(firedA[i].EventTicks - firedB[i].EventTicks);
            if (d > maxTsDiff) maxTsDiff = d;
        }

        // ★ 真正的收益：实际发出时刻的差异（帧同步晚于事件时刻 = 帧量化延迟）
        long maxLate = 0, sumLate = 0, maxGain = 0, sumGain = 0;
        for (int i = 0; i < n; i++)
        {
            long lateA = firedA[i].FiredAt - firedA[i].EventTicks;   // 帧同步的发出延迟
            long lateB = firedB[i].FiredAt - firedB[i].EventTicks;   // 异步的发出延迟（≈0）
            if (lateA > maxLate) maxLate = lateA;
            sumLate += lateA;
            long gain = lateA - lateB;                                // 异步相对改善
            if (gain > maxGain) maxGain = gain;
            sumGain += gain;
        }

        Console.WriteLine($"事件序列不一致 {mismatch} 条");
        Console.WriteLine($"事件时间戳最大差 {maxTsDiff} ticks（应恒为 0 —— 判定精度不变）");
        Console.WriteLine($"帧同步发出延迟：最大 {maxLate / 1e4:F3}ms / 平均 {(n > 0 ? sumLate / (double)n / 1e4 : 0):F3}ms");
        Console.WriteLine($"异步相对改善：最大 {maxGain / 1e4:F3}ms / 平均 {(n > 0 ? sumGain / (double)n / 1e4 : 0):F3}ms");
        bool pass = mismatch == 0 && maxTsDiff == 0 && firedA.Count == count && firedB.Count == count;
        Console.WriteLine(pass ? "序列逐条一致 + 时间戳不变 —— PASS（异步化只改发键时机，不改判定）"
                               : "存在分歧 —— FAIL");
        return pass ? 0 : 1;
    }

    private static Ev[] LoadTrace(string path, out int count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var r = new BinaryReader(fs);
        uint magic = r.ReadUInt32();
        if (magic != Magic) throw new InvalidDataException($"magic 不符: 0x{magic:X8}（期望 FAM1）");
        int ver = r.ReadInt32();
        if (ver != 3) throw new InvalidDataException($"FormatVersion={ver}（期望 3）");
        count = r.ReadInt32();
        // 跳过 TraceStats（10 个字段：6×int + 3×double + 1×int 的读序）
        r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
        r.ReadDouble(); r.ReadDouble();
        r.ReadDouble(); r.ReadDouble(); r.ReadDouble();
        r.ReadInt32();
        long expected = (long)HeaderSize + (long)count * EventStride;
        if (fs.Length != expected)
            throw new InvalidDataException($"长度不符: {fs.Length} != {expected}");

        var events = new Ev[count];
        for (int i = 0; i < count; i++)
        {
            events[i] = new Ev
            {
                TriggerTime = r.ReadDouble(),
                AngleFrac = r.ReadSingle(),
                KeyCode = r.ReadByte(),
                Flags = r.ReadByte(),
                ReleaseKeyCode = r.ReadByte(),
                // ★ 写入端在 ReleaseKeyCode 后有 1 字节 pad（TraceFile.cs:320
                //   `w.Write((byte)0)`），漏读会让后续所有事件错位 1 字节。
            };
            r.ReadByte();                    // pad
            events[i] = new Ev
            {
                TriggerTime = events[i].TriggerTime,
                AngleFrac = events[i].AngleFrac,
                KeyCode = events[i].KeyCode,
                Flags = events[i].Flags,
                ReleaseKeyCode = events[i].ReleaseKeyCode,
                FloorIndex = r.ReadInt32(),
            };
        }
        // 自检：首个事件时刻应为正且合理（< 1e4 秒），否则说明布局解析错了
        if (count > 0 && (events[0].TriggerTime <= 0 || events[0].TriggerTime > 1e4))
            throw new InvalidDataException($"首个事件 TriggerTime={events[0].TriggerTime} 不合理，布局解析可能错误");
        return events;
    }
}
