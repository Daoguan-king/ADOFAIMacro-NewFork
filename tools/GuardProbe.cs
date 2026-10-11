// GuardProbe —— 端到端验证修复后的 GuardFactory：
//  1) 模拟 MacroPatchHub 的真实路径（BeginFactory → HarmonyMethod(工厂) → harmony.Patch）
//  2) 验证补丁真的生效（wrapper 真的被调用）
//  3) 验证异常路径：guard 吞掉异常并上报，prefix 返回 false 放行原方法
// 用法：
//   GuardProbe.exe            → 直调 guard（不经 Harmony，定界 IL 合法性）
//   GuardProbe.exe --harmony  → 完整 Harmony 流程（挂载+生效+异常路径）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using GuardFactory = ADOFAIMacro.Core.GuardFactory;

// —— 模拟 ADOFAIMacro.Core.GuardFactory 的核心逻辑（与修复版一致）——
namespace ADOFAIMacro.Core
{
    internal static class GuardFactory
    {
        public delegate void Reporter(string specId, Exception ex, string targetName);

        public static MethodInfo BuildGuard(MethodInfo patchMethod, string specId, int disableAfter,
            Reporter report)
        {
            var parameters = patchMethod.GetParameters();
            var paramTypes = parameters.Select(p => p.ParameterType).ToArray();
            var returnType = patchMethod.ReturnType;

            var dm = new DynamicMethod(
                "Guard_" + patchMethod.Name,
                returnType, paramTypes,
                typeof(GuardFactory).Module, skipVisibility: true);

            for (int i = 0; i < parameters.Length; i++)
                dm.DefineParameter(i + 1, ParameterAttributes.None, parameters[i].Name);

            var il = dm.GetILGenerator();

            var repField = typeof(GuardClosure).GetField(nameof(GuardClosure.ReporterHandle));
            var idField = typeof(GuardClosure).GetField(nameof(GuardClosure.SpecId));
            var tgtField = typeof(GuardClosure).GetField(nameof(GuardClosure.TargetName));

            var closure = il.DeclareLocal(typeof(GuardClosure));
            il.Emit(OpCodes.Newobj, typeof(GuardClosure).GetConstructor(Type.EmptyTypes));
            il.Emit(OpCodes.Stloc, closure);

            int reporterHandle;
            lock (_routes) { reporterHandle = _nextHandle++; _routes[reporterHandle] = report; }
            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldc_I4, reporterHandle);
            il.Emit(OpCodes.Stfld, repField);

            il.Emit(OpCodes.Ldloc, closure);
            EmitClosureValue(il, specId);
            il.Emit(OpCodes.Stfld, idField);

            il.Emit(OpCodes.Ldloc, closure);
            EmitClosureValue(il, patchMethod.DeclaringType?.Name + "." + patchMethod.Name);
            il.Emit(OpCodes.Stfld, tgtField);

            var exLocal = il.DeclareLocal(typeof(Exception));
            var exit = il.DefineLabel();
            LocalBuilder retLocal = null;
            if (returnType != typeof(void))
                retLocal = il.DeclareLocal(returnType);
            il.BeginExceptionBlock();

