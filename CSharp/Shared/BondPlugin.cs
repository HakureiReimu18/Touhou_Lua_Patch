using System;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.Bond
{
    // 绝对的强者，由此而生的孤独，教会你爱的人将会是...
    // 插件入口。只 patch 本命名空间的类，免得跟主插件（Touhou.Affixes）打重复补丁
    public sealed class BondPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        static string packageDir;
        // 模组目录。优先蹭主插件已解析好的路径，插件初始化顺序不保证谁先来
        public static string PackageDir
        {
            get
            {
                if (packageDir != null) return packageDir;
                packageDir = Touhou.Affixes.Mod.Package?.Dir;
                if (string.IsNullOrEmpty(packageDir) &&
                    LuaCsSetup.Instance.PluginPackageManager.TryGetPackageForPlugin<BondPlugin>(out var pkg))
                    packageDir = pkg.Dir;
                return packageDir ?? ".";
            }
        }

        public void Initialize()
        {
            harmony = new Harmony("touhou.bond");
        }

        public void OnLoadCompleted()
        {
            BondConfig.Load();
            if (oneTimeInitDone)
            {
                BondLog.Debug("OnLoadCompleted: 内容重载，补丁/命令已注册，跳过");
                return;
            }
            oneTimeInitDone = true;

            // reloadlua 后旧补丁会残留堆叠，先清掉自己名下的再打
            //（这个 Harmony 版本里 UnpatchAll(string) 直接标 error 过时，只能 UnpatchSelf）
            harmony.UnpatchSelf();

            harmony.PatchAll(typeof(BondMeterPatch));
            harmony.PatchAll(typeof(BondTickerPatch));
            harmony.PatchAll(typeof(WearDurabilityPatch));
#if CLIENT
            harmony.PatchAll(typeof(BondGui.BondGuiFramePatch));
            harmony.PatchAll(typeof(BondSettingsBridge.BondSettingsPatch));
            BondSettingsBridge.CleanStaleFlags(); // 清上个会话残留的开窗标记，只删不开
#endif

            BondMatch.RegisterCommands();
            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.Bond.RoundStart", OnRoundStart);
            LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.Bond.RoundEnd", OnRoundEnd);
            BondNet.Register();

            BondLog.Log("绑定护符插件已加载");
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.Bond.RoundStart");
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.Bond.RoundEnd");
            foreach (var cmd in BondMatch.CommandNames)
                LuaCsSetup.Instance.Game.RemoveCommand(cmd);
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
            BondState.ClearAll();
            BondLog.Log("绑定护符插件已卸载");
        }

        object OnRoundStart(object[] args)
        {
            // 每轮重置配额和配对历史，服务端顺便从桥接文件恢复配对
            BondState.SwitchUsedThisRound.Clear();
            BondState.PairHistoryThisRound.Clear();
            BondState.LastForcedPartner.Clear();
            if (GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer)
            {
                BondMatch.LoadPairs();
                // 延迟 3 秒补推一次全量配对状态，刷新客户端的绑定显示（等角色/物品就位）
                BondNet.PendingStatePushAt = Timing.TotalTime + 3.0;
            }
            return null;
        }

        object OnRoundEnd(object[] args)
        {
            // 只由权威侧写盘，避免双上下文写同一文件
            if (GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer)
                BondMatch.SavePairs();
            BondState.ClearRuntime();
            return null;
        }
    }

    // 运行时状态。服务端权威，客户端只是网络同步下来的镜像
    public static class BondState
    {
        // id → 配对条目；同一条目占两个键，A/B 各一个
        public static readonly System.Collections.Generic.Dictionary<string, PairEntry> Pairs = new();

        // 这轮已经用掉变更配额的 id
        public static readonly System.Collections.Generic.HashSet<string> SwitchUsedThisRound = new();

        // 这轮配过对的 id；解绑再绑别人也算一次变更
        public static readonly System.Collections.Generic.HashSet<string> PairHistoryThisRound = new();

        // 被动断链前的原对象，恢复旧配对不耗配额
        public static readonly System.Collections.Generic.Dictionary<string, string> LastForcedPartner = new();

        // 穿戴者缓存，每个结算 tick 重建
        public static readonly System.Collections.Generic.Dictionary<Character, Item> Wearers = new();

        public static readonly System.Collections.Generic.Dictionary<Character, DamageMeter> Meters = new();

        public static void ClearRuntime()
        {
            Wearers.Clear();
            Meters.Clear();
        }

        public static void ClearAll()
        {
            Pairs.Clear();
            SwitchUsedThisRound.Clear();
            ClearRuntime();
        }
    }

    public sealed class PairEntry
    {
        public string IdA, IdB;
        public string NameA, NameB;

        public bool Has(string id) => IdA == id || IdB == id;

        public string OtherId(string id) => IdA == id ? IdB : IdA;

        public string OtherName(string id) => IdA == id ? NameB : NameA;

        // 遍历去重用：Pairs 里同一 entry 有两个键
        public bool IsPrimary => string.CompareOrdinal(IdA, IdB) < 0;
    }

    public sealed class DamageMeter
    {
        // 窗口内按（命中肢体, affliction prefab）累计的入账量
        public readonly System.Collections.Generic.Dictionary<(LimbType Limb, AfflictionPrefab Prefab), float> Entries = new();
        public float Total { get; private set; }
        // 诊断用：一发被计量多次就说明补丁叠了
        public int Calls;
        public bool Any => Total > 0.0001f;

        public void Add(LimbType limb, AfflictionPrefab prefab, float amount)
        {
            Entries.TryGetValue((limb, prefab), out float v);
            Entries[(limb, prefab)] = v + amount;
            Total += amount;
        }

        public void Clear()
        {
            Entries.Clear();
            Total = 0f;
            Calls = 0;
        }
    }

    // 配置在 Config/bond_config.xml，bondcfg 指令可热改并写盘
    public static class BondConfig
    {
        public static double SettleInterval = 1.0;     // 结算周期（秒），1s 实测没压力
        public static float ResistanceScale = 0.5f;    // 转移伤害时目标抗性的生效系数：0 无视，1 全额
        public static float MaxLinkDistance = 0f;      // 链接距离上限，0 不限；防"躺家替伤"设 60 左右
        public static float PowerDRef = 30f;           // 蓄能核的伤害归一化参考量
        public static float PowerN = 0.5f;             // 蓄能核幂指数，n<1 大伤分摊变缓（反爆发）
        public static float MinShare = 0.1f, MaxShare = 0.9f; // 承担份额全局上下限
        public static float CharmGraceTime = 15f;      // 没戴护符连续超过这么多秒才判强制断链，防加载期误断
        public static float CondLossMult = 1.0f;       // 受击耐久损耗倍率，0 = 关机制
        public static bool Debug = false;

        // 出厂默认值（设置窗口"重置为默认值"的唯一事实来源）
        public const string DefaultSettleIntervalText = "1";
        public const string DefaultResistanceScaleText = "0.5";
        public const string DefaultMaxLinkDistanceText = "0";
        public const string DefaultCountedTypes = "damage,burn,bleeding,debuff,poison";
        public const string DefaultCondLossMultText = "1";

        // 参与转移的 affliction 类型，逗号分隔
        public static string CountedTypes = "damage,burn,bleeding,debuff,poison";
        static System.Collections.Generic.HashSet<string> countedTypeSet;
        public static System.Collections.Generic.HashSet<string> CountedTypeSet
        {
            get
            {
                if (countedTypeSet == null) RebuildCountedTypes();
                return countedTypeSet;
            }
        }
        static void RebuildCountedTypes()
        {
            countedTypeSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var t in (CountedTypes ?? "").Split(','))
            {
                string s = t.Trim();
                if (s.Length > 0) countedTypeSet.Add(s);
            }
        }

        static string ConfigPath =>
            Path.Combine(BondPlugin.PackageDir, "Config", "bond_config.xml");

        public static void Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) { Save(); return; }
                var root = XDocument.Load(ConfigPath).Root;
                if (root == null) return;
                SettleInterval = ParseDouble(root, "settleinterval", SettleInterval);
                ResistanceScale = ParseFloat(root, "resistancescale", ResistanceScale);
                MaxLinkDistance = ParseFloat(root, "maxlinkdistance", MaxLinkDistance);
                PowerDRef = ParseFloat(root, "powerdref", PowerDRef);
                PowerN = ParseFloat(root, "powern", PowerN);
                MinShare = ParseFloat(root, "minshare", MinShare);
                MaxShare = ParseFloat(root, "maxshare", MaxShare);
                CharmGraceTime = ParseFloat(root, "charmgrace", CharmGraceTime);
                CondLossMult = ParseFloat(root, "condlossmult", CondLossMult);
                Debug = ParseBool(root, "debug", Debug);
                string ct = root.Element("countedtypes")?.Value;
                if (!string.IsNullOrEmpty(ct)) { CountedTypes = ct; countedTypeSet = null; }
                BondLog.Log($"配置已加载：间隔={SettleInterval}s 抗性系数={ResistanceScale} 距离={(MaxLinkDistance > 0 ? MaxLinkDistance.ToString("0") : "不限")}");
            }
            catch (Exception ex) { BondLog.Warn($"配置加载失败，使用默认值：{ex.Message}"); }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                new XDocument(new XElement("BondConfig",
                    new XElement("settleinterval", SettleInterval),
                    new XElement("resistancescale", ResistanceScale),
                    new XElement("maxlinkdistance", MaxLinkDistance),
                    new XElement("powerdref", PowerDRef),
                    new XElement("powern", PowerN),
                    new XElement("minshare", MinShare),
                    new XElement("maxshare", MaxShare),
                    new XElement("charmgrace", CharmGraceTime),
                    new XElement("condlossmult", CondLossMult),
                    new XElement("countedtypes", CountedTypes),
                    new XElement("debug", Debug))).Save(ConfigPath);
            }
            catch (Exception ex) { BondLog.Warn($"配置保存失败：{ex.Message}"); }
        }

        // bondcfg 指令入口，返回 false 就是键名不认识
        public static bool Set(string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "settleinterval": SettleInterval = double.TryParse(value, out var d) ? d : SettleInterval; break;
                case "resistancescale": ResistanceScale = float.TryParse(value, out var f) ? f : ResistanceScale; break;
                case "maxlinkdistance": MaxLinkDistance = float.TryParse(value, out f) ? f : MaxLinkDistance; break;
                case "powerdref": PowerDRef = float.TryParse(value, out f) ? f : PowerDRef; break;
                case "powern": PowerN = float.TryParse(value, out f) ? f : PowerN; break;
                case "minshare": MinShare = float.TryParse(value, out f) ? f : MinShare; break;
                case "maxshare": MaxShare = float.TryParse(value, out f) ? f : MaxShare; break;
                case "debug": Debug = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "condlossmult": CondLossMult = float.TryParse(value, out f) ? f : CondLossMult; break;
                case "countedtypes": CountedTypes = value; countedTypeSet = null; break;
                default: return false;
            }
            Save();
            return true;
        }

        static float ParseFloat(XElement root, string name, float fallback)
            => float.TryParse(root.Element(name)?.Value, out var v) ? v : fallback;

        static double ParseDouble(XElement root, string name, double fallback)
            => double.TryParse(root.Element(name)?.Value, out var v) ? v : fallback;

        static bool ParseBool(XElement root, string name, bool fallback)
            => bool.TryParse(root.Element(name)?.Value, out var v) ? v : fallback;
    }

    // Debug 输出受配置开关控制
    public static class BondLog
    {
        public static void Log(string msg) => LuaCsLogger.LogMessage($"[绑定] {msg}", Color.LightGreen);
        public static void Warn(string msg) => LuaCsLogger.LogMessage($"[绑定] {msg}", Color.Orange);
        public static void Debug(string msg)
        {
            if (BondConfig.Debug) LuaCsLogger.LogMessage($"[绑定][dbg] {msg}", Color.LightGray);
        }
    }
}
