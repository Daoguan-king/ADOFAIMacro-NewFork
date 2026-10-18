# ADOFAIMacro

[![License](https://img.shields.io/github/license/adofaiex/ADOFAIMacro?color=blue)](LICENSE.txt)
[![Downloads](https://img.shields.io/github/downloads/adofaiex/ADOFAIMacro/total)](https://github.com/adofaiex/ADOFAIMacro/releases)
[![C# 14.0](https://img.shields.io/badge/C%23-14.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET Framework 4.8.1](https://img.shields.io/badge/.NET%20Framework-4.8.1-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet-framework)

[English README](README.en.md)

**ADOFAIMacro** 是《A Dance of Fire and Ice》(ADOFAI) 的 UnityModManager 模组：解析谱面地板时间戳，由独立高优先级线程微秒级定时发送按键。从"直接判定触发"到"系统级按键模拟"再到"左右手手法模拟"，覆盖纯自动过谱到拟真手法的各种场景。

> ⚠️ **两条红线，先说清楚：**
> - **不要修改 `Info.json`** —— 模组带防篡改检测，改动后启用时会直接退出游戏。
> - **与 BaseMacro 不兼容** —— 检测到会直接退出游戏。

---

## 目录

- [功能一览](#功能一览)
- [快速上手](#快速上手)
- [触发模式与输入链路](#触发模式与输入链路)
- [设置速查](#设置速查)
- [手法模拟指南](#手法模拟指南)
  - [轨迹播放器与轨迹缓存](#轨迹播放器与轨迹缓存)
- [游戏内快捷键](#游戏内快捷键)
- [常见问题](#常见问题)
- [构建（开发者）](#构建开发者)
- [项目结构](#项目结构)
- [许可证与相关项目](#许可证与相关项目)

---

## 功能一览

- **自动触发**：谱面时间戳 → 高精度定时按键；双缓冲时间锚点同步主线程与工作线程，无锁零等待。
- **四种输入路径**：直接判定 / SendInput / SkyHook 底层注入（NtInject / NtSendInput）/ 虚拟异步键盘直喂（跳过系统注入，亚微秒抖动）。
- **手法模拟**：左右手交替、BPM 阈值自动细分时间片（单手多指连打）、可配置按键 / 顺序 / 按压时长（多音片重音"强弱拍"）、变速分段覆盖、每关独立配置；核心算法在 C++ 原生 DLL 中运行，离线求解 + 轨迹缓存。
- **轨迹播放器**：关卡加载期即求解并缓存轨迹（进图零计算）；支持导出 / 导入 `.adofaitrace` 分享，时间驱动 / 角度驱动两种回放方式。
- **时间微调**：±100 ms 偏移，游戏内方向键实时调节；判定误差自动校准（闭环，自动消除变速段偏移）。
- **按键过滤**：黑 / 白名单，同步（KeyCode）与异步（VK 码）独立过滤；放行游戏硬编码屏蔽的 4 个特殊键（PrintScreen / F12 / LAlt / Super）。
- **其他**：死亡后自动按键、游玩期 GC 停顿抑制、窗口失焦拦截、宏状态角标（HIT / VIRT / NT / SI）与虚拟按键屏显、中英双语 UI。

---

## 快速上手

**前置**：已安装 ADOFAI 和 [UnityModManager](https://www.nexusmods.com/site/mods/21)（≥ 0.27.0）。

1. 从 [Releases](https://github.com/adofaiex/ADOFAIMacro/releases) 下载压缩包，用 UMM 安装；或手动解压到游戏的 `Mods/ADOFAIMacro/` 目录。
   - 目录内应包含：`ADOFAIMacro.dll`、`InputSystem.dll`、`TechniqueSimulator.dll`、`Localization/`（zh-CN / en-US）。
2. 启动游戏，打开 UMM 模组窗口，启用 **ADOFAIMacro**。
3. 「宏」选项卡 → 勾选 **启用宏**。
4. 默认配置（按键模拟关闭 = 直接判定）即可自动过谱；判定整体偏早/偏晚时，到「延迟设置」调整 **延迟 (ms)**，或游戏内按 <kbd>←</kbd>/<kbd>→</kbd> 微调。

就这么多。更多玩法往下看。

---

## 触发模式与输入链路

「按键设置」中的三个开关组合出四条链路，按需选择：

| 你想要什么 | 配置 |
|---|---|
| 只要能过谱，链路最短 | 按键模拟 **关** |
| 需要真实系统按键（连打观感、按键音效、第三方工具读键盘） | 按键模拟 **开** + 使用高级输入 **关**（走 SendInput） |
| 高频谱面 / 多软件并行 / SendInput 被拦截 | 按键模拟 **开** + 使用高级输入 **开** + 输入模式 **自动** |
| 极致同步精度 | 上一行配置 + **虚拟异步键盘直喂** 开（默认已开） |

各链路说明：

1. **直接判定**（`SimulateKeyPress = false`）：工作线程计数 → 主线程调用游戏 `Hit()`。零系统副作用、延迟最可控，但游戏将其视为自动命中，不产生真实按键。
2. **SendInput**：标准 Win32 注入，兼容性最好。
3. **SkyHook 高级输入**：通过 `InputSystem.dll` 走更底层路径。输入模式四选一：
   - **自动**：优先选择可用的最底层方式（建议从这里开始）；
   - **NtInject**：最底层，直接注入原始输入流；
   - **NtSendInput ★**：内核边界注入；
   - **SendInput**：同标准 Win32。
4. **虚拟异步键盘直喂**：合成按键事件直接送进游戏自身的输入队列（与真实键盘钩子同一入口），跳过整个系统注入链路，时间戳本地生成、亚微秒抖动，且无窗口焦点依赖。不可用（如按键无映射、布局校验失败）时**自动回退**注入路径，无需手动干预。

---

## 设置速查

按 UMM 面板的选项卡组织。「内部名」对应配置文件中的字段。

### 宏

| 面板项 | 内部名 | 默认 | 说明 |
|---|---|---|---|
| 启用宏 | `Macro` | 关 | 宏总开关。 |
| 启用日志（关闭后不再写入 Player.log） | `EnableLogging` | 开 | 关闭后模组不写任何日志；手法表/诊断落盘文件不受影响。 |

### 按键设置

| 面板项 | 内部名 | 默认 | 说明 |
|---|---|---|---|
| 按键序列 (逗号分隔) | `MacroKeys` | `D,F,J,K` | 简单轮转模式的按键序列。 |
| 按键模拟 | `SimulateKeyPress` | 关 | 关 = 直接判定；开 = 系统按键模拟。 |
| 使用高级输入 | `SkyHookMode` | 关 | 开启后走 SkyHook 路径，否则 SendInput。 |
| 虚拟异步键盘直喂 | `UseVirtualAsyncInput` | 开 | 见上文链路 4；不可用自动回退。 |
| 虚拟按键同步系统输入 | `MirrorVirtualKeys` | 开 | 直喂成功后同步注入一份真实按键，让按键显示器等第三方工具看见虚拟按键；注入回声自动丢弃，不会双判定。 |
| Win API 输入模式 | `InputMode` | 自动 | 自动 / NtInject / NtSendInput ★ / SendInput。 |

### 延迟设置

| 面板项 | 内部名 | 默认 | 说明 |
|---|---|---|---|
| 延迟 (ms) | `TimeOffset` | 0 | 宏触发时间偏移，范围 −100 ~ 100。 |
| 调整步长 | `AdjustStep` | 1 | 游戏内方向键每次调整的偏移量，0.1 ~ 10。 |
| 允许左右键调整延迟(游戏中) | `EnableArrowTimeAdjust` | 开 | 游戏内 <kbd>←</kbd>/<kbd>→</kbd> 直接调偏移。 |
| 允许Ctrl+左右键调整步长偏移(游戏中) | `EnableKeyAdjust` | 开 | 游戏内 <kbd>Ctrl</kbd>+<kbd>←</kbd>/<kbd>→</kbd> 调步长。 |
| 启用高精度时间（提高同步精度） | `HighPrecisionTime` | 关 | 切换更精确的时钟源。 |
| [实验性] 启用高精度异步（需重新启用模组生效） | `HighPrecisionAsync` | 关 | 实验特性，不确定有问题再动。 |
| 判定误差自动校准 | `AutoCalibrateJudgement` | 开 | 闭环：用实际判定误差反馈自动补偿偏移（含变速段），每局自动收敛。 |
| 游玩期抑制GC停顿 | `SuppressGcPauses` | 关 | 高密度图消除 GC 造成的误差尖峰（GC 在加载期集中发生）。 |

### 按键过滤

| 面板项 | 内部名 | 默认 | 说明 |
|---|---|---|---|
| 启用按键过滤 | `EnableKeyFilter` | 关 | 过滤系统总开关。 |
| 过滤模式 | `FilterMode` | 黑名单 | 黑名单 = 阻止列表内按键；白名单 = 仅放行列表内按键。 |
| 按键列表 (逗号分隔) | `FilteredKeys` | `F1,F2,F3,F4` | 同步输入过滤。 |
| 异步按键列表 (逗号分隔) | `FilteredAsyncKeys` | 空 | 异步输入过滤（需高级输入 / SkyHook 模式）。 |

### 其他选项

| 面板项 | 内部名 | 默认 | 说明 |
|---|---|---|---|
| 死亡后自动按键 | `EnableDeathKey` | 关 | **需同时开启「按键模拟」与「使用高级输入」（SkyHook）**。 |
| 延迟秒数 | `DeathKeyDelay` | 5 | 死亡后延迟多少秒按键，0.1 ~ 30。 |
| 按键 | `DeathKeyInput` | `R` | 死亡后按的键，支持键名（SPACE、ENTER…）或虚拟键码（0x52）。 |
| 游戏允许切换到失败模式 | `ChangeNoFaillInPlay` | 关 | 游玩中解锁 NoFail 切换。 |
| 游戏中允许切换判定 | `ChangeJudementInPlay` | 关 | 游玩中解锁判定模式切换。 |
| 锁定关卡编辑器 | `LockLevelEditor` | 关 | 防误操作。 |
| 窗口未激活时阻止按键输入 | `BlockInputWhenUnfocused` | 开 | 失焦时不发送按键（工作线程不暂停，回到前台继续）。 |

### 按键书写格式（通用）

- 支持键名：`A`–`Z`、`0`–`9`、`F1`–`F12`、`SPACE`、`ENTER`、`ESC`、`TAB`、`SHIFT`、`CTRL`、`ALT`、方向键（`UP`/`DOWN`/`LEFT`/`RIGHT`）等；
- 支持十六进制虚拟键码：如 `0x41`；
- 多个按键用英文逗号分隔，如 `J,K,L`。

---

## 手法模拟指南

开启后（需同时开启 **按键模拟**），宏不再简单轮转按键序列，而是模拟真人双手打谱：时间按"片"划分，每片交给一只手，片间左右交替；当事件密度超过 **速度阈值 (BPM)** 时自动细分时间片，同一只手连续承担多个事件——即单手多指连打。

### 基本参数

| 面板项 | 默认 | 说明 |
|---|---|---|
| 启用手法模拟（左右手交替） | 关 | 总开关（需按键模拟；Release 版还需 `TechniqueSimulator.dll`）。 |
| 起始手 | 右手 | 第一个时间片交给哪只手。 |
| 全局·速度阈值 (BPM) | 500 | 超过此 BPM 自动细分时间片（范围 50 ~ 2000）。 |
| 左/右手按键 | `D,F` / `J,K` | 每只手可用的键，内置预设 DF/JK、DS/JK、ASDF/JKL。 |
| 左/右手顺序 | 空 | 见下方格式说明，留空 = 默认轮转。 |
| 左/右手时长 | `0.8,0.8` | 按压时长比例（0 ~ 1），决定按下到松开占时间片的比例；长按（hold）谱面自动处理。 |

**按键顺序格式**：用 `|` 分隔不同按键数的片，逗号分隔 1-based 键序号。例如 `1,2 | 1,2 | 1,2,1`：单键片按第 1、2 键轮转，三键片按 1→2→1。留空 = 默认顺序。

**"强弱拍"机制**：片内顺序表按**片内音数选行**——含 N 个音的片使用顺序表的第 N 行（行不够时取最后一行），行内每个键再各绑一档按压时长。于是连续快速段会自然出现"强→弱"的重音循环（例如 3 音片 `50→1.2 / 51→1 / 82→0.8`）。这就是顺序表要多写几行的原因：只写第 1 行的话，多音片就没有重音层次。

**片界守卫（伪双押修复）**：跨手片界上间隔小于一帧（~16.7 ms）的相邻音会被并入前一片（不超过该手键数、不掏空下一片、变速点不合并），消除"三连音被打成双手同步"的伪双押；同时保住下一片的多音性，强弱拍样式不被削。片长本身完全由逐地板速度决定（旧版"变速容差"已移除），片界对齐时间网格，同一节奏型的起手手性稳定。

### 配置（Profiles）

可保存多套完整手法参数（按键、顺序、时长、起始手、分段），面板上一键新建 / 删除 / 切换。

### 变速分段

在某个 floor 区间内覆盖全局设置：每段可独立指定 **BPM 阈值** 与 **左右手按键 / 顺序 / 时长**（留空字段继承全局）。段边界处自动重置手序、释放跨段长按键。

### 关卡特定配置

每张图可以保存独立配置：

- 文件与关卡同目录，命名 `关卡名.adofaimacro.json`；
- 进关自动加载（「自动从关卡目录加载」开关，默认开）；
- 面板底部可手动 加载 / 保存到关卡目录 / 删除，并显示当前关卡的配置状态。

适合给高 BPM 段落配更小的按键组合、为特定手癖设计键序、按图精调参数。

### 轨迹播放器与轨迹缓存

手法面板底部是 **轨迹播放器**。开启手法模拟后，整张谱的手法在**关卡加载完成时就会离线求解并缓存**，按开始前已就绪；下次进同一张谱直接读缓存，零计算。求解指纹包含谱面 + 完整手法配置 + 求解器 DLL 的内容哈希——改任何一项都会自动重新求解，不会误用旧缓存。

- **驱动方式**：时间驱动（默认，走原有时间锚点链路）/ 角度驱动（按行星在当前砖上已扫过的角度触发，不受时钟抖动影响；触发被帧率量化，最坏晚一帧，可用"角度提前量 (ms)"补偿，范围 −200 ~ 400）。
- **离线计算**：随时可手动重新求解当前关卡。
- **导出 / 导入轨迹**：导出当前谱的 `.adofaitrace` 文件分享给别人；导入时校验必须是同一张谱 + 同一套手法配置（指纹校验），成功后重进关卡生效。
- **清除轨迹缓存**：缓存位于游戏 `persistentDataPath/ADOFAIMacro/trace/`，可一键清空。

### 注意

- 核心算法在原生 `TechniqueSimulator.dll` 中运行，请确保它位于 `Mods/ADOFAIMacro/` 目录。**Release 版本没有 DLL 就没有手法模拟输出**（调试版可回退 C# 实现）。

---

## 游戏内快捷键

| 按键 | 作用 | 生效条件 |
|---|---|---|
| <kbd>←</kbd> / <kbd>→</kbd> | 按当前步长增减延迟 | 允许左右键调整延迟 开 |
| <kbd>Ctrl</kbd> + <kbd>←</kbd> / <kbd>→</kbd> | 步长 ±0.1 | 允许Ctrl+左右键调整步长 开 |

建议先在短图测试，确认稳定后再上长图 / 高密谱。

---

## 常见问题

**Q1：宏开了但没反应**
按顺序检查：UMM 中模组已启用 → 启用宏已勾选 → 按键序列格式正确（英文逗号分隔）→ 若开了"窗口未激活时阻止按键输入"，切出游戏时按键会被拦截（回到前台即恢复）。

**Q2：时准时不准、偶尔漏键**
先用方向键微调延迟（每次 1 ms）；高频谱面开高级输入 + 输入模式自动；高密度图开"游玩期抑制GC停顿"；仍冲突就开按键过滤隔离干扰源。

**Q3：变速谱面偏移大**
确认"判定误差自动校准"开启（默认开）；用分段给变速段单独设 BPM 阈值。片长由逐地板速度自动决定，无需（也没有）变速容差可调。

**Q4：死亡后自动按键不生效**
需同时开启「按键模拟」与「使用高级输入」（SkyHook）→ 确认已开启；确认死亡按键已启用、键名 / 键码有效；必要时增大延迟秒数。

**Q5：按键显示器看不到宏按键**
虚拟异步键盘直喂不经过系统输入流，第三方工具读不到 → 开启"虚拟按键同步系统输入"（默认开）。游戏画面左上角的绿色角标与虚拟按键屏显由模组自己渲染，不受此影响。

**Q6：按键过滤"没有作用"**
确认过滤已启用；确认模式（黑 / 白名单）与预期一致；高级输入场景下别忘了设置**异步**按键列表。用 PrintScreen / F12 / LAlt / Super（Win 徽标键）当宏键时无需额外配置——模组已放行游戏硬编码屏蔽的这四个键。

**Q7：启用模组后游戏直接退出**
`Info.json` 被修改过（防篡改检测），还原它；或装了 BaseMacro（互斥），卸载之。

**Q8：手法模拟没输出 / 提示 DLL 不可用**
把 `TechniqueSimulator.dll` 放到 `Mods/ADOFAIMacro/` 目录；Release 版缺 DLL 时手法模拟无事件输出。

**Q9：刚进游戏判定飘**
自动校准判定（面板「自动校准判定」）会在游玩中持续收敛，一般无需手动干预；若仍有固定偏移，可在面板用「延迟 (ms)」手动补偿。首次游玩误差较大属正常（校准数据从本局实测中采集，不跨运行保留）。

提 issue 时建议附上：游戏版本、模组版本、关键配置截图、是否使用 SkyHook 及当前输入模式、复现步骤与日志。

---

## 构建（开发者）

- 环境：Visual Studio 2022 或更高（含 MSBuild），.NET Framework 4.8.1，C# preview。
- C# 侧：`ADOFAIMacro/ADOFAIMacro-Dev.csproj` 已是 SDK 风格项目（NuGet 走 `PackageReference`，自动还原）；游戏程序集通过 `$(GameManaged)` 属性引用本机 ADOFAI 安装目录——需要游戏自带的 `Assembly-CSharp.dll`、`UnityEngine*.dll`、`SkyHook.Unity.dll`、`UnityModManager.dll`、`Newtonsoft.Json.dll`。MSBuild 编译 Release 即可。
- 原生侧：仓库根目录 `cmd /c build_cpp.bat` 一键构建两个 C++ 工程（`TechniqueSimulator` / `InputSystem`，各 Debug + Release）。**改动原生代码后必须全量重建（`/t:Rebuild`），增量构建可能吃旧产物。**
- 部署产物到 `Mods/ADOFAIMacro/`：`ADOFAIMacro.dll` + `Localization/` + `InputSystem.dll` + `TechniqueSimulator.dll`。

---

## 项目结构

```text
ADOFAIMacro/                    # C# 模组（命名空间与目录一一对齐）
├─ Core/
│  ├─ Main.cs                   # 入口：模组生命周期、DLL 加载、防篡改检测
│  ├─ Patches.cs                # 全部 Harmony 补丁（输入链路、判定误差回灌、特殊键放行等）
│  └─ GameNotifier.cs           # 借用游戏原生通知栏的游戏内消息
├─ Settings/Settings.cs         # 设置定义 + UMM 面板 UI（各选项卡）
├─ UI/
│  ├─ UIUtils.cs                # 面板绘制辅助
│  └─ ShowText.cs               # 宏状态角标（HIT/VIRT/NT/SI）+ 虚拟按键屏显
├─ Input/
│  ├─ InputSystem.cs            # InputSystem.dll P/Invoke 封装
│  ├─ VirtualAsyncInput.cs      # 虚拟异步键盘：合成事件直喂游戏输入队列
│  ├─ AsyncInputManager.cs      # 注入路径管理（SkyHook / SendInput 分发）
│  ├─ SkyHookSystem.cs          # SkyHook 结构体定义
│  └─ KeyMap.cs                 # 按键名 → 虚拟键码映射
├─ Technique/
│  ├─ MacroEngine.cs            # 手法主流程（partial：判定/校准/事件生成）
│  ├─ NativeTechnique.cs        # 原生 DLL 调用 + 逐地板输入压缩
│  ├─ PieceBuilder.cs           # C# 回退实现（仅调试版）
│  ├─ EventBuilder.cs           # 按键事件装配
│  ├─ ConfigParser.cs           # 顺序/时长/按键字符串解析
│  ├─ KeyWorker.cs              # 高优先级发键线程（时间锚点）
│  ├─ Clock.cs / Lifecycle.cs   # 时钟与生命周期
│  ├─ TechniqueSimulator.cs     # 原生 ABI 封装（结构体布局与 C++ 侧严格一致）
│  ├─ TraceFile.cs / TracePlayer.cs  # 轨迹缓存（.adotr）与轨迹播放器（.adofaitrace 导入导出）
│  └─ LevelTechniqueManager.cs  # 关卡特定手法配置（*.adofaimacro.json）
├─ Calibration/Calibration.cs   # 判定误差闭环自动校准
├─ Timing/
│  ├─ PreciseNow.cs             # 统一高精度时钟（与游戏判定同域）
│  └─ DSPTimeSimulater.cs       # 音频 DSP 时间模拟
├─ Localization/
│  ├─ LocalizationManager.cs    # JSON 本地化（Newtonsoft.Json，含硬编码兜底）
│  └─ zh-CN.json / en-US.json
└─ Platform/
   ├─ Windows.cs / Linux.cs     # 平台高精度计时
   └─ BaseSelect.cs             # 平台选择
TechniqueSimulator/             # C++ 手法算法（贪心在线分片 + 束搜索离线求解）
InputSystem/                    # C++ 底层输入注入（NtInject / NtSendInput）
build_cpp.bat                   # 一键构建两个原生工程
tools/                          # 离线评测 / 回归测试工装
```

---

## 许可证与相关项目

- 项目许可证：[LICENSE.txt](LICENSE.txt)
- 异步输入优化许可：[AsyncInputOptimize-LICENSE.txt](AsyncInputOptimize-LICENSE.txt)（GPL-3.0）
- [InputSystem](https://github.com/2228293026/InputSystem) —— 底层输入注入库（`InputSystem.dll`，运行时加载）
