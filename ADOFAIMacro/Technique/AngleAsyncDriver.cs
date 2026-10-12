using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using ADOFAIMacro.Core;
using ADOFAIMacro.Timing;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 角度驱动的**异步发键线程**（2026-10-12，用户要求「给角度驱动实现异步的形式」）。
    ///
    /// 【问题】角度驱动原实现把「判定时刻换算 + 等到点 + 发键」全放在主线程每帧的
    /// AngleDriverTick 里 —— 主线程每帧只检查一次「到点没有」，于是发键延迟
    /// = 0~1 帧（16.7ms@60fps），主线程卡顿时更久。用户诊断「角度驱动不是异步的」
    /// 正是这个（fact 0mv4noien）。
    ///
    /// 【职责划分】判定语义**逐字不变**，只把「等待」搬出主线程：
    ///   · 主线程（AngleDriverTick）：推进游标、按原公式算出事件的绝对目标 tick，
    ///     减去提前量得到 eventTicks，然后**发布**到本线程的待发队列。
    ///   · 本线程：精确等到 eventTicks 到点，再发键（带同一时间戳 → 判定精度不变）。
    ///
    /// 【与帧同步路径的等价性】发出的事件序列逐条相同（发布顺序 = 游标顺序），
    /// 每个事件的 eventTicks 用同一公式、同一钳位。唯一差别是**何时执行 SendKey**：
    /// 帧同步「下一帧检查时发」（晚 0~1 帧），异步「到点就发」（微秒级）。
    /// 副作用是超密段的同 tick 批次被摊开成各自精确时刻 —— 比原来更好
    ///（AppHang 的两个成因之一正是「游戏同帧消费巨量同 tick 事件」）。
    ///
    /// 【安全边界】
    ///   ① 时间戳恒 ≤ now：主线程发布前已钳位，本线程发出前再封顶一次当下，
    ///      绝不发未来时间戳（未来戳会让游戏把行星角度推进未来 → 瞬移/抖动）。
    ///   ② 暂停/死亡/离开 PlayerControl/砖序回退/换关 → 主线程 FlushAndHold
    ///      清空队列并要求线程停等 + 松开自己按下的键（防卡键）。
    ///   ③ 队列有界（4096），满则丢最旧并计数 —— 绝不无界增长。
    ///   ④ 线程内异常一律吞掉并计数，绝不影响游戏主线程。
    ///   ⑤ 发布失败（线程未运行/队列满）→ 调用方退回主线程直接发，保证不丢键。
    ///
    /// 【等待设施】复用时间驱动那条链路：HighResSleep（HIGH_RESOLUTION 可等待
    /// 定时器 ~0.5ms）+ 末段 1.5ms 自旋，与 KeyWorker.WorkerLoop 同款。
    /// </summary>
    internal static partial class MacroEngine
    {
        // ── 待发事件（值类型，零 GC 压力）────────────────────────
        private struct AadPending
        {
            public long EventTicks;     // 事件时间戳（= hitStar - lead，主线程已钳位）
            public double TriggerTime;  // 诊断用
            public byte KeyCode;
            public byte ReleaseKeyCode;
            public bool ReleaseOnly;
            public bool IsHoldRelated;
            public bool SimulateKey;
            public bool EnableTechnique;
        }

        private const int AadCapacity = 4096;

        private static readonly AadPending[] _aadQueue = new AadPending[AadCapacity];
        private static int _aadHead;
        private static int _aadTail;
        private static int _aadCount;
        private static readonly object _aadLock = new();

        // 唤醒信号（空队列时等待；100ms 超时兜底，保证 _aadRunning/_aadHold 变化能被看到）
        private static readonly SemaphoreSlim _aadWake = new(0, 1);

        private static volatile bool _aadRunning;
        private static volatile bool _aadHold;          // 停等（暂停/回退/换关）
        private static volatile bool _aadNeedRelease;   // 停等时要求线程松开自己的键
        private static Thread? _aadThread;

        // 本线程私有的按键记账（不碰主线程的 _angleDownKeys —— 跨线程访问不安全）
        private static readonly HashSet<byte> _aadDownKeys = new(4);
        private static byte _aadHoldKey;
        private static bool _aadHoldDown;

        // ── 诊断计数 ──────────────────────────────────────────────
        private static long _aadPublished;
        private static long _aadFired;
        private static long _aadDropped;
        private static long _aadLate;
        private static long _aadMaxLateTicks;
        private static long _aadErrors;
        private static long _aadBurstFires;   // 追账突发（同一 16.7ms 窗口内 >预算）

        internal static bool AngleAsyncRunning => _aadRunning;

        /// <summary>诊断行（[Macro-AngleAsync] 探针）。</summary>
        internal static string AngleAsyncStats =>
            $"发布 {Volatile.Read(ref _aadPublished)} / 发出 {Volatile.Read(ref _aadFired)}" +
            $" / 丢弃 {Volatile.Read(ref _aadDropped)} / 迟发 {Volatile.Read(ref _aadLate)}" +
            $" / 最大迟发 {Volatile.Read(ref _aadMaxLateTicks) / 1e4:F2}ms" +
            $" / 突发 {Volatile.Read(ref _aadBurstFires)} / 异常 {Volatile.Read(ref _aadErrors)}";

        /// <summary>启动异步发键线程（幂等）。失败返回 false，调用方退回帧同步路径。</summary>
        private static bool EnsureAngleAsyncRunning()
        {
            if (_aadRunning && _aadThread?.IsAlive == true) return true;
            try
            {
                lock (_aadLock)
                {
                    _aadHead = _aadTail = _aadCount = 0;
                    _aadHold = false;
                    _aadNeedRelease = false;
                    _aadRunning = true;
                }
                var t = new Thread(AngleAsyncLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Highest,
                    Name = "MacroAngleAsync"
                };
                _aadThread = t;
                t.Start();
                Main.Log("[Macro-AngleAsync] 异步发键线程已启动");
                return true;
            }
            catch (Exception ex)
            {
                _aadRunning = false;
                Main.Log($"[Macro-AngleAsync] 线程启动失败（退回帧同步）：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>停止线程（换关/模组停用）。幂等。</summary>
        private static void StopAngleAsync()
        {
            if (!_aadRunning) return;
            _aadRunning = false;
            try { _aadWake.Release(); } catch (SemaphoreFullException) { }
            var t = _aadThread;
            _aadThread = null;
            if (t != null && t.IsAlive) t.Join(500);
            lock (_aadLock) { _aadHead = _aadTail = _aadCount = 0; }
        }

        /// <summary>
        /// 主线程：发布一个待发事件。返回 false 表示未入队（线程未运行/队列满），
        /// 调用方应退回帧同步路径直接发（保证不丢键）。
        /// </summary>
        private static bool PublishAngleEvent(long eventTicks, double triggerTime, byte keyCode,
            byte releaseKeyCode, bool releaseOnly, bool isHoldRelated,
            bool simulateKey, bool enableTechnique)
        {
            if (!_aadRunning) return false;
            lock (_aadLock)
            {
                if (_aadCount >= AadCapacity)
                {
                    // 满：丢最旧（游标只增，最旧的大概率已被后续事件覆盖语义）
                    _aadHead = (_aadHead + 1) % AadCapacity;
                    _aadCount--;
                    Interlocked.Increment(ref _aadDropped);
                }
                _aadQueue[_aadTail] = new AadPending
                {
                    EventTicks = eventTicks,
                    TriggerTime = triggerTime,
                    KeyCode = keyCode,
                    ReleaseKeyCode = releaseKeyCode,
                    ReleaseOnly = releaseOnly,
                    IsHoldRelated = isHoldRelated,
                    SimulateKey = simulateKey,
                    EnableTechnique = enableTechnique,
                };
                _aadTail = (_aadTail + 1) % AadCapacity;
                _aadCount++;
            }
            Interlocked.Increment(ref _aadPublished);
            try { _aadWake.Release(); } catch (SemaphoreFullException) { }
            return true;
        }

        /// <summary>
        /// 主线程：清空待发队列 + 要求线程停等并松开自己按下的键。
        /// 暂停/死亡/离开 PlayerControl/砖序回退/换关时调用（防卡键、防越界发键）。
        /// </summary>
        private static void FlushAngleAsync()
        {
            if (!_aadRunning) return;
            _aadHold = true;
            _aadNeedRelease = true;
            lock (_aadLock) { _aadHead = _aadTail = _aadCount = 0; }
            try { _aadWake.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>主线程：恢复发布（回到 PlayerControl 且状态正常）。</summary>
        private static void ResumeAngleAsync()
        {
            if (!_aadRunning) return;
            if (_aadHold)
            {
                _aadHold = false;
                Main.Log("[Macro-AngleAsync] 恢复发布");
            }
        }

        // ─────────────────────────────────────────────────────────
        //  线程主体
        // ─────────────────────────────────────────────────────────
        private static void AngleAsyncLoop()
        {
            // 让定时器精度生效（与 WorkerLoop 同款；工作线程自己声明一次）
            timeBeginPeriod(1);
            long lastFrameBucket = 0;
            int burstInBucket = 0;
            try
            {
                while (_aadRunning)
                {
                    // 停等：清队列 + 松开自己按下的键（单线程访问 _aadDownKeys，无竞态）
                    if (_aadHold)
                    {
                        if (_aadNeedRelease)
                        {
                            _aadNeedRelease = false;
                            ReleaseAllAsyncKeys("主线程要求停等");
                        }
                        try { _aadWake.Wait(100); } catch { }
                        continue;
                    }

                    AadPending ev;
                    bool has;
                    lock (_aadLock)
                    {
                        has = _aadCount > 0;
                        if (has)
                        {
                            ev = _aadQueue[_aadHead];
                            _aadHead = (_aadHead + 1) % AadCapacity;
                            _aadCount--;
                        }
                        else ev = default;
                    }
                    if (!has)
                    {
                        try { _aadWake.Wait(100); } catch { }
                        continue;
                    }

                    try
                    {
                        WaitUntilTicks(ev.EventTicks);

                        long now = PreciseNow.LocalTicks();
                        long late = now - ev.EventTicks;
                        if (late > 10_000)   // >1ms
                        {
                            Interlocked.Increment(ref _aadLate);
                            long prev = Volatile.Read(ref _aadMaxLateTicks);
                            if (late > prev) Volatile.Write(ref _aadMaxLateTicks, late);
                        }

                        // 追账突发统计（同一 ~16.7ms 窗口内发太多 = 跟不上）
                        long bucket = now / 167_000;
                        if (bucket == lastFrameBucket) burstInBucket++;
                        else { lastFrameBucket = bucket; burstInBucket = 1; }
                        if (burstInBucket == 257) Interlocked.Increment(ref _aadBurstFires);

                        FireAngleEventOnWorker(in ev, now);
                        Interlocked.Increment(ref _aadFired);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _aadErrors);
                        if (Volatile.Read(ref _aadErrors) <= 3)
                            Main.Log($"[Macro-AngleAsync] 发键异常（第 {Volatile.Read(ref _aadErrors)} 次）: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _aadErrors);
                Main.Log($"[Macro-AngleAsync] 线程退出异常: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { ReleaseAllAsyncKeys("线程退出"); } catch { }
                timeEndPeriod(1);
                _aadRunning = false;
            }
        }

        /// <summary>
        /// 精确等到目标 tick。目标 tick 与 <see cref="PreciseNow.LocalTicks"/> 同域
        ///（.NET DateTime ticks，1ms = 1e4 ticks）。远 future 用 HighResSleep 睡到
        /// 目标前 1.5ms，末段自旋 —— 与 KeyWorker.WorkerLoop 同款设施。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WaitUntilTicks(long targetTicks)
        {
            const long spinWindow = 15_000;   // 1.5ms
            while (true)
            {
                long remain = targetTicks - PreciseNow.LocalTicks();
                if (remain <= 0) return;

                if (remain > spinWindow)
                {
                    double sleepSec = (remain - spinWindow) / 1e7;
                    if (sleepSec > 0.0005) HighResSleep(sleepSec);
                    else Thread.SpinWait(200);
                }
                else
                {
                    Thread.SpinWait(50);
                }
            }
        }

        /// <summary>
        /// 工作线程上执行发键。与 TracePlayer.FireTraceEvent 逐分支同语义，
        /// 但按键记账用本线程私有的 _aadDownKeys（不碰主线程的 _angleDownKeys）。
        ///
        /// ⚠ 时间戳仍用主线程算好的 ev.EventTicks（不重算），保证与帧同步路径
        /// 发出的事件逐字段相同；发出前再封顶当下（绝无未来时间戳）。
        /// </summary>
        private static void FireAngleEventOnWorker(in AadPending ev, long nowTicks)
        {
            long ticks = ev.EventTicks;
            if (ticks > nowTicks) ticks = nowTicks;      // 封顶当下
            long upTicks = ticks > 0 ? ticks - 1 : 0;    // 松键早 1 tick（确定性排序）

            if (!ev.SimulateKey)
            {
                Interlocked.Increment(ref _workerNeedsHit);
                return;
            }

            if (ev.ReleaseOnly)
            {
                if (ev.EnableTechnique)
                {
                    byte keyToRelease = ev.IsHoldRelated
                        ? (ev.ReleaseKeyCode != 0 ? ev.ReleaseKeyCode : _aadHoldKey)
                        : ev.ReleaseKeyCode;
                    SendKey(keyToRelease, false, upTicks);
                    if (keyToRelease != 0) _aadDownKeys.Remove(keyToRelease);
                    if (ev.IsHoldRelated) { _aadHoldKey = 0; _aadHoldDown = false; }
                }
                else
                {
                    if (ev.IsHoldRelated) WorkerReleaseHoldKey();
                    else WorkerReleaseKey(ev.ReleaseKeyCode);
                }
                return;
            }

            if (ev.IsHoldRelated)
            {
                if (ev.EnableTechnique)
                {
                    if (_aadHoldDown)
                    {
                        SendKey(_aadHoldKey, false, upTicks);
                        _aadHoldKey = 0; _aadHoldDown = false;
                    }
                    SendKey(ev.KeyCode, true, ticks);
                    _aadHoldKey = ev.KeyCode; _aadHoldDown = true;
                }
                else WorkerHoldKey(ev.KeyCode);
                return;
            }

            if (ev.EnableTechnique)
            {
                // 与帧同步路径逐行同语义：裸按下，不预松旧键、不做换键判断。
                SendKey(ev.KeyCode, true, ticks);
                _aadDownKeys.Add(ev.KeyCode);
            }
            else WorkerPressKey(ev.KeyCode);
        }

        /// <summary>
        /// 松开本线程按下的所有残留键（停等/退出兜底，防卡键）。
        /// 只在工作线程内调用（单线程访问 _aadDownKeys）。
        /// </summary>
        private static void ReleaseAllAsyncKeys(string why)
        {
            if (_aadDownKeys.Count == 0 && !_aadHoldDown) return;
            var keys = new List<byte>(_aadDownKeys);
            if (_aadHoldDown && _aadHoldKey != 0) keys.Add(_aadHoldKey);
            foreach (byte k in keys) SendKey(k, false, 0);
            if (keys.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (byte k in keys) { if (sb.Length > 0) sb.Append(','); sb.Append("0x").Append(k.ToString("X2")); }
                Main.Log($"[Macro-AngleAsync] 松开残留按键 {sb}（{why}）");
            }
            _aadDownKeys.Clear();
            if (_aadHoldDown) { _aadHoldKey = 0; _aadHoldDown = false; }
        }
    }
}
