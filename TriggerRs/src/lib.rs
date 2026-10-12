//! macro_trigger —— ADOFAIMacro 发键线程的 Rust 触发内核（阶段 1 脚手架）。
//!
//! 职责：接收 C# 侧传来的「时间锚点 + 事件表」快照，在 Rust 侧完成
//! 每次调用的触发判定（该发哪个事件、该等多久），把决策返回给 C#。
//! **发键本身留在 C#**（DirectPushKey 依赖 Unity 托管事件，不能过 ABI）。
//!
//! ABI 纪律：
//! - 所有跨界结构 `#[repr(C)]`，字段布局与 C# 侧 Blittable 结构一一对应；
//! - 导出函数全部 `extern "system"`（StdCall，与 .NET 默认平台调用一致）；
//! - 不分配/不持有跨调用生命周期——C# 传指针进来，Rust 只读；
//! - panic = "abort"（Cargo.toml），任何 panic 直接崩 DLL 而非 unwind 过 FFI；
//!   C# 侧以「连续 N 次返回 ACTION_NONE 且 error 计数上升」检测并回退。

use std::os::raw::{c_double, c_int, c_longlong, c_uchar};

// ── 动作码（C# 侧同名枚举） ────────────────────────────────────
pub const ACTION_NONE: c_int = 0; // 继续（已等待，下轮再问）
pub const ACTION_PRESS: c_int = 1; // 发按下（index 处事件）
pub const ACTION_RELEASE: c_int = 2; // 发松开
pub const ACTION_HOLD_PRESS: c_int = 3; // 发长按头按下
pub const ACTION_ADVANCE: c_int = 4; // 事件已消费，推进 index
pub const ACTION_FINISHED: c_int = 5; // 事件表已到尾
pub const ACTION_WAIT_AGAIN: c_int = 6; // 建议立即再轮询（自旋窗口）
pub const ACTION_SLEEP: c_int = 7; // 建议睡眠（远 future / 锚点过期）

/// C# 时间锚点快照（与 KeyWorker.WorkerLoop 内解构的 anchor 字段一一对应）。
#[repr(C)]
pub struct Anchor {
    /// 事件表指针（C# 固定住的 HitEvent 数组，元素布局见 HitEventFfi）
    pub events: *const HitEventFfi,
    /// 事件数
    pub event_count: c_int,
    /// 锚点版本（C# _resetVersion；不一致时 Rust 返回 ACTION_NONE 让 C# 自行重同步）
    pub reset_version: c_int,
    /// 锚点时刻的 QPC tick
    pub qpc_snapshot: c_longlong,
    /// QPC 频率（ticks per second）
    pub qpc_freq: c_longlong,
    /// 锚点时刻的歌曲位置（秒）
    pub song_pos_ref: c_double,
    /// 速率（pitch）
    pub rate: c_double,
    /// 触发偏移（秒，TimeOffset/1000）
    pub time_offset: c_double,
    /// 锚点过期阈值（秒，AnchorStaleSec）
    pub anchor_stale_sec: c_double,
}

/// 事件表元素（与 C# HitEvent 字段顺序/类型严格一致；C# 侧用布局顺序传）。
#[repr(C)]
#[derive(Clone, Copy)]
pub struct HitEventFfi {
    pub trigger_time: c_double,
    pub key_code: c_uchar,
    pub release_only: c_uchar, // 0/1
    pub is_hold_related: c_uchar, // 0/1
    pub release_key_code: c_uchar,
}

/// Rust 的等待决策（C# 据此执行 Thread.Sleep / SpinWait / HighResSleep）。
#[repr(C)]
pub struct WaitAdvice {
    /// 建议睡眠秒数（仅 ACTION_SLEEP 时有意义）
    pub sleep_sec: c_double,
    /// 诊断：本次判定与目标时刻的差（秒，正=未到）
    pub delta_sec: c_double,
}

/// Rust 侧会话句柄（阶段 1 只存锚点校验状态；后续阶段可扩为完整状态机）。
pub struct TriggerSession {
    last_reset_version: c_int,
    /// 统计：StepTick 调用次数
    ticks: u64,
    /// 统计：异常输入次数（events 为空指针、event_count<=0 等）
    bad_inputs: u64,
}

// ── 导出函数 ──────────────────────────────────────────────────

/// 创建会话。返回指针由 C# 持有，StepFree 释放。
#[no_mangle]
pub extern "system" fn StepCreate() -> *mut TriggerSession {
    Box::into_raw(Box::new(TriggerSession {
        last_reset_version: -1,
        ticks: 0,
        bad_inputs: 0,
    }))
}

/// 释放会话。
///
/// # Safety
/// `session` 必须是 StepCreate 返回且尚未释放的指针。
#[no_mangle]
pub unsafe extern "system" fn StepFree(session: *mut TriggerSession) {
    if !session.is_null() {
        drop(Box::from_raw(session));
    }
}

