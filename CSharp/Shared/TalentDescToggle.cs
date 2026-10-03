#if CLIENT
using System;
using System.IO;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.DescToggle
{
    /// <summary>
    /// 天赋描述详略开关。Lua 把「描述详略切换」绑定的模式写进 TouhouModHotkeyConfig.txt 的
    /// desc.detailed 行，这里每 0.3 秒看一次 mtime；简短模式给 TalentPrefab.Description 打后缀换
    /// talentdescriptionshort.&lt;id&gt;，缺失回原版。TalentPrefab 是 internal 只能按名反射，由 Mod.OnLoadCompleted 注册。
    /// </summary>
    public static class TalentDescToggle
    {
        /// true = 详细版（默认，跟原版一致）；false = 简短版
        public static bool Detailed = true;

        static string cfgPath;
        static DateTime cfgMtime;
        static double nextReload;
        static PropertyInfo identifierProp;

        /// 每帧入口：挂在 GameMain.Update 后缀，跟 BondGui 一个做法
        public static void PollConfig()
        {
            double now = Timing.TotalTime;
            if (now < nextReload) return;
            nextReload = now + 0.3;   // 0.3 秒够了：按住时要尽快看到变化，GetLastWriteTime 反正很便宜
            try
            {
                if (cfgPath == null)
                {
                    string baseDir = null;
                    try { baseDir = SaveUtil.DefaultSaveFolder; } catch { }
                    cfgPath = !string.IsNullOrEmpty(baseDir)
                        ? Path.Combine(baseDir, "TouhouModHotkeyConfig.txt")
                        : Path.Combine("Data", "Saves", "TouhouModHotkeyConfig.txt");
                }
                if (!File.Exists(cfgPath)) return;
                var mtime = File.GetLastWriteTime(cfgPath);
                if (mtime == cfgMtime) return;
                cfgMtime = mtime;
                foreach (var line in File.ReadAllLines(cfgPath))
                {
                    if (!line.StartsWith("desc.detailed=", StringComparison.Ordinal)) continue;
                    Detailed = line.Substring("desc.detailed=".Length).Trim() == "1";
                    break;
                }
            }
            catch { }
        }

        [HarmonyPatch]
        public static class TalentDescriptionPatch
        {
            static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Barotrauma.TalentPrefab");
                if (t == null)
                {
                    LuaCsLogger.LogMessage("[Touhou.DescToggle] TalentPrefab not found, description toggle disabled", Color.Red);
                    return null;
                }
                var m = AccessTools.PropertyGetter(t, "Description");
                if (m == null) LuaCsLogger.LogMessage("[Touhou.DescToggle] TalentPrefab.Description getter not found", Color.Red);
                return m;
            }

            static void Postfix(object __instance, ref LocalizedString __result)
            {
                if (Detailed) return;
                try
                {
                    if (identifierProp == null)
                        identifierProp = AccessTools.Property(__instance.GetType(), "Identifier");
                    string id = identifierProp?.GetValue(__instance) as string;
                    if (string.IsNullOrEmpty(id)) return;
                    __result = TextManager.Get($"talentdescriptionshort.{id}").Fallback(__result, true);
                }
                catch { }
            }
        }

        [HarmonyPatch]
        public static class UpdateDriverPatch
        {
            static MethodBase TargetMethod()
            {
                const BindingFlags flags =
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance;
                var m = typeof(GameMain).GetMethod("Update", flags);
                if (m == null) LuaCsLogger.LogMessage("[Touhou.DescToggle] GameMain.Update not found", Color.Red);
                return m;
            }

            static void Postfix() => PollConfig();
        }
    }
}
#endif
