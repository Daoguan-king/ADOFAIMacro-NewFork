using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ADOFAIMacro.Core
{
    /// <summary>
    /// 为补丁方法生成 try/catch 包装（异常兜底的静态工厂）。
    ///
    /// Harmony 补丁方法的签名因注入项（__instance/___field/__result/ref 参数）而异，
    /// 无法用单一泛型包装覆盖；这里用 Reflection.Emit 按原方法签名动态生成一个
    /// 同签名方法：体内 try { 调原方法 } catch { 上报 }。对 Prefix 返回 false
    /// （异常时放行原方法，不拦截）；void/其他返回类型异常时返回默认值。
    /// 每个补丁只生成一次（MacroPatchHub 缓存）。
    ///
    /// ⚠ Prefix 返回 bool 的约定：Harmony 语义下 prefix 返回 false 会跳过原方法。
    /// 异常时我们选择「放行原方法」——功能退化但游戏行为正确，优于卡死。
    ///
    /// ★★ Harmony 2.3.6 不接受把 DynamicMethod 直接当补丁方法喂给 harmony.Patch
    /// （实机 13/13 报「Cannot directly reference dynamic method ... Use a factory
    /// method instead that will return the dynamic method」）。Patch.GetMethod 的
    /// 约定是「补丁工厂」：一个【返回 MethodInfo 的静态方法，参数恰为 1 个
    /// MethodBase】——Harmony 在挂载时以原方法为实参调用它，取其返回值当补丁方法。
    /// 因此这里生成两个东西：①guard 本体 DynamicMethod ②薄工厂 BuildGuardMethod；
    /// HarmonyMethod 拿的是②。
    ///
    /// ★★ DynamicMethod 的参数默认没有名字（GetParameters()[i].Name == null），
    /// 而 Harmony 生成 wrapper 时 RealNames() 把补丁方法参数名收集进字典
    /// （ToDictionary 的 key），null 名直接 ArgumentNullException("key")——挂载失败。
    /// 所以每个参数必须 DefineParameter 显式命名（名称按位置沿用原参数/注入项名）。
    /// </summary>
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

            // 参数必须显式命名（见类注释 ★★），否则 Harmony RealNames() 抛 ArgumentNullException("key")
            for (int i = 0; i < parameters.Length; i++)
                dm.DefineParameter(i + 1, ParameterAttributes.None, parameters[i].Name);

            var il = dm.GetILGenerator();

            // 捕获变量：上报句柄（路由表索引）/ specId / targetName
            var repField = typeof(GuardClosure).GetField(nameof(GuardClosure.ReporterHandle));
            var idField = typeof(GuardClosure).GetField(nameof(GuardClosure.SpecId));
            var tgtField = typeof(GuardClosure).GetField(nameof(GuardClosure.TargetName));

            var closure = il.DeclareLocal(typeof(GuardClosure));
            il.Emit(OpCodes.Newobj, typeof(GuardClosure).GetConstructor(Type.EmptyTypes));
            il.Emit(OpCodes.Stloc, closure);

            // closure.Report = report; closure.SpecId = specId; closure.TargetName = "<patchMethod.Name>"
            // ★ 不重建委托——把传入的 report 委托【实例本身】塞进闭包。看似需要
            // Reflection.Emit 捕获运行期值，但 DynamicMethod 里做不到（IL 没有引用
            // 托管运行期对象的操作码）。解法：GuardFactory 自己持一个「上报路由表」，
            // guard 里只捕获一个 int 句柄，异常时调静态 ReportDispatch(handle, ...)。
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
            // ★ 公共出口 label 必须定义在异常块【之外】；try 与 catch 都用 Leave_S 出块。
            //   且 Leave 出块时【求值栈必须为空】——返回值先 Stloc 落本地，块外再 Ldloc+Ret。
            //   旧写法两处非法：①Br_S 跨出 try 块+try 内 Ret；②返回值留在栈上直接 Leave
            //   （InvalidProgramException，帧内 _t4/_t8/_tc/_td 单测实证）。
            var exit = il.DefineLabel();
            LocalBuilder retLocal = null;
            if (returnType != typeof(void))
                retLocal = il.DeclareLocal(returnType);
            il.BeginExceptionBlock();

            // 转发全部参数调用原方法
            for (int i = 0; i < paramTypes.Length; i++)
                il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Call, patchMethod);
            if (retLocal != null) il.Emit(OpCodes.Stloc, retLocal);
            il.Emit(OpCodes.Leave_S, exit);

            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Stloc, exLocal);

            // 上报：ReportDispatch(closure.ReporterHandle, closure.SpecId, ex, closure.TargetName)
            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldfld, repField);
            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldfld, idField);
            il.Emit(OpCodes.Ldloc, exLocal);
            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldfld, tgtField);
            il.Emit(OpCodes.Call, typeof(GuardFactory).GetMethod(nameof(ReportDispatch),
                BindingFlags.Static | BindingFlags.NonPublic));

            // 返回默认值：bool → false（放行原方法）；引用/值类型 → default；先落本地再 leave
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

        // —— Harmony 补丁工厂（Patch.GetMethod 约定的形态）—————————————
        // 约定（Harmony 2.3.6 Patch.GetMethod 的判定链，已从游戏内 0Harmony.dll IL 验证）：
        //   ① 静态；② 返回类型恰为 MethodInfo；③ 参数恰为 1 个 MethodBase。
        // Harmony 挂载时会以「原方法」为实参调用本方法，取其返回值（guard DynamicMethod）
        // 当真正的补丁方法。WrapWithGuard 先用 BeginFactory 把本次上下文放进暂存槽，
        // 工厂回调再取出生成 guard——Harmony 对每个补丁只调一次工厂。
        private static readonly Dictionary<MethodInfo, MethodInfo> _guardByPatch = new();

        /// <summary>挂载前登记本次工厂调用的上下文（谁/上报给谁/阈值）。</summary>
        public static void BeginFactory(MethodInfo patchMethod, string specId, int disableAfter, Reporter report)
        {
            _pendingPatchMethod = patchMethod;
            _pendingSpecId = specId;
            _pendingDisableAfter = disableAfter;
            _pendingReporter = report;
        }

        // 供 HarmonyMethod 引用的工厂方法本体（签名固定，见上方约定①②③）
        internal static MethodInfo BuildGuardMethod(MethodBase original)
        {
            var patch = _pendingPatchMethod;
            var guard = BuildGuard(patch, _pendingSpecId, _pendingDisableAfter, _pendingReporter);
            lock (_guardByPatch) { _guardByPatch[patch] = guard; }
            return guard;
        }

        // 工厂调用前的暂存槽（OnToggle 单线程挂载，简单字段够用）
        private static MethodInfo _pendingPatchMethod;
        private static string _pendingSpecId;
        private static int _pendingDisableAfter;
        private static Reporter _pendingReporter;

        // —— 上报路由表 ——————————————————————————————————————
        // guard 的 IL 里无法直接引用运行期的委托实例（IL 没有"装载托管对象"操作码），
        // 因此捕获一个 int 句柄，异常路径调静态 ReportDispatch 反查真正的 reporter。
        // OnToggle 卸载时 Cleanup() 清表防泄漏。
        private static readonly Dictionary<int, Reporter> _routes = new();
        private static int _nextHandle;

        private static void ReportDispatch(int handle, string specId, Exception ex, string targetName)
        {
            Reporter report;
            lock (_routes) { _routes.TryGetValue(handle, out report); }
            if (report == null) return;
            report(specId, ex, targetName);
        }

        /// <summary>清空上报路由表（模组停用/补丁卸载时调用，防句柄泄漏）。</summary>
        public static void Cleanup()
        {
            lock (_routes) { _routes.Clear(); _nextHandle = 0; }
            lock (_guardByPatch) { _guardByPatch.Clear(); }
        }

        private static void EmitClosureValue<T>(ILGenerator il, T value)
        {
            if (value == null) { il.Emit(OpCodes.Ldnull); return; }
            // 只支持 string 捕获（reporter 走上面的句柄路径）
            if (value is string s)
            {
                il.Emit(OpCodes.Ldstr, s);
                return;
            }
            throw new InvalidOperationException("GuardFactory: 不支持的闭包捕获类型 " + typeof(T));
        }

        private sealed class GuardClosure
        {
            public int ReporterHandle;
            public string SpecId;
            public string TargetName;
        }
    }
}
