using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ADOFAIMacro.Core
{
    /// <summary>
    /// ADOFAI 游戏版本探针（移植自 Spectre 的 GameVersion.cs）。
    ///
    /// 启动读一次 releaseNumber：新版游戏放 GCNS，旧版放 Releases；
    /// 读取失败 = -1（版本门控放行，只降级为探针警告，不因读不到版本而 brick）。
    ///
    /// 政策值（MinSupportedBuild / VerifiedBuild）在 MacroPatchHub 里声明，
    /// 这里只负责探测。实测 r150 的 releaseNumber 在 Releases（GCNS 无此字段），
    /// 两个类都试是为了兼容将来游戏把字段搬回 GCNS。
    /// </summary>
    internal static class GameVersion
    {
        internal static readonly int Release = ReadRelease();

        private static int ReadRelease()
        {
            try
            {
                Assembly game = typeof(ADOBase).Assembly;
                foreach (string typeName in new[] { "GCNS", "Releases" })
                {
                    Type type = game.GetType(typeName) ?? AccessTools.TypeByName(typeName);
                    FieldInfo field = type == null ? null : AccessTools.Field(type, "releaseNumber");
                    if (field != null && field.IsStatic)
                        return Convert.ToInt32(field.GetValue(null));
                }
            }
            catch (Exception ex)
            {
                Main.LogStartup("[MacroPatch] 游戏版本读取失败: " + ex.Message);
            }
            return -1;
        }
    }
}