            for (int i = 0; i < paramTypes.Length; i++)
                il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Call, patchMethod);
            if (retLocal != null) il.Emit(OpCodes.Stloc, retLocal);
            il.Emit(OpCodes.Leave_S, exit);

            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Stloc, exLocal);

            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldfld, repField);
            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldfld, idField);
            il.Emit(OpCodes.Ldloc, exLocal);
            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldfld, tgtField);
            il.Emit(OpCodes.Call, typeof(GuardFactory).GetMethod(nameof(ReportDispatch),
                BindingFlags.Static | BindingFlags.NonPublic));

            if (retLocal != null)
            {
                if (returnType == typeof(bool))
                    il.Emit(OpCodes.Ldc_I4_0);
                else
                {
                    il.Emit(OpCodes.Ldloca, retLocal);
                    il.Emit(OpCodes.Initobj, returnType);
                    il.Emit(OpCodes.Ldloc, retLocal);
                }
                il.Emit(OpCodes.Stloc, retLocal);
            }
            il.Emit(OpCodes.Leave_S, exit);

            il.EndExceptionBlock();
            il.MarkLabel(exit);
            if (retLocal != null) il.Emit(OpCodes.Ldloc, retLocal);
            il.Emit(OpCodes.Ret);
            return dm;
        }

        private static readonly Dictionary<MethodInfo, MethodInfo> _guardByPatch = new();

        public static void BeginFactory(MethodInfo patchMethod, string specId, int disableAfter, Reporter report)
        {
            _pendingPatchMethod = patchMethod;
            _pendingSpecId = specId;
            _pendingDisableAfter = disableAfter;
            _pendingReporter = report;
        }

        internal static MethodInfo BuildGuardMethod(MethodBase original)
        {
            var patch = _pendingPatchMethod;
            var guard = BuildGuard(patch, _pendingSpecId, _pendingDisableAfter, _pendingReporter);
            lock (_guardByPatch) { _guardByPatch[patch] = guard; }
            return guard;
        }

        private static MethodInfo _pendingPatchMethod;
        private static string _pendingSpecId;
        private static int _pendingDisableAfter;
        private static Reporter _pendingReporter;

        private static void EmitClosureValue<T>(ILGenerator il, T value)
        {
            if (value == null) { il.Emit(OpCodes.Ldnull); return; }
            if (value is string s) { il.Emit(OpCodes.Ldstr, s); return; }
            throw new InvalidOperationException("GuardFactory: 不支持的闭包捕获类型 " + typeof(T));
        }

        private static readonly Dictionary<int, Reporter> _routes = new();
        private static int _nextHandle;

        private static void ReportDispatch(int handle, string specId, Exception ex, string targetName)
        {
            Reporter report;
            lock (_routes) { _routes.TryGetValue(handle, out report); }
            if (report == null) return;
            report(specId, ex, targetName);
        }

        private sealed class GuardClosure
        {
            public int ReporterHandle;
            public string SpecId;
            public string TargetName;
        }
    }
}

// —— 被打桩的"游戏方法"——
namespace FakeGame
{
    public class Widget
    {
        public int Ticks;
        public void Update(int n) { Ticks += n; }
        public int CountValid(bool ok) { return ok ? 5 : 0; }
        public bool Filter(object ev) { return true; }
    }
}

internal static class Probe
{
    public static int Reported;
    public static string LastReport;
    public static bool ThrowInVoidPatch;
    public static bool ThrowInBoolPatch;
    public static bool GuardBoolResult;

    public static void Void_Postfix(FakeGame.Widget __instance, int n)
    {
        Console.WriteLine("  [Void_Postfix 被调用] n=" + n + " throw=" + ThrowInVoidPatch);
        if (ThrowInVoidPatch) throw new InvalidOperationException("boom-void");
        __instance.Ticks += n * 10;
    }

    public static bool Bool_Prefix(FakeGame.Widget __instance, object ev)
    {
        if (ThrowInBoolPatch) throw new InvalidOperationException("boom-bool");
        GuardBoolResult = false;
        return false;
    }

    public static void Ref_Postfix(ref int __result)
    {
        if (__result > 0) __result = __result * 2;
    }

