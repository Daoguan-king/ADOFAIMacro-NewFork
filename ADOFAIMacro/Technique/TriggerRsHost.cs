using System;
using System.Runtime.InteropServices;
using ADOFAIMacro.Core;

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// TriggerRs（Rust 触发内核）的 C# 互操作宿主。
    ///
    /// 职责：
    ///   - P/Invoke 绑定（macro_trigger.dll 的 5 个导出）；
    ///   - 会话生命周期（LoadTechniqueDll 同款：DLL 搜索路径 = Mods 目录）；
    ///   - 自动回退状态机：连续 N 次异常/坏输入 → 熔断，调用方退回 C# 原路径
    ///     （复用 MacroPatchHub 的「异常达阈值自动卸载」思想）；
    ///   - 诊断计数（StepStats 转发）。
    ///
    /// 安全设计：
    ///   - 所有 P/Invoke 包 try/catch（DllNotFoundException/BadImageFormatException
    ///     → 标记不可用）；
    ///   - 事件表指针只在锚点有效期内有效（C# 侧 events 数组由锚点持有），
    ///     Rust 侧只读、不缓存指针——版本切换时 C# 必须换新锚点，指针自然失效；
    ///   - Rust 侧 panic=abort：若 Rust 真崩了，整个游戏崩（与原生
    ///     TechniqueSimulator.dll 同等风险面，FreezeTest 兜底）。
    /// </summary>
    internal static unsafe class TriggerRsHost
    {
        // ── FFI 结构（与 TriggerRs/src/lib.rs #[repr(C)] 严格一致）────
        [StructLayout(LayoutKind.Sequential)]
        internal struct Anchor
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
        internal struct HitEventFfi
        {
            public double TriggerTime;
            public byte KeyCode;
            public byte ReleaseOnly;
            public byte IsHoldRelated;
            public byte ReleaseKeyCode;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WaitAdvice
        {
            public double SleepSec;
            public double DeltaSec;
        }

        // ── 动作码（与 lib.rs 常量一致）────────────────────────────
        internal const int ACTION_NONE = 0;
        internal const int ACTION_PRESS = 1;
        internal const int ACTION_RELEASE = 2;
        internal const int ACTION_HOLD_PRESS = 3;
        internal const int ACTION_ADVANCE = 4;
        internal const int ACTION_FINISHED = 5;
        internal const int ACTION_WAIT_AGAIN = 6;
        internal const int ACTION_SLEEP = 7;

        // ── 熔断参数 ──────────────────────────────────────────────
        /// <summary>连续异常达到该次数即熔断（回退 C# 路径，模组重启前不再尝试）。</summary>
        private const int ConsecutiveFailureLimit = 30;

        private static IntPtr _session;
        private static bool _available;
        private static bool _tripped;          // 已熔断
        private static int _consecutiveFailures;
        private static bool _probeDone;

        /// <summary>Rust 触发器当前可用（DLL 已加载且未熔断）。</summary>
        internal static bool Available => _available && !_tripped;

        /// <summary>诊断：累计坏输入次数（StepStats 转发）。</summary>
        internal static ulong BadInputs { get; private set; }
        internal static ulong TotalTicks { get; private set; }

        // ── P/Invoke ──────────────────────────────────────────────
        [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr StepCreate();

        [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
        private static extern void StepFree(IntPtr session);

        [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
        private static extern int StepTick(IntPtr session, ref Anchor anchor, int eventIndex, long qpcNow, ref WaitAdvice wait);

        [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
        private static extern int StepResync(IntPtr session);

        [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
        private static extern int StepStats(IntPtr session, out ulong ticks, out ulong badInputs);

        // ── 生命周期 ──────────────────────────────────────────────

        /// <summary>
        /// 尝试加载 Rust 触发器。失败（DLL 缺失/签名不符）返回 false 且记日志——
        /// 调用方退回 C# 原路径，不算错误。
        /// </summary>
        internal static bool TryInitialize()
        {
            _tripped = false;
            _consecutiveFailures = 0;
            _available = false;
            if (_session != IntPtr.Zero) { StepFree(_session); _session = IntPtr.Zero; }
            try
            {
                // 嵌入式加载：macro_trigger.dll 内嵌在 ADOFAIMacro.dll 资源里，
                // 首次使用时解压到 %LOCALAPPDATA%\ADOFAIMacro\native\ 再 LoadLibrary
                //（Windows 加载器语义：同名校验过模块后，后续 DllImport("macro_trigger")
                //  直接复用已加载模块，不再走文件系统 probing）。
                // 若嵌入资源缺失（旧版主 DLL），退回 Mods 目录探测（SetDllDirectory）。
                IntPtr h = EmbeddedNative.LoadEmbedded();
                if (h == IntPtr.Zero)
                {
                    string modPath = Main.Mod?.Path;
                    if (!string.IsNullOrEmpty(modPath) && System.IO.Directory.Exists(modPath))
                        SetDllDirectory(modPath);
                }

                _session = StepCreate();
                _available = _session != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Main.Log($"[TriggerRs] 加载失败（{ex.GetType().Name}: {ex.Message}），本次会话使用 C# 触发路径");
                _available = false;
            }
            return _available;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string lpPathName);

        internal static void Shutdown()
        {
            if (_session != IntPtr.Zero)
            {
                try { StepFree(_session); } catch { }
                _session = IntPtr.Zero;
            }
            _available = false;
            _tripped = false;
            _consecutiveFailures = 0;
        }

        /// <summary>C# 侧完成重同步后调用（Rust 吸收新版本号）。</summary>
        internal static void NotifyResync()
        {
            if (!Available) return;
            try { StepResync(_session); } catch { Trip("StepResync 异常"); }
        }

        // ── 核心判定 ──────────────────────────────────────────────

        /// <summary>
        /// 单步判定：与 WorkerLoop 热循环同语义。返回动作码，wait 带等待建议。
        /// 任何异常计入熔断计数并返回 ACTION_NONE（调用方走 C# 路径语义）。
        /// </summary>
        internal static int Step(
            ref Anchor anchor, int eventIndex, long qpcNow, out WaitAdvice wait)
        {
            wait = default;
            if (!Available) return ACTION_NONE;
            try
            {
                int act = StepTick(_session, ref anchor, eventIndex, qpcNow, ref wait);
                if (act == ACTION_NONE)
                {
                    // NONE 在正常流里只出现在版本吸收轮；连续 NONE 不算失败。
                    _consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveFailures = 0;
                }
                RefreshStats();
                return act;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                Main.Log($"[TriggerRs] StepTick 异常（第 {_consecutiveFailures} 次）: {ex.GetType().Name}: {ex.Message}");
                if (_consecutiveFailures >= ConsecutiveFailureLimit)
                    Trip("连续异常达阈值");
                return ACTION_NONE;
            }
        }

        /// <summary>坏输入（Rust 侧 bad_inputs 上升）计入熔断计数。</summary>
        internal static void ReportBadInput()
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= ConsecutiveFailureLimit)
                Trip("坏输入达阈值");
        }

        private static void Trip(string why)
        {
            if (_tripped) return;
            _tripped = true;
            Main.Log($"[TriggerRs] 已熔断（{why}），回退 C# 触发路径；重新启用模组前不再尝试");
            try
            {
                GameNotifier.Warning("Rust 触发器异常已熔断，回退内置路径（详见日志）", 5f);
            }
            catch { /* 通知栏不可用不阻塞 */ }
        }

        private static void RefreshStats()
        {
            if (_probeDone) return;
            try
            {
                StepStats(_session, out var ticks, out var bad);
                TotalTicks = ticks;
                BadInputs = bad;
                if (bad > 0) ReportBadInput();
                // 只在首个诊断点打一条探针（避免每事件刷日志）
                Main.Log($"[TriggerRs] 探针: ticks={ticks} badInputs={bad}");
                _probeDone = true;
            }
            catch { }
        }
    }
}
