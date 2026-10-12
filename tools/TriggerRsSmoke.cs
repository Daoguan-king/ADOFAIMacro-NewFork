using System;
using System.Runtime.InteropServices;

// StepFfiSmoke —— Rust 触发内核 macro_trigger.dll 的 C# 侧冒烟测试。
// 验证：①P/Invoke 签名与 Rust #[repr(C)] 布局一致；②StepTick 的触发判定
// 与 C# WorkerLoop 的语义等价（用构造的锚点+事件表跑三个场景）。
// 用法：Roslyn csc 编译后直接运行，不需要游戏。
static class StepFfiSmoke
{
    // ── 与 lib.rs 严格一致的 FFI 结构 ──────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    struct Anchor
    {
        public IntPtr Events;
        public int EventCount;
        public int ResetVersion;
        public long QpcSnapshot;
        public long QpcFreq;
        public double SongPosRef;
        public double Rate;
        public double TimeOffset;
        public double AnchorStaleSec;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HitEventFfi
    {
        public double TriggerTime;
        public byte KeyCode;
        public byte ReleaseOnly;
        public byte IsHoldRelated;
        public byte ReleaseKeyCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaitAdvice
    {
        public double SleepSec;
        public double DeltaSec;
    }

    const int ACTION_NONE = 0, ACTION_PRESS = 1, ACTION_RELEASE = 2, ACTION_HOLD_PRESS = 3,
              ACTION_ADVANCE = 4, ACTION_FINISHED = 5, ACTION_WAIT_AGAIN = 6, ACTION_SLEEP = 7;

    [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
    static extern IntPtr StepCreate();

    [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
    static extern void StepFree(IntPtr session);

    [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
    static extern unsafe int StepTick(IntPtr session, ref Anchor anchor, int eventIndex, long qpcNow, ref WaitAdvice wait);

    [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
    static extern int StepStats(IntPtr session, out ulong ticks, out ulong badInputs);

    static int _fail;
    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name + (ok ? "" : "  ← " + detail));
        if (!ok) _fail++;
    }

    static long QpcFreq = System.Diagnostics.Stopwatch.Frequency;

    static unsafe int Main()
    {
        var sess = StepCreate();

        // ── 场景 0：版本吸收语义 — 首次 tick 恒返回 NONE（与 C# resync 挂起等价）──
        {
            var evs = new HitEventFfi[]
            {
                new HitEventFfi { TriggerTime = 1.000, KeyCode = 0x44 },
                new HitEventFfi { TriggerTime = 2.000, KeyCode = 0x46 }, // 哨兵
            };
            fixed (HitEventFfi* p = evs)
            {
                var a = MkAnchor((IntPtr)p, 2);
                var w = new WaitAdvice();
                int act = StepTick(sess, ref a, 0, a.QpcSnapshot, ref w);
                Check("版本切换首轮→NONE", act == ACTION_NONE, $"act={act}");
                // 二轮：elapsed=0、songPosRef=0.995、trigger=1.0 → delta=+0.005 → SLEEP
                act = StepTick(sess, ref a, 0, a.QpcSnapshot, ref w);
                Check("版本切换二轮→正常判定(SLEEP)", act == ACTION_SLEEP, $"act={act}");
            }
        }

        // ── 场景 1：未到时刻（delta=+5ms）→ ACTION_SLEEP，建议睡 3.5ms ──
        {
            var evs = new HitEventFfi[]
            {
                new HitEventFfi { TriggerTime = 1.000, KeyCode = 0x44 },
                new HitEventFfi { TriggerTime = 2.000, KeyCode = 0x46 }, // 哨兵
            };
            fixed (HitEventFfi* p = evs)
            {
                var a = MkAnchor((IntPtr)p, 2);
                a.SongPosRef = 0.995;
                long qpcNow = a.QpcSnapshot; // elapsed=0
                // audioNow=0.995, triggerAt=1.000 → delta=+0.005
                var w = new WaitAdvice();
                StepTick(sess, ref a, 0, qpcNow, ref w); // 先吸收版本
                int act = StepTick(sess, ref a, 0, qpcNow, ref w);
                Check("未到时刻→SLEEP", act == ACTION_SLEEP, $"act={act}");
                Check("睡眠建议≈3.5ms", Math.Abs(w.SleepSec - 0.0035) < 1e-9, $"{w.SleepSec:F6}");
                Check("delta≈+5ms", Math.Abs(w.DeltaSec - 0.005) < 1e-9, $"{w.DeltaSec:F6}");
            }
        }

        // ── 场景 2：极近（delta=+0.5ms）→ ACTION_WAIT_AGAIN ──
        {
            var evs = new HitEventFfi[]
            {
                new HitEventFfi { TriggerTime = 1.000, KeyCode = 0x44 },
                new HitEventFfi { TriggerTime = 2.000, KeyCode = 0x46 }, // 哨兵
            };
            fixed (HitEventFfi* p = evs)
            {
                var a = MkAnchor((IntPtr)p, 2);
                a.SongPosRef = 0.9995;
                long qpcNow = a.QpcSnapshot;
                var w = new WaitAdvice();
                StepTick(sess, ref a, 0, qpcNow, ref w); // 吸收版本
                int act = StepTick(sess, ref a, 0, qpcNow, ref w);
                Check("极近→WAIT_AGAIN", act == ACTION_WAIT_AGAIN, $"act={act}");
            }
        }

        // ── 场景 3：已到时刻（普通音/长按头/松开三分支）──
        // elapsed=0.4s 压在 stale 窗口(0.5s)内；songPosRef=0.6 → audioNow=1.0
        {
            var evs = new HitEventFfi[]
            {
                new HitEventFfi { TriggerTime = 1.000, KeyCode = 0x44 },                                  // 普通按下
                new HitEventFfi { TriggerTime = 2.000, KeyCode = 0x46, IsHoldRelated = 1 },               // 长按头
                new HitEventFfi { TriggerTime = 3.000, KeyCode = 0, ReleaseOnly = 1, ReleaseKeyCode = 0x46 }, // 松开
                new HitEventFfi { TriggerTime = 4.000, KeyCode = 0x44 },                                  // 哨兵
            };
            fixed (HitEventFfi* p = evs)
            {
                var a = MkAnchor((IntPtr)p, 4);
                var w = new WaitAdvice();
                // 每个事件各用专属锚点（songPosRef 移动，elapsed 恒 0.4 < stale 0.5）：
                //   idx0: songPos=0.6  → audioNow=1.0（trigger=1.0, delta=0）
                //   idx1: songPos=1.6  → audioNow=2.0（trigger=2.0）
                //   idx2: songPos=2.6  → audioNow=3.0（trigger=3.0）
                a.AnchorStaleSec = 0.5;
                long q = a.QpcSnapshot + (long)(QpcFreq * 0.4);
                a.SongPosRef = 0.6;
                StepTick(sess, ref a, 0, q, ref w); // 吸收版本
                int act = StepTick(sess, ref a, 0, q, ref w);
                Check("到时刻普通音→PRESS", act == ACTION_PRESS, $"act={act}");

                a.SongPosRef = 1.6;
                act = StepTick(sess, ref a, 1, q, ref w);
                Check("到时刻长按头→HOLD_PRESS", act == ACTION_HOLD_PRESS, $"act={act}");

                a.SongPosRef = 2.6;
                act = StepTick(sess, ref a, 2, q, ref w);
                Check("到时刻松开→RELEASE", act == ACTION_RELEASE, $"act={act}");
                // ★【2026-10-12 语义修正】尾事件（idx == count-1）不再是 FINISHED：
                //   旧语义把最后一拍吞掉（实机「TriggerRs 没有播放完关卡」）。
                //   新语义：idx3（TriggerTime=4.0）在 audioNow=1.0 时刻是「未到」→
                //   先返回 WAIT_AGAIN/SLEEP（等时间推进）；把它推到到点后应 PRESS。
                act = StepTick(sess, ref a, 3, q, ref w);
                Check("尾事件未到时刻→等待（非 FINISHED）", act == ACTION_WAIT_AGAIN || act == ACTION_SLEEP, $"act={act}");
                // 把锚点推到 idx3 的到点时刻，验证最后一拍能正常 PRESS
                a.SongPosRef = 3.6;
                act = StepTick(sess, ref a, 3, a.QpcSnapshot + (long)(QpcFreq * 0.4), ref w);
                Check("尾事件到时刻→PRESS（修复：最后一拍不被吞）", act == ACTION_PRESS, $"act={act}");
            }
        }

        // ── 场景 4：锚点过期 → ACTION_SLEEP ──
        {
            var evs = new HitEventFfi[]
            {
                new HitEventFfi { TriggerTime = 1.000, KeyCode = 0x44 },
                new HitEventFfi { TriggerTime = 2.000, KeyCode = 0x46 }, // 哨兵
            };
            fixed (HitEventFfi* p = evs)
            {
                var a = MkAnchor((IntPtr)p, 2);
                a.AnchorStaleSec = 0.5;
                long qpcNow = a.QpcSnapshot + (long)(QpcFreq * 2.0); // elapsed=2s > 0.5
                var w = new WaitAdvice();
                int act = StepTick(sess, ref a, 0, qpcNow, ref w);
                Check("锚点过期→SLEEP", act == ACTION_SLEEP, $"act={act}");
            }
        }

        // ── 场景 6：诊断统计 ──
        {
            StepStats(sess, out var ticks, out var bad);
            Check("StepStats ticks>0", ticks > 0, $"{ticks}");
            Check("StepStats badInputs==0", bad == 0, $"{bad}");
        }

        StepFree(sess);

        Console.WriteLine(_fail == 0 ? "\n全部通过" : $"\n失败 {_fail} 项");
        return _fail;
    }

    static Anchor MkAnchor(IntPtr events, int count) => new Anchor
    {
        Events = events,
        EventCount = count,
        ResetVersion = 1,
        QpcSnapshot = System.Diagnostics.Stopwatch.GetTimestamp(),
        QpcFreq = QpcFreq,
        SongPosRef = 0.995,
        Rate = 1.0,
        TimeOffset = 0.0,
        AnchorStaleSec = 0.5,
    };
}
