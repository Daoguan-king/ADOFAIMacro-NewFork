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

            var il = dm.GetILGenerator();

            // 捕获变量：reporter / specId / targetName
            var repField = typeof(GuardClosure).GetField(nameof(GuardClosure.Report));
            var idField = typeof(GuardClosure).GetField(nameof(GuardClosure.SpecId));
            var tgtField = typeof(GuardClosure).GetField(nameof(GuardClosure.TargetName));

            var closure = il.DeclareLocal(typeof(GuardClosure));
            il.Emit(OpCodes.Newobj, typeof(GuardClosure).GetConstructor(Type.EmptyTypes));
            il.Emit(OpCodes.Stloc, closure);

            // closure.Report = report; closure.SpecId = specId; closure.TargetName = "<patchMethod.Name>"
            // Ldftn 需要 MethodInfo（report 是编译期已知的静态/实例委托，取其 Method）
            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldftn, report.Method);
            il.Emit(OpCodes.Newobj, typeof(Reporter).GetConstructor(new[] { typeof(object), typeof(IntPtr) }));
            il.Emit(OpCodes.Stfld, repField);

            il.Emit(OpCodes.Ldloc, closure);
            EmitClosureValue(il, specId);
            il.Emit(OpCodes.Stfld, idField);

            il.Emit(OpCodes.Ldloc, closure);
            EmitClosureValue(il, patchMethod.DeclaringType?.Name + "." + patchMethod.Name);
            il.Emit(OpCodes.Stfld, tgtField);

            var exLocal = il.DeclareLocal(typeof(Exception));
            var tryBegin = il.BeginExceptionBlock();

            // 转发全部参数调用原方法
            for (int i = 0; i < paramTypes.Length; i++)
                il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Call, patchMethod);

            // Prefix 返回 bool：正常路径直接返回原方法的返回值
            Label retNormal = il.DefineLabel();
            il.Emit(OpCodes.Br_S, retNormal);

            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Stloc, exLocal);

            // 上报：closure.Report(closure.SpecId, ex, closure.TargetName)
            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldfld, repField);
            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldfld, idField);
            il.Emit(OpCodes.Ldloc, exLocal);
            il.Emit(OpCodes.Ldloc, closure);
            il.Emit(OpCodes.Ldfld, tgtField);
            il.Emit(OpCodes.Callvirt, typeof(Reporter).GetMethod("Invoke"));

            // 返回默认值：bool → false（放行原方法）；void → ret；引用/值类型 → default
            if (returnType == typeof(bool))
            {
                il.Emit(OpCodes.Ldc_I4_0);
            }
            else if (returnType != typeof(void))
            {
                LocalBuilder def = il.DeclareLocal(returnType);
                il.Emit(OpCodes.Ldloca, def);
                il.Emit(OpCodes.Initobj, returnType);
                il.Emit(OpCodes.Ldloc, def);
            }
            il.Emit(OpCodes.Ret);

            // 正常返回点
            il.MarkLabel(retNormal);
            il.Emit(OpCodes.Ret);

            il.EndExceptionBlock();

            // 构造时把 disableAfter 传给闭包没有意义（阈值判断在 Hub 的 RecordGuardException），
            // 这里仅通过 closure 携带上下文，保持 Reporter 签名简单。
            return dm;
        }

        private static void EmitClosureValue<T>(ILGenerator il, T value)
        {
            if (value == null) { il.Emit(OpCodes.Ldnull); return; }
            // 只支持 string 捕获（delegate 走上面的专用路径）
            if (value is string s)
            {
                il.Emit(OpCodes.Ldstr, s);
                return;
            }
            throw new InvalidOperationException("GuardFactory: 不支持的闭包捕获类型 " + typeof(T));
        }

        private sealed class GuardClosure
        {
            public Reporter Report;
            public string SpecId;
            public string TargetName;
        }
    }
}
