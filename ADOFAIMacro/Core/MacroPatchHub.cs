using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ADOFAIMacro.Core
{
    // MacroPatchHub —— 补丁应用器（移植自 Spectre 的 SpectrePatchHub，按宏的体量裁剪）。
    //
    // 保留：候选链解析（MethodNames/ClassNames）、版本门控（MinVersion/MaxVersion +
    // MinSupportedBuild 隐式地板）、目标解析消歧（ParameterTypes 精确签名 / 补丁参数名 /
    // AllOverloads）、异常兜底（窗口限流 + 自动卸载）、启动探针与挂载失败汇总。
    // 裁剪：Feature 功能开关粒度（宏的补丁与宏功能强耦合，开关语义已内建）、
    // 运行时手动挂载、异步日志（宏的 Main.Log 已带总开关）。
    //
    // 引擎就是原生 Harmony：不引入需要改写 Harmony 内部的扩展类型。
    //
    // 性能设计：
    //   - 目标解析/反射只在启用模组时做一次，运行期零反射；
    //   - 单一 Harmony 实例（owner = modEntry.Info.Id），UnpatchAll 一次全下；
    //   - 异常兜底走补丁方法自身的 try/catch 包装（共享 finalizer 的等价物），
    //     无异常时零开销。
    internal static class MacroPatchHub
    {
        // —— 版本政策（宿主 Main.OnToggle 前声明）——————————————————
        // 支持的最低游戏 build：低于它 OnToggle 网关直接拒绝启用（fail-closed）。
        internal const int MinSupportedBuild = 141;
        // 已验证的游戏 build：核心补丁可 pin 到它，游戏更新后自动停用待适配。
        internal const int VerifiedBuild = 150;

        private sealed class Spec
        {
            public string Id;               // "MacroPatch:类.方法#序号"
            public MethodInfo PatchMethod;
            public MacroPatchAttribute Attr;
            public List<MethodBase> Targets; // 解析出的全部目标（AllOverloads 可多个）；null = 解析失败
            public string TargetName;        // 探针展示：命中候选的最终目标
            public string Error;             // 解析/应用失败原因；null = 正常
            public bool VersionSkipped;      // 版本门控跳过：不算失败，只进探针明细
            public bool Applied;
            public bool AutoDisabled;        // 异常达阈值自动卸载后置位：Refresh 不再重挂
            public bool TryingCatch;         // 应用时据此包装补丁体
        }

        private static readonly List<Spec> _specs = new();

        // 异常限流状态：key = 补丁@目标。同一异常窗口期内详报一次、重复只计数，
        // 窗口过后仍在犯则合并成一行摘要；异常类型/消息一变立即重新详报
        private sealed class GuardStat
        {
            public DateTime WindowStart; public int Suppressed; public string Sig;
        }
        private static readonly Dictionary<string, GuardStat> _guardStats = new();
        private const int GuardWindowSeconds = 30;

        // 挂载失败汇总（启用模组后弹一次游戏内通知）
        private static readonly List<string> _applyFailures = new();
        private static bool _failureSummaryShown;
        private static readonly object _lock = new();

        // —— 生命周期 ——————————————————————————————————————

        /// <summary>
        /// 扫描程序集里全部 [MacroPatch] 方法并解析目标（不挂载）。
        /// 可重复调用（重扫前先卸载）。在 OnToggle(true) 里调用，先于 ApplyAll。
        /// </summary>
        public static void Initialize(Assembly assembly)
        {
            lock (_lock)
            {
                _specs.Clear();
                _guardStats.Clear();
                _applyFailures.Clear();
                _failureSummaryShown = false;
                int index = 0;
                foreach (Type type in assembly.GetTypes())
                {
                    foreach (MethodInfo method in type.GetMethods(
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        foreach (MacroPatchAttribute attr in method.GetCustomAttributes<MacroPatchAttribute>(false))
                        {
                            var spec = new Spec
                            {
                                Id = "MacroPatch:" + type.Name + "." + method.Name + "#" + (index++),
                                PatchMethod = method,
                                Attr = attr,
                                TryingCatch = attr.TryingCatch,
                            };
                            attr.PatchMethod = method;
                            Resolve(spec);
                            _specs.Add(spec);
                        }
                    }
                }
            }
            LogProbe();
        }

        /// <summary>按解析结果挂载全部就绪补丁。Initialize 之后调用一次。</summary>
        public static void ApplyAll()
        {
            int applied = 0;
            lock (_lock)
            {
                foreach (Spec spec in _specs)
                {
                    if (spec.Error != null || spec.Applied) continue;
                    Apply(spec);
                    if (spec.Applied) applied++;
                }
            }
            Main.Log($"[MacroPatch] 应用 {applied}/{_specs.Count} 个补丁（其余为版本跳过或解析失败，明细见探针）");
        }

        /// <summary>卸载全部补丁并清空状态（OnToggle(false) / 重扫时调用）。</summary>
        public static void UnpatchAll(string harmonyId)
        {
            lock (_lock)
            {
                _specs.Clear();
                _guardStats.Clear();
            }
            // Harmony.UnpatchAll 由 Main 统一做（owner 相同），这里只清 Hub 状态。
        }

        /// <summary>
        /// 由 Main 在启用完成后调用：若有补丁未能挂载，弹一次游戏内通知。
        /// 无失败时什么都不做（有失败也只提示一次）。
        /// </summary>
        public static void ShowApplyFailureSummary()
        {
            List<string> snapshot;
            lock (_lock)
            {
                if (_failureSummaryShown || _applyFailures.Count == 0) return;
                _failureSummaryShown = true;
                snapshot = new List<string>(_applyFailures);
            }
            Main.LogStartup("[MacroPatch] 合计 " + snapshot.Count + " 个补丁未能挂载（对应功能不会生效）："
                + string.Join(" | ", snapshot));
            try
            {
                GameNotifier.Warning(
                    "ADOFAIMacro 有 " + snapshot.Count + " 个功能未能加载（游戏版本可能不兼容），详见日志", 8f);
            }
            catch { /* 通知栏不可用时不阻塞 */ }
        }

        // —— 解析 ————————————————————————————————————————————

        private static void Resolve(Spec spec)
        {
            MacroPatchAttribute a = spec.Attr;
            if (GameVersion.Release > 0)
            {
                // 宿主的最低支持版本作隐式地板，attribute 只在需要更高下限时才设 MinVersion
                int min = Math.Max(a.MinVersion, MinSupportedBuild);
                if (GameVersion.Release < min)
                {
                    spec.VersionSkipped = true;
                    spec.Error = "游戏 r" + GameVersion.Release + " 低于 MinVersion " + min;
                    return;
                }
                if (a.MaxVersion > 0 && GameVersion.Release > a.MaxVersion)
                {
                    spec.VersionSkipped = true;
                    spec.Error = "游戏 r" + GameVersion.Release + " 高于 MaxVersion " + a.MaxVersion + "（未验证）";
                    return;
                }
            }
            else if (a.MinVersion > 0 || a.MaxVersion > 0)
            {
                // 版本探测失败（Release == -1）时不能放行带版本分叉的补丁：
                // 分叉对（MaxVersion=149 / MinVersion=150）会同时注册，两条目标一存在一不存在，
                // 宁可整体不挂并显式告警（与 SpectrePatchHub 同策略）
                spec.VersionSkipped = true;
                spec.Error = "游戏版本探测失败（Release == -1），已跳过带版本约束的补丁";
                return;
            }
            Type type = a.ClassType;
            if (type == null && !string.IsNullOrEmpty(a.ClassName))
                type = AccessTools.TypeByName(a.ClassName);
            // 类型候选链：ClassName 未命中依次尝试 ClassNames（新名在前）
            if (type == null && a.ClassNames != null)
                foreach (string cn in a.ClassNames)
                {
                    if (string.IsNullOrEmpty(cn)) continue;
                    type = AccessTools.TypeByName(cn);
                    if (type != null) break;
                }
            if (type == null)
            {
                spec.Error = "目标类型未找到: " + (a.ClassType != null ? a.ClassType.FullName
                    : !string.IsNullOrEmpty(a.ClassName) ? a.ClassName
                    : string.Join(", ", a.ClassNames ?? Array.Empty<string>()));
                return;
            }

            var names = new List<string>();
            if (!string.IsNullOrEmpty(a.MethodName)) names.Add(a.MethodName);
            if (a.MethodNames != null)
                names.AddRange(a.MethodNames.Where(n => !string.IsNullOrEmpty(n) && !names.Contains(n)));
            if (names.Count == 0)
            {
                spec.Error = "未指定目标方法名";
                return;
            }

            foreach (string name in names)
            {
                if (a.AllOverloads)
                {
                    List<MethodBase> all = FindAllOverloads(type, name, a);
                    if (all != null && all.Count > 0)
                    {
                        spec.Targets = all;
                        spec.TargetName = type.Name + "." + name + (all.Count > 1 ? " ×" + all.Count : "");
                        return;
                    }
                    continue;
                }
                MethodBase target = FindMethod(type, name, a);
                if (target != null)
                {
                    spec.Targets = new List<MethodBase> { target };
                    spec.TargetName = type.Name + "." + name;
                    return;
                }
            }
            spec.Error = "目标未找到或重载无法消歧（候选: " + string.Join(", ", names)
                + "）；可用 ParameterTypes 精确限定参数签名";
        }

        // 目标解析：枚举全部同名重载（含基类）再消歧——ParameterTypes 精确签名优先，
        // 其次用补丁方法声明的普通参数名消歧，仍不唯一返回 null 进探针，
        // 绝不静默绑到任意重载上（要全挂见 FindAllOverloads）
        private static MethodBase FindMethod(Type type, string name, MacroPatchAttribute a)
        {
            try
            {
                Type[] args = a.ParameterTypes;
                if (name == ".ctor" || name == ".cctor")
                {
                    bool isStatic = name == ".cctor";
                    ConstructorInfo[] ctors = type.GetConstructors(
                        (isStatic ? BindingFlags.Static : BindingFlags.Instance)
                        | BindingFlags.Public | BindingFlags.NonPublic);
                    if (isStatic) return ctors.Length == 1 ? ctors[0] : null;
                    if (args != null) return ctors.FirstOrDefault(c => SignatureMatches(c, args));
                    return ctors.Length == 1 ? ctors[0] : null;
                }

                List<MethodInfo> candidates = CollectMethods(type, name);
                if (candidates.Count == 0) return null;
                if (candidates.Count == 1) return candidates[0];
                if (args != null)
                    return candidates.FirstOrDefault(m => SignatureMatches(m, args));
                return DisambiguateByPatchParams(candidates, a.PatchMethod);
            }
            catch
            {
                return null;
            }
        }

        // 同名方法候选收集（含基类）：override 与基类声明 GetBaseDefinition 相同——
        // 只保留最派生的实现；不同重载的 BaseDefinition 各不相同，不受影响
        private static List<MethodInfo> CollectMethods(Type type, string name)
        {
            var candidates = new List<MethodInfo>();
            for (Type t = type; t != null; t = t.BaseType)
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == name) candidates.Add(m);
                }
            }
            var deduped = new List<MethodInfo>();
            var seenBaseDefs = new HashSet<MethodInfo>();
            foreach (MethodInfo m in candidates)
                if (seenBaseDefs.Add(m.GetBaseDefinition()))
                    deduped.Add(m);
            return deduped;
        }

        // AllOverloads 解析：同名全部重载都返回；给了 ParameterTypes 时仍按签名过滤
        private static List<MethodBase> FindAllOverloads(Type type, string name, MacroPatchAttribute a)
        {
            try
            {
                if (name == ".ctor")
                    return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Cast<MethodBase>().ToList();
                if (name == ".cctor") return null;
                List<MethodInfo> candidates = CollectMethods(type, name);
                if (a.ParameterTypes != null)
                    candidates = candidates.Where(m => SignatureMatches(m, a.ParameterTypes)).ToList();
                return candidates.Cast<MethodBase>().ToList();
            }
            catch
            {
                return null;
            }
        }

        private static bool SignatureMatches(MethodBase method, Type[] args)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != args.Length) return false;
            for (int i = 0; i < args.Length; i++)
                if (parameters[i].ParameterType != args[i]) return false;
            return true;
        }

        // 重载消歧：补丁方法的普通参数（__ 开头的注入项除外）按名落在哪个重载上。
        // 候选必须包含全部普通参数名才幸存；多个幸存时选"多出来的目标参数最少"者，
        // 并列取声明序靠前者（确定性），全部淘汰则返回 null（进探针，不猜）
        private static MethodInfo DisambiguateByPatchParams(List<MethodInfo> candidates, MethodInfo patch)
        {
            if (patch == null) return null;
            ParameterInfo[] ordinary = patch.GetParameters()
                .Where(p => !p.Name.StartsWith("__", StringComparison.Ordinal))
                .ToArray();
            if (ordinary.Length == 0) return null;
            MethodInfo best = null;
            int bestExtra = int.MaxValue;
            foreach (MethodInfo candidate in candidates)
            {
                ParameterInfo[] target = candidate.GetParameters();
                int matched = 0;
                foreach (ParameterInfo p in ordinary)
                    if (target.Any(tp => tp.Name == p.Name)) matched++;
                if (matched < ordinary.Length) continue;
                int extra = target.Length - matched;
                if (extra < bestExtra)
                {
                    bestExtra = extra;
                    best = candidate;
                }
            }
            return best;
        }

        // —— 应用 ————————————————————————————————————————————

        private static void Apply(Spec spec)
        {
            try
            {
                Harmony harmony = Main.Harmony!;
                MethodInfo patchMethod = spec.TryingCatch
                    ? WrapWithGuard(spec) : spec.PatchMethod;
                HarmonyMethod patch = new(patchMethod);
                int done = 0;
                List<string> failures = null;
                foreach (MethodBase target in spec.Targets)
                {
                    try
                    {
                        switch (spec.Attr.PatchType)
                        {
                            case PatchType.Prefix:
                                harmony.Patch(target, patch, null, null, null);
                                break;
                            case PatchType.Postfix:
                                harmony.Patch(target, null, patch, null, null);
                                break;
                            case PatchType.Transpiler:
                                harmony.Patch(target, null, null, patch, null);
                                break;
                            case PatchType.Finalizer:
                                harmony.Patch(target, null, null, null, patch);
                                break;
                        }
                        spec.Applied = true;
                        done++;
                    }
                    catch (Exception ex)
                    {
                        (failures ??= new List<string>()).Add(TargetLabel(target) + ": " + ex.Message);
                    }
                }
                if (failures != null)
                {
                    spec.Error = "应用失败: " + string.Join("; ", failures)
                        + (done > 0 ? "（其余 " + done + " 个目标已挂）" : "");
                    Main.Log("[MacroPatch] " + spec.Id + " → " + spec.Error);
                    RecordApplyFailure(spec.Id, spec.Error);
                }
            }
            catch (Exception ex)
            {
                spec.Error = "应用失败: " + ex.GetType().Name + ": " + ex.Message;
                Main.Log("[MacroPatch] " + spec.Id + " → " + spec.Error);
                RecordApplyFailure(spec.Id, spec.Error);
            }
        }

        // —— 异常兜底（包装 + 限流 + 自动卸载）—————————————————

        // TryingCatch = true 时把补丁方法包一层 try/catch（闭包捕获 specId/目标名/阈值）。
        // 每个补丁生成一个独立包装（字典缓存防重复包装），无异常时只有一次 try 进出，
        // 与 Spectre 的共享 finalizer 等价且不需要 Harmony finalizer 语义。
        private static readonly Dictionary<MethodInfo, MethodInfo> _guardCache = new();

        // 工厂方法引用（GuardFactory.BuildGuardMethod 的 MethodInfo，反射缓存一次）
        private static readonly MethodInfo _factoryMethod =
            typeof(GuardFactory).GetMethod(nameof(GuardFactory.BuildGuardMethod),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static MethodInfo WrapWithGuard(Spec spec)
        {
            if (_guardCache.TryGetValue(spec.PatchMethod, out var cached)) return cached;
            // ★ 不把 guard DynamicMethod 直接给 HarmonyMethod（2.3.6 拒绝，见 GuardFactory 类注释）。
            // 正确形态：把「补丁工厂」BuildGuardMethod 交给 HarmonyMethod，并先用 BeginFactory
            // 登记本次上下文；Harmony 挂载时调 BuildGuardMethod(original) → 内部生成 guard
            // → 取其返回值当补丁方法。
            GuardFactory.BeginFactory(spec.PatchMethod, spec.Id, spec.Attr.DisableAfterExceptions,
                (id, ex, targetName) => RecordGuardException(spec, targetName, ex, id));
            _guardCache[spec.PatchMethod] = _factoryMethod;
            return _factoryMethod;
        }

        private static void RecordGuardException(Spec spec, string targetName, Exception ex, string specId)
        {
            try
            {
                string key = specId + "@" + targetName;
                string sig = ex.GetType().Name + ": " + ex.Message;
                DateTime nowUtc = DateTime.UtcNow;
                int occurrences;
                bool autoDisable = false;
                lock (_lock)
                {
                    if (!_guardStats.TryGetValue(key, out GuardStat stat) || stat.Sig != sig)
                    {
                        stat = new GuardStat { WindowStart = nowUtc, Suppressed = 0, Sig = sig };
                        _guardStats[key] = stat;
                        occurrences = 1;
                        Main.Log("[MacroPatch] 补丁 " + specId + " 在 " + targetName
                            + " 内抛出异常（已吞掉，功能可能退化，游戏版本不匹配？）: "
                            + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                    }
                    else if ((nowUtc - stat.WindowStart).TotalSeconds >= GuardWindowSeconds)
                    {
                        Main.Log("[MacroPatch] 补丁 " + specId + " 在 " + targetName
                            + " 内异常持续（" + GuardWindowSeconds + "s 内重复 " + stat.Suppressed
                            + " 次，已合并）: " + sig);
                        stat.WindowStart = nowUtc; stat.Suppressed = 0;
                        occurrences = 1;
                    }
                    else
                    {
                        stat.Suppressed++;
                        occurrences = stat.Suppressed + 1;
                    }
                    // 异常自动卸载：窗口内累计达到补丁声明的阈值，把这个补丁卸了（宁缺毋滥）
                    if (spec.Attr.DisableAfterExceptions > 0 && occurrences >= spec.Attr.DisableAfterExceptions
                        && !spec.AutoDisabled)
                    {
                        spec.AutoDisabled = true;
                        autoDisable = true;
                    }
                }
                if (autoDisable)
                {
                    try
                    {
                        if (spec.Targets != null && Main.Harmony != null)
                            foreach (MethodBase target in spec.Targets)
                                Main.Harmony.Unpatch(target, HarmonyPatchType.All, Main.Harmony.Id);
                        Main.Log("[MacroPatch] 补丁 " + specId + " 异常达阈值，已自动卸载；重新启用模组前不会自动恢复");
                    }
                    catch (Exception uex)
                    {
                        Main.Log("[MacroPatch] 自动卸载失败 " + specId + ": " + uex.Message);
                    }
                }
            }
            catch
            {
                // 兜底自身绝不允许再抛
            }
        }

        private static void RecordApplyFailure(string specId, string error)
        {
            lock (_lock)
            {
                if (_applyFailures.Exists(x => x.StartsWith(specId + "|", StringComparison.Ordinal)))
                    return;
                string shortError = error != null && error.Length > 120 ? error.Substring(0, 120) + "…" : error;
                _applyFailures.Add(specId + "|" + shortError);
            }
        }

        // —— 探针 ————————————————————————————————————————————

        private static void LogProbe()
        {
            int total, ok;
            List<Spec> failed;
            lock (_lock)
            {
                total = _specs.Count;
                failed = _specs.Where(s => s.Error != null).ToList();
                ok = total - failed.Count;
            }
            Main.LogStartup("[MacroPatch] 探针: 游戏 r" + GameVersion.Release
                + "，已验证 r" + VerifiedBuild + "，" + ok + "/" + total + " 就绪");
            if (GameVersion.Release > 0 && GameVersion.Release != VerifiedBuild)
                Main.LogStartup("[MacroPatch] 当前游戏版本未验证（r" + GameVersion.Release
                    + " ≠ r" + VerifiedBuild + "），如遇异常请反馈日志");
            foreach (Spec s in failed)
                Main.LogStartup("[MacroPatch]   ✗ " + s.Id + " → " + s.Error
                    + (s.VersionSkipped ? "（版本门控跳过）" : ""));
        }

        private static string TargetLabel(MethodBase m)
            => (m.DeclaringType?.Name ?? "?") + "." + m.Name;
    }
}