/// 核心判定：给定锚点 + 当前事件下标 + 当前 QPC tick，返回动作。
///
/// # Safety
/// `anchor.events` 必须指向至少 `event_count` 个有效 HitEventFfi；
/// `session` 必须是 StepCreate 返回的有效指针。
#[no_mangle]
pub unsafe extern "system" fn StepTick(
    session: *mut TriggerSession,
    anchor: *const Anchor,
    event_index: c_int,
    qpc_now: c_longlong,
    wait: *mut WaitAdvice,
) -> c_int {
    // 防御：任何坏输入都返回 NONE + 睡眠建议，让 C# 走原路径语义
    if session.is_null() || anchor.is_null() || wait.is_null() {
        return ACTION_NONE;
    }
    let s = &mut *session;
    let a = &*anchor;
    s.ticks += 1;

    // WaitAdvice 先填默认值
    (*wait).sleep_sec = 0.001;
    (*wait).delta_sec = 0.0;

    if a.events.is_null() || a.event_count <= 0 {
        s.bad_inputs += 1;
        return ACTION_FINISHED;
    }
    if a.qpc_freq <= 0 {
        s.bad_inputs += 1;
        (*wait).sleep_sec = 0.001;
        return ACTION_SLEEP;
    }
    if a.reset_version != s.last_reset_version {
        // 版本切换：C# 应先做重同步（StepResync），本轮不动
        s.last_reset_version = a.reset_version;
        return ACTION_NONE;
    }

    // 锚点过期检查（C# WorkerLoop :109 同款语义）
    let elapsed = (qpc_now - a.qpc_snapshot) as c_double / a.qpc_freq as c_double;
    if elapsed > a.anchor_stale_sec {
        (*wait).sleep_sec = 0.001;
        (*wait).delta_sec = elapsed;
        return ACTION_SLEEP;
    }

    // 事件表越界（含最后一拍**之后**）才 FINISHED。
    //
    // ★【2026-10-12 修 bug】旧写法 `event_index >= a.event_count - 1 → FINISHED`
    //   会把「最后一拍」本身吞掉：C# for 循环 `i < evCount` 允许 i == evCount-1
    //   进入并正常触发（C# 的 FINISHED 哨兵在外层 for **之前**），而 Rust 在
    //   i == evCount-1 时未看触发时刻就返回 FINISHED → C# case FINISHED 只松键
    //   + goto WriteBack → **最后一拍永不发**（用户实机：「TriggerRs 没有播放完
    //   关卡」）。离线 A/B 也没拦住——TriggerRsAB 的 C# 参照复刻了同一句错，
    //   两边一起错=0 不一致（已同步修正）。
    //   正确语义：event_index == event_count-1 与普通事件一样走触发判定；
    //   越界（>= event_count）才 FINISHED。
    if event_index >= a.event_count {
        return ACTION_FINISHED;
    }
    if event_index < 0 {
        s.bad_inputs += 1;
        return ACTION_FINISHED;
    }

    let ev = *a.events.offset(event_index as isize);

    // 触发判定（C# :112-120 同款外推）
    let audio_now = a.song_pos_ref + elapsed * a.rate;
    let trigger_at = ev.trigger_time + a.time_offset;
    let delta = trigger_at - audio_now;
    (*wait).delta_sec = delta;

    if delta > 0.0 {
        // 未到时刻：给等待建议
        if a.rate <= 0.0 {
            (*wait).sleep_sec = 0.001;
            return ACTION_SLEEP;
        }
        let wait_sec = delta / a.rate;
        if wait_sec > 0.01 {
            (*wait).sleep_sec = wait_sec; // C# 自己再夹到 1ms 粒度
            return ACTION_SLEEP;
        }
        if wait_sec > 0.0015 {
            // 1.5~10ms：建议睡到目标前 1.5ms（C# 用 HighResSleep，末段自旋）
            (*wait).sleep_sec = wait_sec - 0.0015;
            return ACTION_SLEEP;
        }
        // 极近：C# 自旋
        return ACTION_WAIT_AGAIN;
    }

    // 到时刻了：给出动作
    if ev.release_only != 0 {
        ACTION_RELEASE
    } else if ev.is_hold_related != 0 {
        ACTION_HOLD_PRESS
    } else {
        ACTION_PRESS
    }
}

/// C# 完成重同步后调用（告知当前事件下标已就绪）。
#[no_mangle]
pub extern "system" fn StepResync(session: *mut TriggerSession) -> c_int {
    if session.is_null() {
        return -1;
    }
    let s = unsafe { &mut *session };
    s.last_reset_version = -1; // 下一次 StepTick 会吸收新版本号
    0
}

/// 诊断统计（C# 侧健康检查用）。
#[no_mangle]
pub extern "system" fn StepStats(
    session: *mut TriggerSession,
    out_ticks: *mut u64,
    out_bad_inputs: *mut u64,
) -> c_int {
    if session.is_null() || out_ticks.is_null() || out_bad_inputs.is_null() {
        return -1;
    }
    let s = unsafe { &*session };
    unsafe {
        *out_ticks = s.ticks;
        *out_bad_inputs = s.bad_inputs;
    }
    0
}