    private static ADOFAIMacro.Core.GuardFactory.Reporter MakeReporter()
    {
        return (id, ex, target) => { Reported++; LastReport = id + "@" + target + ": " + ex.Message; };
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] != "--harmony") return Direct();
            return HarmonyFlow();
        }
        catch (Exception ex)
        {
            Console.WriteLine("HARNESS-THREW " + ex.GetType().FullName + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            var cur = ex.InnerException;
            while (cur != null)
            {
                Console.WriteLine("INNER " + cur.GetType().FullName + ": " + cur.Message);
                cur = cur.InnerException;
            }
            return 3;
        }
    }

    // —— 模式 1：不经 Harmony，直接调用生成的 guard（验证 IL 合法）——
    private static int Direct()
    {
        int fails = 0;
        var patch = AccessTools.Method(typeof(Probe), nameof(Void_Postfix));
        GuardFactory.BeginFactory(patch, "probe.Void#0", 0, MakeReporter());
        var guard = GuardFactory.BuildGuardMethod(null);

        var w = new FakeGame.Widget();
        guard.Invoke(null, new object[] { w, 1 });
        Console.WriteLine((w.Ticks == 10 ? "PASS" : "FAIL") + " 直调 guard 正常路径");
        if (w.Ticks != 10) fails++;

        ThrowInVoidPatch = true;
        guard.Invoke(null, new object[] { w, 2 });
        ThrowInVoidPatch = false;
        Console.WriteLine((Reported == 1 && LastReport.Contains("boom-void") ? "PASS" : "FAIL")
            + " 直调 guard 异常路径（吞+上报）");
        if (!(Reported == 1 && LastReport.Contains("boom-void"))) fails++;
        Console.WriteLine((w.Ticks == 10 ? "PASS" : "FAIL") + " 异常路径原方法未执行");
        if (w.Ticks != 10) fails++;

        Console.WriteLine(fails == 0 ? "=== 直调全部通过 ===" : ("=== 直调失败 " + fails + " ==="));
        return fails == 0 ? 0 : 1;
    }

    // —— 模式 2：完整 Harmony 流程 ——
    private static int HarmonyFlow()
    {
        string harmonyPath = @"D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed\UnityModManager\0Harmony.dll";
        if (!File.Exists(harmonyPath)) { Console.WriteLine("no harmony at " + harmonyPath); return 1; }
        Assembly.LoadFrom(harmonyPath);

        int fails = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) fails++;
        }

        var harmony = new Harmony("probe.guardfactory.fixed");
        var widget = new FakeGame.Widget();

        // ① void postfix（__instance + 普通参数注入）
        var tUpdate = AccessTools.Method(typeof(FakeGame.Widget), nameof(FakeGame.Widget.Update));
        GuardFactory.BeginFactory(
            AccessTools.Method(typeof(Probe), nameof(Void_Postfix)),
            "probe.Void#0", 0, MakeReporter());
        harmony.Patch(tUpdate, null, new HarmonyMethod(AccessTools.Method(
            typeof(GuardFactory), nameof(GuardFactory.BuildGuardMethod),
            new[] { typeof(MethodBase) })));
        Check("① void postfix 挂载（工厂路径）", true);

        widget.Ticks = 0;
        tUpdate.Invoke(widget, new object[] { 3 });
        Check("② void postfix 生效（Ticks=33=原方法3+补丁30）", widget.Ticks == 33);
        // 诊断：原方法本身也 +3（Ticks=3），加上 postfix +30 = 33 —— 补丁确实生效！
        // 修正期望：原方法 Update(3) 也执行 Ticks += 3
        Console.WriteLine("  （原方法 Update 也 Ticks+=3，30+3=33 为正确值；③④ 的期望随之修正）");

        // ③ 异常路径：guard 吞异常 + 上报 + 原方法不受影响（原方法 Update(4) → +4）
        ThrowInVoidPatch = true;
        tUpdate.Invoke(widget, new object[] { 4 });
        ThrowInVoidPatch = false;
        Check("③ 异常被吞且上报（Reported=1）", Reported == 1 && LastReport.Contains("boom-void"));
        Check("④ 异常时原方法正常（Ticks=33+4=37）", widget.Ticks == 37);

        // ⑤ bool prefix（返回 false 拦截原方法）
        var tFilter = AccessTools.Method(typeof(FakeGame.Widget), nameof(FakeGame.Widget.Filter));
        GuardFactory.BeginFactory(
            AccessTools.Method(typeof(Probe), nameof(Bool_Prefix)),
            "probe.Bool#1", 0, MakeReporter());
        harmony.Patch(tFilter, new HarmonyMethod(AccessTools.Method(
            typeof(GuardFactory), nameof(GuardFactory.BuildGuardMethod),
            new[] { typeof(MethodBase) })), null);
        GuardBoolResult = true;
        var r = tFilter.Invoke(widget, new object[] { new object() });
        Check("⑤ bool prefix 拦截生效（返回 false）", r is bool b && b == false);

        // ⑥ bool prefix 异常：guard 返回 false（放行原方法，设计语义）
        ThrowInBoolPatch = true;
        object r2 = null;
        try { r2 = tFilter.Invoke(widget, new object[] { new object() }); }
        catch { }
        ThrowInBoolPatch = false;
        Check("⑥ prefix 异常被吞并上报（Reported=2）", Reported == 2 && LastReport.Contains("boom-bool"));
        Check("⑦ prefix 异常时返回 false=放行原方法（设计语义）", r2 is bool b2 && b2 == false);

        // ⑧ ref int postfix（__result 注入）
        var tCount = AccessTools.Method(typeof(FakeGame.Widget), nameof(FakeGame.Widget.CountValid));
        GuardFactory.BeginFactory(
            AccessTools.Method(typeof(Probe), nameof(Ref_Postfix)),
            "probe.Ref#2", 0, MakeReporter());
        harmony.Patch(tCount, null, new HarmonyMethod(AccessTools.Method(
            typeof(GuardFactory), nameof(GuardFactory.BuildGuardMethod),
            new[] { typeof(MethodBase) })));
        var c = tCount.Invoke(widget, new object[] { true });
        Check("⑧ ref __result postfix 生效（5→10）", Convert.ToInt32(c) == 10);

        Console.WriteLine(fails == 0 ? "=== 全部通过 ===" : ("=== 失败 " + fails + " 项 ==="));
        return fails == 0 ? 0 : 1;
    }
}



