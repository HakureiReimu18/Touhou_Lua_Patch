using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Touhou.Affixes
{
    /*
真是拿你没办法呀，坐稳咯，虎杖：“老师！你的术式好碍事啊！”五条悟先是一愣，然后马上就理解了虎杖的意思，大笑着脱下自己的外套，说：“哈哈！尽管来吧！”虎杖同样大笑着拍上了老师的肩膀，在他之后他的学生们也不再拘谨，都带着轻松的笑容，为五条悟送上自己的祝福。“去吧！笨蛋眼罩！”“去证明你不是靠脸吃饭的男人”“如果很辛苦就换我上吧！”“要赢啊！五条先生！”“鲑鱼！！”有趣的是，因为缺少手臂的关系，狗卷「拍」向五条悟身体的，是自己的脚…
面对浑身咒力喷涌而出的五条悟，被誉为诅咒之王的两面宿傩，恍惚感受到了犹如排山倒海般的压力【千年未有的紧张感再一次浮上心头】。五条悟的拳头毫不留情砸向魔虚罗，致命的黑色闪电亮起。仓促间用右臂格挡的魔虚罗身体不禁后退，宿傩猛地回头，惊疑不定的表情充分暴露在五条悟面前。五条悟利用打出黑闪时进入的【无我境界】将自身状态拉满 —— 黑闪发出的一瞬间，平常需要刻意进行的咒力操作都会变得如呼吸一般自然。那是仿佛除自己外的一切都围绕着自己旋转的全能感。五条悟死死钳住宿傩的手腕砸向魔虚罗，两者交叠之际，第四发黑闪如期而至。魔虚罗用身体护住宿傩连退十数米，借着短暂摆脱纠缠的时机，五条悟开始近距离吟唱咒词。“位相”“波罗蜜”“光之柱”—— 五条悟就已经抬手对准了天际。宿傩惊慌失措地喊出了魔虚罗的大名，下一秒，魔虚罗像喷气发动机般追赫而去。眼看魔虚罗已呈败势，宿傩立刻出现，将目标锁定在移速缓慢的赫上。
既然苍的必经之路挡着五条悟这尊大佛，那就用【穿血】将赫引爆 —— 无论破坏哪一个，只要能阻止两者发生碰撞就行。宿傩的身体被五条悟一拳打歪，可压缩到极致的水箭还是射向了天际的赫，五条悟望着极速飞行的穿血。再次吟唱：“位相”“黄昏”“智慧之瞳”。宿傩瞳孔放大，一股寒气扑面而来。在被压缩的时间中，宿傩明白，自己再也没有任何机会阻止茈的诞生了
    */
    public class Mod : Barotrauma.LuaCs.IAssemblyPlugin
    {
        public static ContentPackage Package;
        public static string Name => Package?.Name ?? "Touhou.Affixes";
        public static bool DebugMode = false;

        private Harmony harmony;
        public static Dictionary<string, AffixDef> AffixDefs = new();
        public static Dictionary<ushort, AffixData> ItemAffixes = new();
        // 待恢复的词缀条目。物品 ID 跨巡回会被回收复用（离线玩家的物品不加载，ID 被矿石/掉落物占走），
        // 恢复必须 ID+prefab 双因子校验；uid 对不上就是另一件物品，拒贴
        public class PendingAffix
        {
            public string AffixId;
            public string PrefabId;
            public string Uid;
        }

        public static Dictionary<ushort, PendingAffix> PendingAffixes = new();

        // 三个拓展事件：附魔应用后 / 被移除前 / 适用性覆盖（返回 true/false 直接拍板，null 走默认）。
        // 订阅方必须轻量，异常会被隔离
        public static event Action<Item, AffixDef> AffixApplied;
        public static event Action<Item, AffixDef> AffixRemoved;
        public static event Func<AffixDef, Item, bool?> ApplicabilityOverride;

        static void RaiseAffixEvent(Action<Item, AffixDef> evt, Item item, AffixDef def)
        {
            if (evt == null || def == null) return;
            foreach (var d in evt.GetInvocationList())
            {
                try { ((Action<Item, AffixDef>)d)(item, def); }
                catch (Exception ex) { Warning($"Affix event subscriber failed: {ex.Message}"); }
            }
        }

        // 材料档位权重：数字越小越贵（1=核心 500 / 2=棱镜 100 / 3=合金 25），权重也越好。键是 tag 不是 identifier
        public static Dictionary<string, TierWeights> MaterialTiers = new()
        {
            ["affixes_material_1"] = new TierWeights { Normal = 5, Rare = 36, Epic = 36, Legendary = 20, Special = 8 },
            ["affixes_material_2"] = new TierWeights { Broken = 10, Normal = 30, Rare = 35, Epic = 15, Legendary = 10, Special = 1 },
            ["affixes_material_3"] = new TierWeights { Broken = 35, Normal = 40, Rare = 20, Epic = 5, Legendary = 1, Special = 0 },
        };

        static bool savedDataLoaded = false;

        // 主线程延迟任务队列。游戏状态（Item.ItemList 等）不是线程安全的，恢复词缀这类活必须在主线程干，
        // 由 GameMain.Update 补丁每帧扫一遍
        static readonly List<(double At, Action Task)> mainThreadTasks = new();

        // 随游戏时间走，暂停时不走
        public static void ScheduleOnMainThread(double delaySeconds, Action task)
        {
            mainThreadTasks.Add((Timing.TotalTime + delaySeconds, task));
        }

        public static void RunMainThreadScheduled()
        {
            TickSlotOneBonuses(); // 优先词条槽位监视（自带 0.25s 节流与全局开关）
            TickLoneWolfBonuses(); // 单打独斗词条快捷栏监视（自带 0.25s 节流与全局开关）
            TickAffixTagSelfHeal(); // 词缀标签自愈（自带 5s 节流）
            if (mainThreadTasks.Count == 0) return;
            for (int i = mainThreadTasks.Count - 1; i >= 0; i--)
            {
                if (Timing.TotalTime < mainThreadTasks[i].At) continue;
                var task = mainThreadTasks[i].Task;
                mainThreadTasks.RemoveAt(i);
                try { task(); } catch (Exception ex) { Warning($"Scheduled task failed: {ex.Message}"); }
            }
        }

        // 会话令牌：物品 ID 跨进程会漂移，桥接文件只在同一次进程里有效，令牌对不上整份作废
        static readonly string SessionToken = Guid.NewGuid().ToString("N");

        public const string AFFIX_TAG_PREFIX = "__affix_";
        // 词缀实例 UID 标签，附魔时生成，全局唯一，存进战役存档。同 uid 出现在两件物品上 = 复制，恢复扫描时排毒。
        // 前缀特意不让它命中 "__affix_"（第 7 位是 u 不是 _），免得被词缀标签检查误伤
        public const string AFFIX_UID_TAG_PREFIX = "__affixuid_";
        // 只在附魔/恢复补戳时调，量小，Guid 开销无所谓
        static string NewAffixUid() => Guid.NewGuid().ToString("N").Substring(0, 12);
        // 附魔石 tag：万能载体，带词缀时丢附魔台当材料可把词缀转给目标
        public const string STONE_TAG = "affixes_stone";
        // 奇迹词缀 id：解锁两个额外槽，只能经附魔石转移附上
        public const string MIRACLE_AFFIX_ID = "miracle";
        // 服务端→客户端的同步消息 id：附魔台在服务端跑，客户端要本地镜像一份才有显示
        public const string NET_APPLY_AFFIX = "itemaffixes_apply";

        // 包名里带"东方"就算东方模组（"东方潜渊行动组"及其本地测试版、其他东方系都覆盖）
        public const string TouhouPackageNameKeyword = "东方";

        // 药物词缀生效的最低耐久比例：耐久见底药本身就没效果了，词缀也别生效
        public static float MedicalMinConditionPercent = 0.2f;

        // 本上下文该不该动伤害/耐久。本地开服时 CLIENT 和 SERVER 两个程序集同时加载、补丁打在同一份方法上，
        // 不区分的话每个伤害前缀跑两遍，倍率直接平方（金冠 1.25 → 1.5625）。单人/远端客户端=CLIENT，开服=SERVER
        public static bool IsGameplayAuthority =>
#if SERVER
            GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer;
#else
            GameMain.NetworkMember == null || GameMain.NetworkMember.IsClient;
#endif

        // 全局快速退出标志：没有这类词缀时对应补丁整体跳过（LoadAffixDefs 时计算）
        public static bool AnyDamageMultAffixes;
        public static bool AnyDamageTakenAffixes;
        public static bool AnyFuelMultAffixes;
        public static bool AnyAmmoSaveAffixes;
        public static bool AnyDurabilitySaveAffixes;
        public static bool AnyDeviceRepairAffixes;
        public static bool AnyCooldownDamageAffixes;
        public static bool AnyLastShotAffixes;
        public static bool AnySlotOneAffixes;
        public static bool AnyNoiseAffixes;
        public static bool AnyAimWobbleAffixes;   // 稳定：手部受伤不抖动
        public static bool AnyMovePenaltyAffixes; // 踏步：腿部受伤不减速
        public static bool AnyThornsAffixes;      // 荆棘：受击反伤
        public static bool AnyMassBonusAffixes;   // 巨人杀手：高质量目标增伤
        public static bool AnyChargeTimeAffixes;  // 紧张/等候：蓄力时间倍率
        public static bool AnyFirstShotAffixes;   // 领先：满弹匣首发增伤
        public static bool AnyLoneWolfAffixes;    // 单打独斗：快捷栏唯一附魔武器增伤

        // 伤害倍率调试日志开关（affixdmgdebug 指令切换）
        public static bool DamageDebugLog;

        public void Initialize()
        {
            if (!LuaCsSetup.Instance.PluginPackageManager.TryGetPackageForPlugin<Mod>(out ContentPackage package))
            {
                LuaCsLogger.LogMessage("[Touhou.Affixes] Could not find ContentPackage", Color.Red);
                return;
            }
            Package = package;
            if (Package.Dir.Contains("LocalMods"))
            {
                DebugMode = true;
                LogOnce($"Found [{Package.Name}] in LocalMods, debug mode enabled");
            }

            harmony = new Harmony("com.itemaffixes.mod");

            LogOnce($"ItemAffixes mod initialized from: {Package.Dir}");
        }

        // Harmony 不去重同 owner 补丁：OnLoadCompleted 每次内容重载都会触发，
        // 不防护的话补丁/命令/钩子会层层叠加（tooltip 词缀行显示 2 遍、4 遍……就是因此）
        static bool oneTimeInitDone;

        // 只按命名空间逐类注册，绝不用程序集级 PatchAll：整个模组的 C# 编译进同一程序集，
        // PatchAll 会把 Bond/MartialArts 那些插件的补丁也用本 Harmony 再打一遍 = 双倍执行。
        // 逐类 try/catch：某个补丁类目标缺失不会拖垮其余（PatchAll 会整体中断）
        void PatchOwnNamespaces()
        {
            Type[] types;
            try { types = System.Reflection.Assembly.GetExecutingAssembly().GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            int patchedTypes = 0;
            foreach (var type in types)
            {
                if (type.Namespace != "Touhou.Affixes"
                    && type.Namespace != "Touhou.DescToggle"
                    && type.Namespace != "Touhou.WearableOrder"
                    && type.Namespace != "Touhou.RagdollScale") continue;
                try
                {
                    var result = new PatchClassProcessor(harmony, type).Patch();
                    if (result != null && result.Count > 0) patchedTypes++;
                }
                catch (Exception ex)
                {
                    Warning($"Failed to patch {type.FullName}: {ex.Message}");
                }
            }
            LogOnce($"Patching complete: {patchedTypes} patch classes applied");
#if CLIENT
            // 穿戴绘制顺序修正：显式安装（非 [HarmonyPatch] 特性），目标缺失时只记日志，
            // 不干扰本程序集里其他插件的 PatchAll（详见 WearableDrawOrder.cs）
            Touhou.WearableOrder.WearableDrawOrderPatch.TryInstall(harmony);
#endif
        }

        public void OnLoadCompleted()
        {
            LoadAffixDefs();
            if (oneTimeInitDone)
            {
                LogOnce($"Reloaded {AffixDefs.Count} affix definitions (patches/hooks already registered, skipped)");
                return;
            }
            oneTimeInitDone = true;

            PatchOwnNamespaces();

            AddCommands();

            LuaCsSetup.Instance.Hook.Add("roundEnd", "ItemAffixes.RoundEnd", OnRoundEnd);
            LuaCsSetup.Instance.Hook.Add("roundStart", "ItemAffixes.RoundStart", OnRoundStart);

            LogOnce($"Loaded {AffixDefs.Count} affix definitions, patching complete");
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "ItemAffixes.RoundEnd");
            LuaCsSetup.Instance.Hook.Remove("roundStart", "ItemAffixes.RoundStart");
            LuaCsSetup.Instance.Game.RemoveCommand("enchant");
            LuaCsSetup.Instance.Game.RemoveCommand("giveaffix");
            LuaCsSetup.Instance.Game.RemoveCommand("removeaffix");
            LuaCsSetup.Instance.Game.RemoveCommand("clearallaffixes");
            LuaCsSetup.Instance.Game.RemoveCommand("listaffixes");
            LuaCsSetup.Instance.Game.RemoveCommand("listaffixdefs");
            LuaCsSetup.Instance.Game.RemoveCommand("affixdmgdebug");
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;   // Dispose 后补丁已卸载，允许下次完整重注册
            ItemAffixes.Clear();
            AffixDefs.Clear();
            PendingAffixes.Clear();
            mainThreadTasks.Clear();
            loggedOnceMessages.Clear(); // 日志去重键按物品 ID 生成，卸载后重新加载时该重新提示就重新提示
            AffixApplied = null;
            AffixRemoved = null;
            ApplicabilityOverride = null;
        }

        // Affixes.xml 里已解析的属性名，之外的全收进 CustomProps
        static readonly HashSet<string> KnownAffixAttrs = new(StringComparer.OrdinalIgnoreCase)
        {
            "identifier", "tier", "nameprefix", "applicable", "desc", "descarmor",
            "damagemult", "fireratemult", "damagetakenmult", "fuelconsumemult",
            "repairbonuspercent", "spreadmult", "skillreqmult",
            "ammosavechance", "durabilitysavechance", "structurefixmult", "devicerepairmult",
            "cooldowndamagemult", "cooldowndamageinterval", "lastshotdamagemult", "forcetwohanded",
            "slot1damagemult", "slot1fireratemult", "noisemult",
            "noaimwobble", "nomovepenalty",
            "thornsbleeding", "thornslacerations", "thornsstun", "thornsstunchance", "thornsinterval",
            "forcetwohandedranged", "massbonusmult", "massthreshold",
            "chargetimemult", "firstshotdamagemult", "lonewolfdamagemult",
            "stonerollonly"
        };

        private void LoadAffixDefs()
        {
            AffixDefs.Clear();            try
            {
                string affixPath = System.IO.Path.Combine(Package.Dir, "Items", "Affixes.xml");
                if (!System.IO.File.Exists(affixPath))
                {
                    Warning($"Affixes.xml not found at {affixPath}");
                    return;
                }

                var doc = XDocument.Load(affixPath);
                if (doc.Root == null) return;

                foreach (var element in doc.Root.Elements("Affix"))
                {
                    string id = element.Attribute("identifier")?.Value;
                    if (string.IsNullOrEmpty(id))
                    {
                        Warning("Affix element missing identifier, skipping");
                        continue;
                    }

                    var def = new AffixDef();
                    def.Identifier = id;
                    def.Tier = element.Attribute("tier")?.Value ?? "Normal";
                    def.NamePrefix = element.Attribute("nameprefix")?.Value ?? "";
                    def.Applicable = element.Attribute("applicable")?.Value ?? "all";
                    def.Description = element.Attribute("desc")?.Value ?? "";
                    def.DescriptionArmor = element.Attribute("descarmor")?.Value ?? "";

                    // 本地化：Text/ 下的 affix.prefix/desc.<id> 优先，没翻译的语言回落默认文本，再不行用 XML 属性
                    def.NamePrefixLoc = TextManager.Get($"affix.prefix.{id}").Fallback(def.NamePrefix, true);
                    def.DescriptionLoc = TextManager.Get($"affix.desc.{id}").Fallback(def.Description, true);
                    def.DescriptionArmorLoc = TextManager.Get($"affix.descarmor.{id}").Fallback(def.DescriptionArmor, true);

                    def.DamageMult = ParseInvariantFloat(element.Attribute("damagemult")?.Value, 1f);
                    def.FireRateMult = ParseInvariantFloat(element.Attribute("fireratemult")?.Value, 1f);
                    def.DamageTakenMult = ParseInvariantFloat(element.Attribute("damagetakenmult")?.Value, 1f);
                    def.FuelConsumeMult = ParseInvariantFloat(element.Attribute("fuelconsumemult")?.Value, 1f);
                    def.SpreadMult = ParseInvariantFloat(element.Attribute("spreadmult")?.Value, 1f);
                    def.SkillReqMult = ParseInvariantFloat(element.Attribute("skillreqmult")?.Value, 1f);
                    def.AmmoSaveChance = ParseInvariantFloat(element.Attribute("ammosavechance")?.Value, 0f);
                    def.DurabilitySaveChance = ParseInvariantFloat(element.Attribute("durabilitysavechance")?.Value, 0f);
                    def.StructureFixMult = ParseInvariantFloat(element.Attribute("structurefixmult")?.Value, 1f);
                    def.DeviceRepairMult = ParseInvariantFloat(element.Attribute("devicerepairmult")?.Value, 1f);
                    def.CooldownDamageMult = ParseInvariantFloat(element.Attribute("cooldowndamagemult")?.Value, 1f);
                    def.CooldownDamageInterval = ParseInvariantFloat(element.Attribute("cooldowndamageinterval")?.Value, 30f);
                    def.LastShotDamageMult = ParseInvariantFloat(element.Attribute("lastshotdamagemult")?.Value, 1f);
                    def.ForceTwoHanded = bool.TryParse(element.Attribute("forcetwohanded")?.Value, out bool fth) && fth;
                    def.SlotOneDamageMult = ParseInvariantFloat(element.Attribute("slot1damagemult")?.Value, 1f);
                    def.SlotOneFireRateMult = ParseInvariantFloat(element.Attribute("slot1fireratemult")?.Value, 1f);
                    def.NoiseMult = ParseInvariantFloat(element.Attribute("noisemult")?.Value, 1f);
                    def.NoAimWobble = bool.TryParse(element.Attribute("noaimwobble")?.Value, out bool naw) && naw;
                    def.NoMovePenalty = bool.TryParse(element.Attribute("nomovepenalty")?.Value, out bool nmp) && nmp;
                    def.ThornsBleeding = ParseInvariantFloat(element.Attribute("thornsbleeding")?.Value, 0f);
                    def.ThornsLacerations = ParseInvariantFloat(element.Attribute("thornslacerations")?.Value, 0f);
                    def.ThornsStun = ParseInvariantFloat(element.Attribute("thornsstun")?.Value, 0f);
                    def.ThornsStunChance = ParseInvariantFloat(element.Attribute("thornsstunchance")?.Value, 1f);
                    def.ThornsInterval = ParseInvariantFloat(element.Attribute("thornsinterval")?.Value, 2f);
                    def.ForceTwoHandedRanged = bool.TryParse(element.Attribute("forcetwohandedranged")?.Value, out bool fthr) && fthr;
                    def.MassBonusMult = ParseInvariantFloat(element.Attribute("massbonusmult")?.Value, 1f);
                    def.MassThreshold = ParseInvariantFloat(element.Attribute("massthreshold")?.Value, 100f);
                    def.ChargeTimeMult = ParseInvariantFloat(element.Attribute("chargetimemult")?.Value, 1f);
                    def.FirstShotDamageMult = ParseInvariantFloat(element.Attribute("firstshotdamagemult")?.Value, 1f);
                    def.LoneWolfDamageMult = ParseInvariantFloat(element.Attribute("lonewolfdamagemult")?.Value, 1f);
                    def.StoneRollOnly = bool.TryParse(element.Attribute("stonerollonly")?.Value, out bool sro) && sro;

                    // 没见过的属性原样收进 CustomProps，以后加新词缀参数不用改解析代码
                    foreach (var attr in element.Attributes())
                    {
                        if (!KnownAffixAttrs.Contains(attr.Name.LocalName))
                            (def.CustomProps ??= new Dictionary<string, string>())[attr.Name.LocalName] = attr.Value;
                    }

                    def.DisplayColor = def.Tier switch
                    {
                        "Broken" => new Color(128, 128, 128),
                        "Normal" => Color.White,
                        "Rare" => new Color(74, 144, 255),
                        "Epic" => new Color(192, 64, 255),
                        "Legendary" => new Color(255, 140, 0),
                        "Special" => new Color(255, 64, 64),
                        _ => Color.White
                    };

                    def.Effects = new List<StatusEffect>();
                    foreach (var child in element.Elements())
                    {
                        string childName = child.Name.ToString().ToLowerInvariant();
                        if (childName == "statuseffect")
                        {
                            try
                            {
                                var effect = StatusEffect.Load(new ContentXElement(null, child), parentDebugName: $"Affix.{id}");
                                if (effect != null) def.Effects.Add(effect);
                            }
                            catch (Exception ex)
                            {
                                Warning($"Failed to load status effect for affix {id}: {ex.Message}");
                            }
                        }
                    }

                    AffixDefs[id] = def;
                }

                AnyDamageMultAffixes = AffixDefs.Values.Any(a => Math.Abs(a.DamageMult - 1f) > 0.0001f);
                AnyDamageTakenAffixes = AffixDefs.Values.Any(a => Math.Abs(a.DamageTakenMult - 1f) > 0.0001f);
                AnyFuelMultAffixes = AffixDefs.Values.Any(a => Math.Abs(a.FuelConsumeMult - 1f) > 0.0001f);
                AnyAmmoSaveAffixes = AffixDefs.Values.Any(a => a.AmmoSaveChance > 0f);
                AnyDurabilitySaveAffixes = AffixDefs.Values.Any(a => a.DurabilitySaveChance > 0f);
                AnyDeviceRepairAffixes = AffixDefs.Values.Any(a => Math.Abs(a.DeviceRepairMult - 1f) > 0.0001f);
                AnyCooldownDamageAffixes = AffixDefs.Values.Any(a => a.CooldownDamageMult > 1f);
                AnyLastShotAffixes = AffixDefs.Values.Any(a => a.LastShotDamageMult > 1f);
                AnySlotOneAffixes = AffixDefs.Values.Any(a =>
                    a.SlotOneDamageMult > 1f || Math.Abs(a.SlotOneFireRateMult - 1f) > 0.0001f);
                AnyNoiseAffixes = AffixDefs.Values.Any(a => Math.Abs(a.NoiseMult - 1f) > 0.0001f);
                AnyAimWobbleAffixes = AffixDefs.Values.Any(a => a.NoAimWobble);
                AnyMovePenaltyAffixes = AffixDefs.Values.Any(a => a.NoMovePenalty);
                AnyThornsAffixes = AffixDefs.Values.Any(a => a.ThornsBleeding > 0f || a.ThornsLacerations > 0f || a.ThornsStun > 0f);
                AnyMassBonusAffixes = AffixDefs.Values.Any(a => a.MassBonusMult > 1f);
                AnyChargeTimeAffixes = AffixDefs.Values.Any(a => Math.Abs(a.ChargeTimeMult - 1f) > 0.0001f);
                AnyFirstShotAffixes = AffixDefs.Values.Any(a => a.FirstShotDamageMult > 1f);
                AnyLoneWolfAffixes = AffixDefs.Values.Any(a => a.LoneWolfDamageMult > 1f);
            }
            catch (Exception ex)
            {
                Warning($"Failed to load Affixes.xml: {ex.Message}");
            }
        }

        static float ParseInvariantFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        // LuaCs 的命令行参数被包成 args[0] 里的 string[]，直接 ToString 只会得到 "System.String[]"
        public static string[] CmdArgs(object[] args)
        {
            if (args == null || args.Length == 0) return Array.Empty<string>();
            if (args.Length == 1 && args[0] is string[] real) return real;
            return args.Select(a => a?.ToString()).ToArray();
        }

        public static void AddCommands()
        {
            // 控制台自动补全：列出全部词缀 id
            LuaCsFunc affixIdArgs = _ => new[] { AffixDefs.Keys.OrderBy(id => id).ToArray() };

            // 物品 Tags 的实例修改不会自动网络同步：附魔台在服务端跑完，客户端要本地镜像一份才看得到
            RegisterNetReceiver();

            LuaCsSetup.Instance.Game.AddCommand("enchant", "Apply a random affix to the held item: enchant <affixid>", (args) =>
            {
                if (Character.Controlled == null) return;
                var heldItem = Character.Controlled.HeldItems?.FirstOrDefault();
                if (heldItem == null)
                {
                    Log("No item held", Color.Yellow);
                    return;
                }

                var cmdArgs = CmdArgs(args);
                string affixId = cmdArgs.Length > 0 ? cmdArgs[0] : null;

                AffixDef chosen;
                if (!string.IsNullOrEmpty(affixId) && AffixDefs.TryGetValue(affixId.ToLowerInvariant(), out var specific))
                {
                    chosen = specific;
                    if (!IsAffixApplicable(chosen, heldItem))
                        Log($"Warning: [{affixId}] is NOT applicable to {heldItem.Name} (debug bypass, enchant at your own risk)", Color.Orange);
                }
                else
                {
                    var pool = AffixDefs.Values.Where(a => IsAffixApplicable(a, heldItem)
                        && (!a.StoneRollOnly || heldItem.HasTag(STONE_TAG))).ToList();
                    if (pool.Count == 0)
                    {
                        Log($"No affixes available for {heldItem.Name}", Color.Yellow);
                        return;
                    }
                    chosen = pool[Rand.Range(0, pool.Count, Rand.RandSync.Unsynced)];
                }

                ApplyAffix(heldItem, chosen);
                SaveAffixData();
                savedDataLoaded = false;
            }, affixIdArgs, false);

            // 定向附魔，严格校验适用性，多人需要服务端给玩家勾选该命令权限。
            // RelayToServer=false：有权限的客户端本地执行（服务器控制台用第二参数指定玩家）
            LuaCsSetup.Instance.Game.AddCommand("giveaffix", "Apply a specific affix to the held item: giveaffix <affixid> [player]", (args) =>
            {
                var cmdArgs = CmdArgs(args);
                if (cmdArgs.Length == 0)
                {
                    Log("Usage: giveaffix <affixid> [player]", Color.Yellow);
                    return;
                }

                Character targetChar = Character.Controlled;
                if (targetChar == null && cmdArgs.Length > 1 && GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer)
                {
                    string playerName = cmdArgs[1];
                    targetChar = GameMain.NetworkMember.ConnectedClients?.FirstOrDefault(c => c?.Character != null &&
                        string.Equals(c.Name, playerName, StringComparison.OrdinalIgnoreCase))?.Character;
                }
                if (targetChar == null)
                {
                    Log("No controlled character. On server console use: giveaffix <affixid> <player>", Color.Yellow);
                    return;
                }

                var heldItem = targetChar.HeldItems?.FirstOrDefault();
                if (heldItem == null)
                {
                    Log($"{targetChar.Name} is not holding any item", Color.Yellow);
                    return;
                }

                string affixId = cmdArgs[0]?.ToLowerInvariant();
                if (string.IsNullOrEmpty(affixId) || !AffixDefs.TryGetValue(affixId, out var def))
                {
                    Log($"Unknown affix id [{affixId}]. Use tab-completion or listaffixdefs.", Color.Yellow);
                    return;
                }
                if (!IsAffixApplicable(def, heldItem))
                {
                    Log($"[{affixId}] is not applicable to {heldItem.Name}", Color.Orange);
                    return;
                }

                ApplyAffix(heldItem, def);
                BroadcastAffixApplied(heldItem, def);
                SaveAffixData();
                savedDataLoaded = false;
            }, affixIdArgs, false);
            SetCommandRelayToServer("giveaffix", false);

            LuaCsSetup.Instance.Game.AddCommand("removeaffix", "Remove the affix from the held item", (args) =>
            {
                if (Character.Controlled == null) return;
                var heldItem = Character.Controlled.HeldItems?.FirstOrDefault();
                if (heldItem == null) return;

                if (TryGetAffixData(heldItem, out var data))
                {
                    var oldDef = GetEffectiveDef(data);
                    UnregisterEffects(heldItem, data.Effects);
                    RestoreStatChanges(data, heldItem);
                    ItemAffixes.Remove(heldItem.ID);
                    RemoveAffixTag(heldItem);
                    SaveAffixData();
                    savedDataLoaded = false;
                    RaiseAffixEvent(AffixRemoved, heldItem, oldDef);
                    Log($"Removed affix [{data.PlainPrefixDisplay ?? data.NamePrefix}] from {heldItem.Name}");
                }
                else
                {
                    Log($"No affix on {heldItem.Name}");
                }
            }, null, false);

            LuaCsSetup.Instance.Game.AddCommand("clearallaffixes", "Remove ALL affixes from every item, then SAVE to write the clean state to file", (args) =>
            {
                int cleared = 0;
                foreach (var item in SnapshotItems() ?? new List<Item>())
                {
                    bool hadAffix = ItemAffixes.TryGetValue(item.ID, out var data);
                    bool hadTag = item.GetTags().Any(t =>
                        t.Value.StartsWith(AFFIX_TAG_PREFIX, StringComparison.OrdinalIgnoreCase));
                    if (!hadAffix && !hadTag) continue;

                    // 事件要旧词缀定义：先查内存表（奇迹是合成词缀），查不到再解析标签
                    AffixDef oldDef = null;
                    if (hadAffix) oldDef = GetEffectiveDef(data);
                    if (oldDef == null) Helpers.TryReadAffixFromTags(item, out oldDef);

                    if (hadAffix)
                    {
                        UnregisterEffects(item, data.Effects);
                        RestoreStatChanges(data, item);
                        ItemAffixes.Remove(item.ID);
                    }
                    RemoveAffixTag(item);
                    RaiseAffixEvent(AffixRemoved, item, oldDef);
                    cleared++;
                }

                PendingAffixes.Clear();
                try { File.Delete(SaveFilePath); } catch { }
                savedDataLoaded = false;

                // 存档里的 affixid 属性不用手工清：Item.Save 只在带 __affix_ 标签时才写，
                // 标签已清空，下次正常保存出来的就是干净文件
                Log($"Cleared {cleared} affixes. NOW SAVE (editor: save submarine / campaign: save game) to make it permanent.", Color.Orange);
            }, null, false);

            LuaCsSetup.Instance.Game.AddCommand("listaffixes", "List all affixed items", (args) =>
            {
                if (ItemAffixes.Count == 0)
                {
                    Log("No affixed items in memory");
                    return;
                }
                foreach (var kv in ItemAffixes)
                {
                    var item = FindItemById(kv.Key);
                    string info;
                    if (item == null) info = "(item not found)";
                    else if (item.Removed) info = "REMOVED";
                    else if (item.ParentInventory?.Owner is Character c) info = $"held by {c.Name}";
                    else if (item.ParentInventory?.Owner is Item i) info = $"in {i.Name}";
                    else info = $"pos={item.Position.X:F0},{item.Position.Y:F0}";
                    Log($"  [{JoinAffixIds(kv.Value)}] ID={kv.Key} {info}");
                }
            }, null, false);

            LuaCsSetup.Instance.Game.AddCommand("listaffixdefs", "List all affix definitions and their applicable targets", (args) =>
            {
                foreach (var def in AffixDefs.Values.OrderBy(d => d.Tier).ThenBy(d => d.Identifier))
                {
                    Log($"  {def.Identifier} [{def.Tier}] applicable={def.Applicable}");
                }
            }, null, false);

            LuaCsSetup.Instance.Game.AddCommand("affixdmgdebug", "Toggle damage multiplier debug logging", (args) =>
            {
                DamageDebugLog = !DamageDebugLog;
                Log($"affix damage debug logging: {(DamageDebugLog ? "ON" : "OFF")}", Color.Yellow);
            }, null, false);
        }

        // LuaCs 命令默认 RelayToServer=true 会转发到服务器，但服务器拿不到"是谁按的回车"，
        // Character.Controlled 恒为 null。"给自己手上附魔"这类命令要本地执行，权限仍由引擎把关
        static void SetCommandRelayToServer(string name, bool relay)
        {
            try
            {
                var commandsProp = typeof(DebugConsole).GetProperty("Commands",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (commandsProp?.GetValue(null) is not System.Collections.IEnumerable commands) return;
                foreach (var cmd in commands)
                {
                    if (cmd == null) continue;
                    var namesField = cmd.GetType().GetField("Names");
                    if (namesField?.GetValue(cmd) is not System.Collections.Immutable.ImmutableArray<Identifier> names) continue;
                    if (!names.Any(n => n.Value.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    cmd.GetType().GetField("RelayToServer")?.SetValue(cmd, relay);
                }
            }
            catch (Exception ex)
            {
                Warning($"SetCommandRelayToServer({name}) failed: {ex.Message}");
            }
        }

        // 物品列表快照：前哨站生成/巡回转换时加载线程会动 Item.ItemList，主线程直接 foreach 就炸
        //（2026-09-12 实测崩在 TickAffixTagSelfHeal）。枚举副本就没事；快照失败返回 null，本轮跳过
        static List<Item> SnapshotItems()
        {
            try { return Item.ItemList.ToList(); }
            catch (Exception) { return null; }
        }

        static Item FindItemById(ushort id)
        {
            return FindItemById(id, SnapshotItems());
        }

        // 在调用方给的快照里按 ID 找——0.25s 监视每轮共享一份快照，不然每条词缀复制一次全物品列表太伤
        static Item FindItemById(ushort id, List<Item> items)
        {
            if (items == null) return null;
            foreach (var item in items)
            {
                if (item.ID == id) return item;
            }
            return null;
        }

        // 内存表读取（带身份校验）：物品 ID 会被引擎回收复用，残留条目会让新物品"继承"词缀。
        // prefab 对不上就当场清掉；PrefabId 为空是旧数据，没法校验只能放行
        public static bool TryGetAffixData(Item item, out AffixData data)
        {
            if (item != null && ItemAffixes.TryGetValue(item.ID, out data))
            {
                if (data.PrefabId == null || item.Prefab.Identifier.Value == data.PrefabId) return true;
                ItemAffixes.Remove(item.ID); // 过期条目：这是同 ID 的另一个物品
            }
            data = null;
            return false;
        }

        static void RemoveAffixTag(Item item)
        {
            foreach (var tag in item.GetTags().ToList())
            {
                if (tag.Value.StartsWith(AFFIX_TAG_PREFIX, StringComparison.OrdinalIgnoreCase)
                    || tag.Value.StartsWith(AFFIX_UID_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                    item.RemoveTag(tag);
            }
        }

        // 只读 UID 标签；没标签返回 null——可能从没附魔，也可能标记被整串擦过
        static string ReadUidTag(Item item)
        {
            if (string.IsNullOrEmpty(item.Tags) || !item.Tags.Contains(AFFIX_UID_TAG_PREFIX)) return null;
            foreach (var tag in item.GetTags())
            {
                if (tag.Value.StartsWith(AFFIX_UID_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                    return tag.Value.Substring(AFFIX_UID_TAG_PREFIX.Length);
            }
            return null;
        }

        // 一遍遍历读出词缀 ID 和 UID（恢复扫描未命中路径用，省一次遍历）；奇迹时 affixId 是逗号连的全部
        static bool TryReadAffixTags(Item item, out string affixId, out string uid)
        {
            affixId = null; uid = null;
            // 快速拒绝：两个前缀都以 "__affix" 开头，不含此子串的物品直接返回
            if (string.IsNullOrEmpty(item.Tags) || !item.Tags.Contains("__affix")) return false;
            List<string> ids = null;
            foreach (var tag in item.GetTags())
            {
                if (tag.Value.StartsWith(AFFIX_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                    (ids ??= new List<string>(2)).Add(tag.Value.Substring(AFFIX_TAG_PREFIX.Length));
                else if (tag.Value.StartsWith(AFFIX_UID_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                    uid = tag.Value.Substring(AFFIX_UID_TAG_PREFIX.Length);
            }
            if (ids == null) return false;
            affixId = string.Join(",", ids);
            return true;
        }

        public static void SaveAffixData()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SaveFilePath));
                var doc = new XDocument(new XElement("AffixData",
                    new XAttribute("session", SessionToken),
                    new XAttribute("saveid", CurrentSaveId)));
                int saved = 0;
                var saveSnapshot = SnapshotItems() ?? new List<Item>();
                foreach (var kv in ItemAffixes)
                {
                    // 记 prefab：ID 会被回收复用，恢复时靠它确认不是"同 ID 的另一个物品"
                    var item = saveSnapshot.FirstOrDefault(i => i.ID == kv.Key && !i.Removed);
                    // 找到的实例 prefab 和记录不符 = ID 已被复用，写进去会把词缀贴到无辜物品上，跳过
                    if (item != null && kv.Value.PrefabId != null
                        && item.Prefab.Identifier.Value != kv.Value.PrefabId) continue;
                    var el = new XElement("Item",
                        new XAttribute("id", kv.Key.ToString()),
                        new XAttribute("affixid", JoinAffixIds(kv.Value)));
                    string prefab = kv.Value.PrefabId ?? item?.Prefab.Identifier.Value;
                    if (prefab != null) el.SetAttributeValue("prefab", prefab);
                    if (kv.Value.Uid != null) el.SetAttributeValue("uid", kv.Value.Uid);
                    doc.Root.Add(el);
                    saved++;
                }
                doc.Save(SaveFilePath);
                Log($"Saved {saved} affixes to file");
            }
            catch (Exception ex)
            {
                Warning($"Failed to save affix data: {ex.Message}");
            }
        }

        public static void LoadAffixData()
        {
            try
            {
                if (!File.Exists(SaveFilePath))
                {
                    Log("No affix save file found");
                    return;
                }
                var doc = XDocument.Load(SaveFilePath);
                if (doc.Root == null) return;
                // 会话令牌校验：跨进程的文件内容不可信（物品 ID 会漂移）
                if (doc.Root.Attribute("session")?.Value != SessionToken)
                {
                    Log("Ignoring affix save file from a previous session (stale item IDs)");
                    return;
                }
                // 换存档校验：同进程内中途退出没走 roundEnd 就进新档的话，上一档的桥接数据作废，
                // 不然旧词缀按 ID 撞进新存档
                string fileSaveId = doc.Root.Attribute("saveid")?.Value ?? "";
                if (fileSaveId != CurrentSaveId)
                {
                    Log("Ignoring affix save file: it belongs to a different save (stale bridge data discarded)");
                    return;
                }
                int loaded = 0;
                foreach (var el in doc.Root.Elements("Item"))
                {
                    if (ushort.TryParse(el.Attribute("id")?.Value, out var id) &&
                        !string.IsNullOrEmpty(el.Attribute("affixid")?.Value))
                    {
                        PendingAffixes[id] = new PendingAffix
                        {
                            AffixId = el.Attribute("affixid").Value,
                            PrefabId = el.Attribute("prefab")?.Value,
                            Uid = el.Attribute("uid")?.Value
                        };
                        loaded++;
                    }
                }
                Log($"Loaded {loaded} affixes from file");
            }
            catch (Exception ex)
            {
                Warning($"Failed to load affix data: {ex.Message}");
            }
        }

        // 恢复词缀。isFinal=true 是最后一次重试：还恢复不了的（离线玩家的包）直接丢弃，
        // 留着只会被复用同 ID 的新物品误触发。晚加入玩家的物品走 Item.Load 的 affixid 属性恢复，不靠这里
        static void RestoreAffixes(bool isFinal = false)
        {
            int count = 0;
            int pruned = 0;
            var items = SnapshotItems();
            if (items == null)
            {
                Warning("RestoreAffixes skipped: item list snapshot failed (level loading in progress)");
                return;
            }
            foreach (var item in items)
            {
                if (item.Removed) continue;
                // 带校验的命中检查：过期条目（同 ID 的另一个物品）会被清掉并继续走恢复
                if (TryGetAffixData(item, out _))
                {
                    // 内存已有 = 词缀已就位，pending 一并消费掉——不然它会挂到最终轮报"物品不存在"的假警告，
                    // 甚至在 removeaffix 后把刚剥掉的词缀贴回来
                    PendingAffixes.Remove(item.ID);
                    continue;
                }

                string affixId = null;
                string uidToApply = null;

                if (PendingAffixes.TryGetValue(item.ID, out var pending))
                {
                    // 双因子校验：ID 对上还不够，prefab 必须一致——
                    // 离线玩家的物品不加载，其 ID 会被新刷出的物品（矿石/掉落物）复用
                    if (pending.PrefabId != null
                        && item.Prefab.Identifier.Value != pending.PrefabId)
                    {
                        if (isFinal)
                        {
                            Warning($"Dropped stale pending affix '{pending.AffixId}' for ID={item.ID}: "
                                + $"prefab mismatch ({item.Prefab.Identifier.Value} != {pending.PrefabId})");
                            PendingAffixes.Remove(item.ID);
                        }
                        continue;
                    }
                    // uid 对不上 = 铁证是另一件物品（同 prefab 也蒙混不过去），拒贴丢弃。
                    // 没 uid 标签没法验证——这正是桥接文件要救的"标记丢失"场景，按 ID+prefab 放行并补戳回去
                    string itemUid = ReadUidTag(item);
                    if (pending.Uid != null && itemUid != null
                        && !string.Equals(itemUid, pending.Uid, StringComparison.OrdinalIgnoreCase))
                    {
                        Warning($"Dropped pending affix '{pending.AffixId}' for ID={item.ID}: "
                            + $"uid mismatch ({itemUid} != {pending.Uid}), item is provably a different one");
                        PendingAffixes.Remove(item.ID);
                        continue;
                    }
                    if (pending.PrefabId == null)
                        Warning($"Restoring affix '{pending.AffixId}' on {item.Name} (ID={item.ID}) without prefab check (legacy save data)");
                    affixId = pending.AffixId;
                    uidToApply = pending.Uid;
                    // 命中即消费掉条目：防止本巡回后段新物品复用同一 ID 时误恢复
                    PendingAffixes.Remove(item.ID);
                }
                else
                {
                    // 旧存档物品没有 uid 标签，恢复时 ApplyAffix 会补戳（每件旧物品只发生一次）
                    TryReadAffixTags(item, out affixId, out uidToApply);
                }

                if (!string.IsNullOrEmpty(affixId))
                {
                    // affixId 可能是逗号连接的多词缀列表（奇迹）：逐项校验存在性/适用性/槽位规则
                    var validIds = ValidateAffixIdListForRestore(item, affixId);
                    if (validIds.Count == 0)
                    {
                        // 大声日志：这是唯一会永久删除词缀标签的路径，绝不能无声发生
                        Warning($"Pruned affix '{affixId}' from {item.Name} (ID={item.ID}): no valid/applicable affix remains");
                        RemoveAffixTag(item);
                        pruned++;
                        continue;
                    }
                    if (validIds.Count < affixId.Split(',').Length) pruned++;
                    SetAffixes(item, validIds, uidToApply);
                    count++;
                }
            }

            // 自动排毒：同一 uid 出现在两件物品上 = 铁证复制（uid 全局唯一不可能撞）。
            // 留先注册的、剥后到的并大声报警——复制 bug 下一轮恢复扫描自动清掉，不会固化进存档
            int detoxed = DetoxDuplicateUids();
            if (detoxed > 0) Warning($"Detox: stripped {detoxed} duplicate-uid affix(es)");

            // 这里原来有"按 prefab 兜底恢复"（isFinal 时贴给全场唯一同 prefab 无词缀物品），删了。
            // 实测两件相同潜水服互串词缀：同 prefab 多件时分不清"ID 漂移的原物"和"无辜的另一件"，
            // 而 ID 漂移时 tag 路径就能自愈（tag 跟着物品走进存档，不受 ID 影响）。带警告丢弃远好过复制

            if (isFinal && PendingAffixes.Count > 0)
            {
                Warning($"Expiring {PendingAffixes.Count} unrestored affixes (items absent this round, e.g. offline players' inventories): "
                    + string.Join(", ", PendingAffixes.Select(kv => $"{kv.Value.AffixId}@ID{kv.Key}")));
                PendingAffixes.Clear();
            }

            if (count > 0 || pruned > 0)
                Log($"Restored {count} affixes, pruned {pruned} incompatible (pending={PendingAffixes.Count}, items={items.Count(i => !i.Removed)})");
            else
                DebugLog($"No affixes restored: pending={PendingAffixes.Count}, items={items.Count(i => !i.Removed)}");
        }

        // 恢复时的多词缀校验：逗号列表逐项剔除——定义不存在/不再适用/奇迹结构非法/槽位违规。
        // 空列表 = 整组都不可恢复
        static List<string> ValidateAffixIdListForRestore(Item item, string idList)
        {
            var kept = new List<string>();
            foreach (var raw in idList.Split(','))
            {
                string id = raw.Trim();
                if (id.Length == 0) continue;
                if (!AffixDefs.TryGetValue(id, out var def))
                {
                    Warning($"Restore: unknown affix '{id}' on {item.Name} (ID={item.ID}), dropped");
                    continue;
                }
                if (!IsAffixApplicable(def, item))
                {
                    Warning($"Restore: affix '{id}' on {item.Name} (ID={item.ID}) no longer applicable, dropped");
                    continue;
                }
                kept.Add(id);
            }
            // 奇迹结构：额外词缀只能依附奇迹存在；主词缀不是奇迹时额外词缀全部剔除
            if (kept.Count > 1 && kept[0] != MIRACLE_AFFIX_ID)
            {
                Warning($"Restore: {item.Name} (ID={item.ID}) has extra affixes without '{MIRACLE_AFFIX_ID}' as primary, extras dropped");
                kept.RemoveRange(1, kept.Count - 1);
            }
            // 槽位规则复核（规则收窄时剔除违规额外词缀）
            if (kept.Count > 1)
            {
                bool aboveEpicSeen = false;
                for (int i = kept.Count - 1; i >= 1; i--)
                {
                    string reason = null;
                    if (kept.IndexOf(kept[i]) < i) reason = "duplicate affix";
                    else if (i > 2) reason = "extra slots full (max 2)";
                    else if (IsAboveEpic(AffixDefs[kept[i]].Tier) && aboveEpicSeen) reason = "only one Legendary/Special extra allowed";
                    if (reason != null)
                    {
                        Warning($"Restore: extra affix '{kept[i]}' on {item.Name} (ID={item.ID}) violates slot rule ({reason}), dropped");
                        kept.RemoveAt(i);
                        continue;
                    }
                    if (IsAboveEpic(AffixDefs[kept[i]].Tier)) aboveEpicSeen = true;
                }
            }
            return kept;
        }

        // 排毒：同 uid 只留先注册的一件（它更靠近权威来源），其余剥掉。词缀数 <2 直接返回
        static int DetoxDuplicateUids()
        {
            if (ItemAffixes.Count < 2) return 0;
            Dictionary<string, ushort> seen = null;
            List<ushort> toStrip = null;
            foreach (var kv in ItemAffixes)
            {
                if (kv.Value.Uid == null) continue; // 旧存档数据不可验证，跳过
                seen ??= new Dictionary<string, ushort>();
                if (seen.ContainsKey(kv.Value.Uid)) (toStrip ??= new List<ushort>()).Add(kv.Key);
                else seen[kv.Value.Uid] = kv.Key;
            }
            if (toStrip == null) return 0;

            int stripped = 0;
            var detoxSnapshot = SnapshotItems();
            foreach (var id in toStrip)
            {
                if (!ItemAffixes.TryGetValue(id, out var data)) continue;
                var item = FindItemById(id, detoxSnapshot);
                var oldDef = GetEffectiveDef(data);
                if (item != null && !item.Removed)
                {
                    UnregisterEffects(item, data.Effects);
                    RestoreStatChanges(data, item);
                    RemoveAffixTag(item);
                    Warning($"Detox: stripped DUPLICATE affix '{data.AffixId}' (uid={data.Uid}) from " +
                        $"{item.Name} (ID={id}) — identical uid on another item proves this is a copy");
                    RaiseAffixEvent(AffixRemoved, item, oldDef);
                }
                ItemAffixes.Remove(id);
                CooldownDamageState.Purge(id);
                stripped++;
            }
            return stripped;
        }

        static string SaveFilePath => Path.Combine(Package.Dir, "Data", "affix_save.xml");

        // 存档身份：GameSession.DataPath.SavePath（战役存档文件路径），反射解析一次
        static readonly PropertyInfo GameSessionDataPathProp =
            typeof(GameMain).Assembly.GetType("Barotrauma.GameSession")
                ?.GetProperty("DataPath", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo CampaignSavePathField =
            GameSessionDataPathProp?.PropertyType.GetField("SavePath", BindingFlags.Public | BindingFlags.Instance);

        // 当前存档身份（战役存档路径；无会话/非战役为空串）。同一进程退到主菜单再进另一个存档时，
        // 上一档的词缀绝不能恢复进来——物品 ID 只在存档内有意义，跨界恢复就是贴到无辜物品上
        static string CurrentSaveId
        {
            get
            {
                try
                {
                    object dp = GameSessionDataPathProp?.GetValue(GameMain.GameSession);
                    if (dp == null) return "";
                    return CampaignSavePathField?.GetValue(dp) as string ?? "";
                }
                catch { return ""; }
            }
        }

        object OnRoundEnd(object[] args)
        {
            // 巡回结束先把词缀写进桥接文件（在清内存表之前！）：附魔台附魔只在这里持久化，
            // 标签若在战役存档里丢了（堆叠/容器边界情况），下一巡回还能按 ID 兜底恢复。
            // 客户端不写：避免双上下文同时写同一个文件
            if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient)
            {
                SaveAffixData();
                DebugLog($"roundEnd: saved {ItemAffixes.Count} affixes to bridge file");
            }
            // 清内存表前强制重写全部标签：tickbox 类组件会整串擦 Tags，内存表清空后
            // tags 是 Item.Save 写 affixid 属性的唯一兜底来源
            StampAllAffixTags();
            savedDataLoaded = false;
            PendingAffixes.Clear();
            ItemAffixes.Clear();
            AffixEffectInjectionPatch.ClearProcTimers();
            throttledLogs.Clear(); // 节流日志键按物品 ID 生成，巡回结束清理防长期累积
            loggedOnceMessages.Clear(); // 同上是按物品 ID 生成的键，不清的话每附魔一件就多一条
            return null;
        }

        object OnRoundStart(object[] args)
        {
            // 先恢复 PendingAffixes 再删文件：恢复重试期间删了，晚于首次恢复加载的物品就彻底丢词缀
            LoadAffixData();
            try { File.Delete(SaveFilePath); } catch { }
            DebugLog($"roundStart: pending={PendingAffixes.Count}, affixed={ItemAffixes.Count}. Scheduling restores at 3s/10s/25s");
            // 主线程延迟调度，原来 Task.Delay 的线程池回调直接改游戏状态，有安全隐患。
            // 多次重试：多人客户端物品经网络陆续到位，单次 3 秒恢复会漏晚到的。RestoreAffixes 幂等，重试≈多扫一遍
            ScheduleOnMainThread(3.0, () => RestoreAffixes(false));
            ScheduleOnMainThread(10.0, () => RestoreAffixes(false));
            ScheduleOnMainThread(25.0, () => RestoreAffixes(true));
            return null;
        }

        public static bool IsAffixApplicable(AffixDef affix, Item item)
        {
            // 附魔石是万能载体：什么词缀都能往上附（在石头上只有展示/转移作用），不查适用性。
            // 顺带保证恢复路径不会清掉石头上的词缀
            if (item.HasTag(STONE_TAG)) return true;
            if (!IsAffixApplicableCore(affix, item)) return false;
            // 专一：只给当前单手握持的近战；已带标签的放行——恢复路径上它已被改成双手，不能再被这道门槛清掉
            if (affix.ForceTwoHanded
                && (item.Tags == null || !item.Tags.Contains(AFFIX_TAG_PREFIX + affix.Identifier))
                && !IsOneHandedMelee(item))
                return false;
            // 专注：只给当前单手握持的远程武器（已带词缀标签的放行，理由同专一）
            if (affix.ForceTwoHandedRanged
                && (item.Tags == null || !item.Tags.Contains(AFFIX_TAG_PREFIX + affix.Identifier))
                && !IsOneHandedRanged(item))
                return false;
            return true;
        }

        static bool IsAffixApplicableCore(AffixDef affix, Item item)
        {
            // 拓展点：订阅者返回 true/false 直接拍板（可放行非东方模组物品），null 走默认
            if (ApplicabilityOverride != null)
            {
                foreach (var d in ApplicabilityOverride.GetInvocationList())
                {
                    bool? r = null;
                    try { r = ((Func<AffixDef, Item, bool?>)d)(affix, item); }
                    catch (Exception ex) { Warning($"ApplicabilityOverride subscriber failed: {ex.Message}"); }
                    if (r.HasValue) return r.Value;
                }
            }
            // 只对东方模组物品生效；恢复/显示不走这条路不受影响，enchant 指令仍可强制绕过
            if (!IsTouhouModItem(item)) return false;
            if (affix.Applicable == "all") return true;
            // 吗啡这类也带 MeleeWeapon 组件能敲人，但本质是药：只给 medical 词缀
            bool isMedical = IsMedicalItem(item);
            var tags = affix.Applicable.Split(',');
            foreach (var tag in tags)
            {
                switch (tag.Trim().ToLowerInvariant())
                {
                    // 组件判定优先：金刚杵这类东方近战没有 weapon tag，服装挂 clothing tag 却有减伤——tag 不可靠
                    case "weapon":
                        if (!isMedical && (IsMeleeWeapon(item) || IsRangedWeapon(item) || item.HasTag("weapon"))) return true;
                        break;
                    case "meleeweapon":
                        if (!isMedical && IsMeleeWeapon(item)) return true;
                        break;
                    case "rangedweapon":
                        if (!isMedical && IsRangedWeapon(item)) return true;
                        break;
                    case "tool":
                        if (IsTool(item)) return true;
                        break;
                    case "medical":
                        if (isMedical) return true;
                        break;
                    case "armor":
                        // 护甲/穿戴词缀只给外套槽（潜水服/防弹衣），防止头饰/耳机槽多件叠效果
                        if (IsArmor(item) && IsOuterClothes(item)) return true;
                        break;
                    case "wearable":
                        if (IsPlainWearable(item) && IsOuterClothes(item)) return true;
                        break;
                }
            }
            return false;
        }

        static bool HasComponentNamed(Item item, params string[] typeNames)
        {
            if (item.Components == null) return false;
            foreach (var c in item.Components)
            {
                if (c != null && typeNames.Contains(c.GetType().Name)) return true;
            }
            return false;
        }

        // 东方模组物品判定：主用 identifier 白名单（扫描所有含"东方"的包构建；补丁覆盖定义后包会易主，
        // 但 identifier 不变），包名关键词兜底
        public static bool IsTouhouModItem(Item item)
        {
            if (item?.Prefab == null) return false;
            if (!touhouItemIdsBuilt) BuildTouhouItemIdSet();
            if (touhouItemIds.Contains(item.Prefab.Identifier.Value)) return true;
            string pkgName = item.Prefab.ContentPackage?.Name;
            return !string.IsNullOrEmpty(pkgName)
                && pkgName.IndexOf(TouhouPackageNameKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static HashSet<string> touhouItemIds;
        static bool touhouItemIdsBuilt;

        // 扫描所有含"东方"的包的 filelist.xml，收集全部物品 identifier，首次用时惰性构建
        static void BuildTouhouItemIdSet()
        {
            touhouItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            touhouItemIdsBuilt = true;
            try
            {
                foreach (var pkg in ContentPackageManager.RegularPackages)
                {
                    if (pkg?.Name == null) continue;
                    if (pkg.Name.IndexOf(TouhouPackageNameKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string filelist = Path.Combine(pkg.Dir, "filelist.xml");
                    if (!File.Exists(filelist)) continue;
                    XDocument listDoc;
                    try { listDoc = XDocument.Load(filelist); }
                    catch (Exception ex) { Warning($"TouhouItemIds: cannot read {filelist}: {ex.Message}"); continue; }
                    foreach (var itemFile in listDoc.Root.Elements("Item"))
                    {
                        string f = itemFile.Attribute("file")?.Value;
                        if (string.IsNullOrEmpty(f)) continue;
                        f = f.Replace("%ModDir%", pkg.Dir.TrimEnd('/', '\\'));
                        if (!File.Exists(f)) continue;
                        try
                        {
                            foreach (var el in XDocument.Load(f).Root.Descendants("Item"))
                            {
                                string id = el.Attribute("identifier")?.Value;
                                if (!string.IsNullOrEmpty(id)) touhouItemIds.Add(id);
                            }
                        }
                        catch (Exception ex) { Warning($"TouhouItemIds: cannot parse {f}: {ex.Message}"); }
                    }
                }
                LogOnce($"Touhou item whitelist built: {touhouItemIds.Count} identifiers");
            }
            catch (Exception ex)
            {
                Warning($"BuildTouhouItemIdSet failed: {ex.Message}");
            }
        }

        // 外套槽穿戴物优先用 descarmor
        public static string GetDescriptionFor(AffixDef def, Item item)
        {
            if (item != null && IsOuterClothes(item)
                && !string.IsNullOrEmpty(def.DisplayDescArmor))
                return def.DisplayDescArmor;
            return def.DisplayDesc;
        }

        // 药物判定：能在健康界面用且标了 medical；哪怕带 MeleeWeapon 组件也只算药，不算近战
        public static bool IsMedicalItem(Item item)
        {
            if (item?.Prefab == null) return false;
            if (!item.Prefab.UseInHealthInterface) return false;
            return item.HasTag("medical")
                || item.Prefab.Category.ToString().IndexOf("Medical", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsMeleeWeapon(Item item) =>
            HasComponentNamed(item, "MeleeWeapon") || item.HasTag("meleeweapon");

        public static bool IsRangedWeapon(Item item) =>
            HasComponentNamed(item, "RangedWeapon") || item.HasTag("rangedweapon") || item.HasTag("gun");

        // 工具：RepairTool 组件或带 tool tag；但元素腰带这类背包位穿戴容器也挂 tool tag，用 backpack 排除掉
        public static bool IsTool(Item item) =>
            HasComponentNamed(item, "RepairTool")
            || (item.HasTag("tool") && !item.HasTag("backpack"));

        // 有 Wearable 且带 damagemodifier = 有减伤的护甲（潜水服/防弹衣/角色服装）
        public static bool IsArmor(Item item) => TryGetWearableDamageModifierCount(item, out int n) && n > 0;

        // 单手近战：槽位含单手、不含双手组合槽
        public static bool IsOneHandedMelee(Item item)
        {
            if (!IsMeleeWeapon(item)) return false;
            if (item.Components == null) return false;
            foreach (var c in item.Components)
            {
                // 必须用 IsInstanceOfType：MeleeWeapon 是 Holdable 子类，精确比较会把近战全排除
                //（专一词条曾因此把所有近战判成"非单手"）
                if (c == null || ReflectionCache.HoldableType == null
                    || !ReflectionCache.HoldableType.IsInstanceOfType(c)) continue;
                if (ReflectionCache.HoldableAllowedSlotsProp?.GetValue(c) is not System.Collections.IEnumerable slots) continue;
                bool single = false, dual = false;
                foreach (InvSlotType s in slots)
                {
                    if (s == InvSlotType.RightHand || s == InvSlotType.LeftHand) single = true;
                    if (s == (InvSlotType.RightHand | InvSlotType.LeftHand)) dual = true;
                }
                return single && !dual;
            }
            return false;
        }

        public static bool HasChargeTime(Item item)
        {
            if (item?.Components == null) return false;
            var prop = ReflectionCache.RangedWeaponMaxChargeTimeProp;
            if (prop == null) return false;
            foreach (var c in item.Components)
            {
                if (c == null || c.GetType() != ReflectionCache.RangedWeaponType) continue;
                try
                {
                    if ((float)prop.GetValue(c) > 0f) return true;
                }
                catch { }
            }
            return false;
        }

        // 等候词条的门槛：蓄力和增伤同带时，只有真有蓄力的武器才吃增伤，不然非蓄力武器白嫖。
        // getter 补丁读的是乘过的值，>0 和原始值同真
        public static bool ChargeGatedDamageApplies(AffixDef def, Item item)
        {
            if (Math.Abs(def.ChargeTimeMult - 1f) < 0.0001f || Math.Abs(def.DamageMult - 1f) < 0.0001f)
                return true; // 无耦合：纯蓄力或纯增伤词缀不受门槛限制
            return HasChargeTime(item);
        }

        public static bool IsOneHandedRanged(Item item)
        {
            if (!IsRangedWeapon(item)) return false;
            if (item.Components == null) return false;
            foreach (var c in item.Components)
            {
                if (c == null || ReflectionCache.HoldableType == null
                    || !ReflectionCache.HoldableType.IsInstanceOfType(c)) continue;
                if (ReflectionCache.HoldableAllowedSlotsProp?.GetValue(c) is not System.Collections.IEnumerable slots) continue;
                bool single = false, dual = false;
                foreach (InvSlotType s in slots)
                {
                    if (s == InvSlotType.RightHand || s == InvSlotType.LeftHand) single = true;
                    if (s == (InvSlotType.RightHand | InvSlotType.LeftHand)) dual = true;
                }
                return single && !dual;
            }
            return false;
        }

        public static bool IsPlainWearable(Item item) => TryGetWearableDamageModifierCount(item, out int n) && n == 0;

        // 外套槽判定。护甲/穿戴词缀只给它，头饰耳机槽的小件不附，防叠效果。
        // 槽位是 [Flags] 且可组合（凭依武装占外套+头盔），列表里是合并值，必须按位判断——
        // 字符串等值比较会把 "OuterClothes, Head" 漏掉
        public static bool IsOuterClothes(Item item)
        {
            if (item.Components == null || ReflectionCache.WearableType == null) return false;
            foreach (var c in item.Components)
            {
                if (c == null || c.GetType() != ReflectionCache.WearableType) continue;
                if (ReflectionCache.WearableAllowedSlotsProp?.GetValue(c) is System.Collections.IEnumerable slots)
                {
                    foreach (var s in slots)
                    {
                        if (s is InvSlotType st && st.HasFlag(InvSlotType.OuterClothes)) return true;
                    }
                }
                return false;   // 有 Wearable 但不是外套槽
            }
            return false;
        }

        static bool TryGetWearableDamageModifierCount(Item item, out int count)
        {
            count = 0;
            if (item.Components == null || ReflectionCache.WearableType == null) return false;
            foreach (var c in item.Components)
            {
                if (c == null || c.GetType() != ReflectionCache.WearableType) continue;
                if (ReflectionCache.WearableDamageModifiersProp?.GetValue(c) is System.Collections.IEnumerable mods)
                {
                    foreach (var _ in mods) count++;
                }
                return true;
            }
            return false;
        }

        // 单词缀入口，等价 SetAffixes(item, [id], uid)。UID 优先级：调用方指定 → 物品已有标签
        // （重附魔保持身份，双上下文也戳同一个）→ 新生成
        public static void ApplyAffix(Item item, AffixDef affix, string uid = null)
        {
            SetAffixes(item, new List<string> { affix.Identifier }, uid);
        }

        // 多词缀统一入口：先拆旧组（注销效果/恢复属性/清标签）再整组上新。奇迹时合成 EffectiveDef——
        // 效果注册和属性修改按合成值单次执行，回滚也是单次，没有两个词缀抢同一属性的顺序问题
        public static void SetAffixes(Item item, List<string> affixIds, string uid = null)
        {
            string existingUid = ReadUidTag(item); // 先读——RemoveAffixTag 会把它一起清掉

            if (ItemAffixes.TryGetValue(item.ID, out var oldData))
            {
                RaiseAffixEvent(AffixRemoved, item, oldData.EffectiveDef);
                if (oldData.Effects != null) UnregisterEffects(item, oldData.Effects);
                RestoreStatChanges(oldData, item);
            }

            RemoveAffixTag(item);

            var defs = new List<AffixDef>(affixIds.Count);
            foreach (var id in affixIds)
            {
                if (AffixDefs.TryGetValue(id, out var d)) defs.Add(d);
            }
            if (defs.Count == 0)
            {
                // 新组全无效：内存条目必须一并清，不然显示/效果补丁还在读旧词缀
                ItemAffixes.Remove(item.ID);
                Warning($"SetAffixes: no valid affix in [{string.Join(",", affixIds)}], skipped {item.Name}");
                return;
            }

            AffixDef effective = defs.Count == 1 ? defs[0] : ComposeAffixDef(defs, item);

            var data = new AffixData
            {
                AffixId = defs[0].Identifier,
                Tier = effective.Tier,
                NamePrefix = defs[0].NamePrefix,
                DisplayColor = effective.DisplayColor,
                PrefabId = item.Prefab.Identifier.Value,
                Uid = uid ?? existingUid ?? NewAffixUid(),
                Effects = effective.Effects,
                EffectiveDef = effective
            };
            if (defs.Count > 1)
            {
                data.ExtraAffixIds = new List<string>();
                for (int i = 1; i < defs.Count; i++) data.ExtraAffixIds.Add(defs[i].Identifier);
            }
            BuildPrefixDisplay(data, defs);

            ItemAffixes[item.ID] = data;
            // 每个词缀各写一个标签 + 一个 UID 标签（RemoveAffixTag 刚清过，直接拼接即可，不必查重）
            string pairTags = BuildAffixTagsString(JoinAffixIds(data), data.Uid);
            item.Tags = string.IsNullOrEmpty(item.Tags) ? pairTags : item.Tags + "," + pairTags;

            RegisterEffectsForDisplay(item, effective);
            ApplyStatChanges(item, effective, data);
            RaiseAffixEvent(AffixApplied, item, effective);
            LogOnce($"Applied [{data.PlainPrefixDisplay}] ({effective.Tier}) to {item.Name} (ID={item.ID})");
        }

        static void BuildPrefixDisplay(AffixData data, List<AffixDef> defs)
        {
            var rich = new System.Text.StringBuilder();
            var plain = new System.Text.StringBuilder();
            foreach (var d in defs)
            {
                if (rich.Length > 0) { rich.Append(' '); plain.Append(' '); }
                rich.Append(Helpers.BracketedRichPrefix(d.DisplayPrefix));
                plain.Append('[').Append(Helpers.StripRichText(d.DisplayPrefix)).Append(']');
            }
            data.RichPrefixDisplay = rich.ToString();
            data.PlainPrefixDisplay = plain.ToString();
        }

        // 全部词缀 id（主词缀 + 奇迹额外）
        public static List<string> GetAllAffixIds(AffixData data)
        {
            if (data == null) return null;
            var ids = new List<string>(1 + (data.ExtraAffixIds?.Count ?? 0)) { data.AffixId };
            if (data.ExtraAffixIds != null) ids.AddRange(data.ExtraAffixIds);
            return ids;
        }

        // 条目的生效词缀（奇迹为合成值），给不走 TryGetAffix 的读取点用
        public static AffixDef GetEffectiveDef(AffixData data)
        {
            if (data == null) return null;
            if (data.EffectiveDef != null) return data.EffectiveDef;
            return AffixDefs.TryGetValue(data.AffixId, out var def) ? def : null;
        }

        // 逗号连接格式，存档属性/桥接文件/网络消息共用
        public static string JoinAffixIds(AffixData data) => string.Join(",", GetAllAffixIds(data));

        // 逗号 id 列表 → 完整标签串。附魔/网络镜像/标签自愈三处共用，格式只有这一个出处
        public static string BuildAffixTagsString(string joinedIds, string uid)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var raw in joinedIds.Split(','))
            {
                string id = raw.Trim();
                if (id.Length == 0) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(AFFIX_TAG_PREFIX).Append(id);
            }
            if (uid != null) sb.Append(',').Append(AFFIX_UID_TAG_PREFIX).Append(uid);
            return sb.ToString();
        }

        public static bool IsAboveEpic(string tier) => tier == "Legendary" || tier == "Special";

        // 奇迹额外槽校验：最多 2 个、不重复、不能再加奇迹、传说/特殊档最多 1 个
        public static bool CanAddExtraAffix(AffixData data, AffixDef def, out string reason)
        {
            reason = null;
            if (data == null || data.AffixId != MIRACLE_AFFIX_ID)
            {
                reason = "物品没有奇迹词缀";
                return false;
            }
            if (def.Identifier == MIRACLE_AFFIX_ID)
            {
                reason = "奇迹不能再作为额外词缀";
                return false;
            }
            int count = data.ExtraAffixIds?.Count ?? 0;
            if (count >= 2)
            {
                reason = "两个额外词缀槽已满";
                return false;
            }
            if (data.AffixId == def.Identifier || (data.ExtraAffixIds?.Contains(def.Identifier) ?? false))
            {
                reason = "物品已有相同词缀";
                return false;
            }
            if (IsAboveEpic(def.Tier) && data.ExtraAffixIds != null)
            {
                foreach (var id in data.ExtraAffixIds)
                {
                    if (AffixDefs.TryGetValue(id, out var ex) && IsAboveEpic(ex.Tier))
                    {
                        reason = "传说/特殊档词缀最多一个";
                        return false;
                    }
                }
            }
            return true;
        }

        // 奇迹合成：多词缀并成一个运行时 AffixDef，倍率乘算、概率并集、布尔取或、荆棘加算。
        // 蓄力耦合（等候）在这里逐部件烘焙，合成结果带 IsComposite，伤害补丁见到直接放行不再过门槛。
        // 不进 AffixDefs，只挂 AffixData.EffectiveDef
        public static AffixDef ComposeAffixDef(List<AffixDef> parts, Item item)
        {
            var c = new AffixDef
            {
                Identifier = string.Join(",", parts.ConvertAll(p => p.Identifier)),
                IsComposite = true,
                Tier = parts[0].Tier,
                DisplayColor = parts[0].DisplayColor,
                NamePrefix = parts[0].NamePrefix,
                Applicable = parts[0].Applicable,
                Effects = new List<StatusEffect>()
            };
            float noAmmoSave = 1f, noDurabilitySave = 1f;
            float cooldownMult = 1f, cooldownInterval = 0f;
            float massMult = 1f, massThreshold = float.MaxValue;
            float thornsStunMiss = 1f;
            foreach (var p in parts)
            {
                // 蓄力耦合逐部件判定：部件同时带蓄力+增伤且物品无蓄力 → 该部件增伤不进合成
                bool chargeGated = Math.Abs(p.ChargeTimeMult - 1f) > 0.0001f
                    && Math.Abs(p.DamageMult - 1f) > 0.0001f
                    && item != null && !HasChargeTime(item);
                if (!chargeGated) c.DamageMult *= p.DamageMult;
                c.FireRateMult *= p.FireRateMult;
                c.DamageTakenMult *= p.DamageTakenMult;
                c.FuelConsumeMult *= p.FuelConsumeMult;
                c.SpreadMult *= p.SpreadMult;
                c.SkillReqMult *= p.SkillReqMult;
                noAmmoSave *= 1f - p.AmmoSaveChance;
                noDurabilitySave *= 1f - p.DurabilitySaveChance;
                c.StructureFixMult *= p.StructureFixMult;
                c.DeviceRepairMult *= p.DeviceRepairMult;
                if (p.CooldownDamageMult > 1f)
                {
                    cooldownMult *= p.CooldownDamageMult;
                    cooldownInterval = Math.Max(cooldownInterval, p.CooldownDamageInterval);
                }
                c.LastShotDamageMult *= p.LastShotDamageMult;
                c.ForceTwoHanded |= p.ForceTwoHanded;
                c.SlotOneDamageMult *= p.SlotOneDamageMult;
                c.SlotOneFireRateMult *= p.SlotOneFireRateMult;
                c.NoiseMult *= p.NoiseMult;
                c.NoAimWobble |= p.NoAimWobble;
                c.NoMovePenalty |= p.NoMovePenalty;
                c.ThornsBleeding += p.ThornsBleeding;
                c.ThornsLacerations += p.ThornsLacerations;
                if (p.ThornsStun > 0f)
                {
                    c.ThornsStun += p.ThornsStun;
                    // 眩晕概率按"都不触发"的补数累积（两件各 50% → 75%），语义与 ammo/durability 概率一致
                    thornsStunMiss *= 1f - p.ThornsStunChance;
                }
                if (p.ThornsBleeding > 0f || p.ThornsLacerations > 0f || p.ThornsStun > 0f)
                    c.ThornsInterval = Math.Min(c.ThornsInterval, p.ThornsInterval);
                c.ForceTwoHandedRanged |= p.ForceTwoHandedRanged;
                if (p.MassBonusMult > 1f)
                {
                    massMult *= p.MassBonusMult;
                    massThreshold = Math.Min(massThreshold, p.MassThreshold);
                }
                c.ChargeTimeMult *= p.ChargeTimeMult;
                c.FirstShotDamageMult *= p.FirstShotDamageMult;
                c.LoneWolfDamageMult *= p.LoneWolfDamageMult;
                if (TierRank(p.Tier) > TierRank(c.Tier)) { c.Tier = p.Tier; c.DisplayColor = p.DisplayColor; }
                if (p.Effects != null) c.Effects.AddRange(p.Effects);
            }
            c.AmmoSaveChance = 1f - noAmmoSave;
            c.DurabilitySaveChance = 1f - noDurabilitySave;
            c.ThornsStunChance = 1f - thornsStunMiss; // 无眩晕部件时为 0，ThornsStun=0 时本就不触发
            c.CooldownDamageMult = cooldownMult;
            if (cooldownInterval > 0f) c.CooldownDamageInterval = cooldownInterval;
            c.MassBonusMult = massMult;
            if (massThreshold < float.MaxValue) c.MassThreshold = massThreshold;
            return c;
        }

        static int TierRank(string tier) => tier switch
        {
            "Broken" => 0,
            "Normal" => 1,
            "Rare" => 2,
            "Epic" => 3,
            "Legendary" => 4,
            "Special" => 5,
            _ => 1
        };

        // 附魔广播要反射解析的 Send/ConnectedClients 成员：Networking 的类型运行时才定得了，
        // 静态构造里解析不到，首次调用时缓存一次
        static MethodInfo netSendToConn;
        static PropertyInfo netConnectedClientsProp;

        // 附魔后的同步：①本地开服时服务器/客户端是两个隔离脚本上下文但共享同一实体列表，
        // 把标签打到所有同 ID 实例上，另一边的显示/效果补丁就能读到；
        // ②广播给远端客户端（走兼容层 Send，编译期调用，不用反射猜重载）
        public static void BroadcastAffixApplied(Item item, AffixDef affix)
        {
            try
            {
                if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsServer) return;

                // UID 和完整词缀列表从内存表取（SetAffixes 已先跑完），镜像标签和网络消息都带上。
                // 奇迹时 affixId 是逗号连的全列表
                string joinedIds = affix.Identifier;
                string uid = null;
                if (ItemAffixes.TryGetValue(item.ID, out var bdata))
                {
                    joinedIds = JoinAffixIds(bdata);
                    uid = bdata.Uid;
                }
                string pairTags = BuildAffixTagsString(joinedIds, uid);
                int mirrored = 0;
                var mirrorSnapshot = SnapshotItems();
                if (mirrorSnapshot == null) return; // 加载线程正在改动物品列表，放弃本次镜像（客户端由网络消息覆盖）
                foreach (var it in mirrorSnapshot)
                {
                    if (it == null || it.Removed || it.ID != item.ID || ReferenceEquals(it, item)) continue;
                    // 无条件清旧重戳：重复附魔同一词缀时 uid 也会变，旧标签不清则
                    // 客户端仍显示旧词缀、存档写出旧 uid
                    RemoveAffixTag(it);
                    it.Tags = string.IsNullOrEmpty(it.Tags) ? pairTags : it.Tags + "," + pairTags;
                    mirrored++;
                }
                if (mirrored > 0)
                    DebugLog($"Net: mirrored [{joinedIds}] to {mirrored} same-ID instance(s) for local client");

                var net = LuaCsSetup.Instance.Networking;

                // 双端签名完全不同：客户端 Send(IWriteMessage, DeliveryMethod) 是发给服务器，服务端只有
                // Send(msg, NetworkConnection, DeliveryMethod) 单播。Shared 代码两头编译，直接调必有一端编不过，只能全程反射。
                // 广播 = 遍历 ConnectedClients 逐个单播，每条消息重新 Start，避免复用已消费的缓冲区
                var sendToConn = netSendToConn ??= net.GetType().GetMethods().FirstOrDefault(m =>
                    m.Name == "Send" && m.GetParameters() is { Length: 3 } p &&
                    p[1].ParameterType.Name == "NetworkConnection" && p[2].ParameterType.IsEnum);
                var clients = (netConnectedClientsProp ??= GameMain.NetworkMember.GetType()
                    .GetProperty("ConnectedClients"))?.GetValue(GameMain.NetworkMember) as System.Collections.IEnumerable;
                if (sendToConn != null && clients != null)
                {
                    object reliable = Enum.Parse(sendToConn.GetParameters()[2].ParameterType, "Reliable");
                    int sent = 0;
                    foreach (var client in clients)
                    {
                        if (client == null) continue;
                        var conn = client.GetType().GetProperty("Connection")?.GetValue(client);
                        if (conn == null) continue;
                        var m2 = net.Start(NET_APPLY_AFFIX);
                        m2.WriteUInt16(item.ID);
                        m2.WriteString(joinedIds);
                        // 带上 prefab 标识：客户端若因 ID 复用/错位找到的是另一个物品，凭此拒绝误附魔
                        m2.WriteString(item.Prefab.Identifier.Value);
                        m2.WriteString(uid ?? "");
                        sendToConn.Invoke(net, new object[] { m2, conn, reliable });
                        sent++;
                    }
                    DebugLog($"Net: broadcast [{joinedIds}] for {item.Name} (ID={item.ID}) unicast to {sent} client(s)");
                    return;
                }

                // 兜底：少数版本可能存在 Send(msg, DeliveryMethod) 广播或单参 Send
                var msg = net.Start(NET_APPLY_AFFIX);
                msg.WriteUInt16(item.ID);
                msg.WriteString(joinedIds);
                msg.WriteString(item.Prefab.Identifier.Value);
                msg.WriteString(uid ?? "");
                var send = net.GetType().GetMethods().FirstOrDefault(m =>
                    m.Name == "Send" && m.GetParameters() is { Length: 2 } p &&
                    p[1].ParameterType.IsEnum && p[1].ParameterType.Name == "DeliveryMethod");
                if (send != null)
                {
                    object reliable = Enum.Parse(send.GetParameters()[1].ParameterType, "Reliable");
                    send.Invoke(net, new[] { msg, reliable });
                    DebugLog($"Net: broadcast [{joinedIds}] for {item.Name} (ID={item.ID}) via Send+DeliveryMethod");
                }
                else
                {
                    var send1 = net.GetType().GetMethods().FirstOrDefault(m =>
                        m.Name == "Send" && m.GetParameters().Length == 1);
                    if (send1 != null)
                    {
                        send1.Invoke(net, new object[] { msg });
                        DebugLog($"Net: broadcast [{joinedIds}] for {item.Name} (ID={item.ID}) via Send");
                    }
                    else Warning("BroadcastAffixApplied: no usable Networking.Send overload");
                }
            }
            catch (Exception ex)
            {
                Warning($"BroadcastAffixApplied failed: {ex.Message}");
            }
        }

        // 优先走兼容接口的 LuaCsAction（跨版本稳）；老版本没有就表达式树照 Receive 委托的真实签名动态构造
        static void RegisterNetReceiver()
        {
            try
            {
                var net = LuaCsSetup.Instance.Networking;
                if (net is Barotrauma.LuaCs.Compatibility.ILuaCsNetworking compat)
                {
                    compat.Receive(NET_APPLY_AFFIX, (LuaCsAction)(args => HandleApplyAffixMessage(args)));
                    LogOnce($"Net: receiver [{NET_APPLY_AFFIX}] registered via ILuaCsNetworking");
                    return;
                }

                var recv = net.GetType().GetMethods().FirstOrDefault(m =>
                    m.Name == "Receive" && m.GetParameters() is { Length: 2 } p &&
                    p[0].ParameterType == typeof(string) && p[1].ParameterType.IsSubclassOf(typeof(Delegate)));
                if (recv == null)
                {
                    Warning("RegisterNetReceiver: no usable Networking.Receive overload");
                    return;
                }
                var delType = recv.GetParameters()[1].ParameterType;
                var invoke = delType.GetMethod("Invoke");
                var ps = invoke.GetParameters().Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name ?? "p")).ToArray();
                var arr = System.Linq.Expressions.Expression.NewArrayInit(typeof(object),
                    ps.Select(p => System.Linq.Expressions.Expression.Convert(p, typeof(object))));
                var call = System.Linq.Expressions.Expression.Call(
                    typeof(Mod).GetMethod(nameof(HandleApplyAffixMessage),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
                    arr);
                var del = System.Linq.Expressions.Expression.Lambda(delType, call, ps).Compile();
                recv.Invoke(net, new object[] { NET_APPLY_AFFIX, del });
                LogOnce($"Net: receiver [{NET_APPLY_AFFIX}] registered via reflection ({delType.Name})");
            }
            catch (Exception ex)
            {
                Warning($"RegisterNetReceiver failed: {ex.Message}");
            }
        }

        static void HandleApplyAffixMessage(object[] args)
        {
            try
            {
                if (args == null || args.Length == 0)
                {
                    Warning("Net: received affix message with no args");
                    return;
                }
                if (args[0] is not Barotrauma.Networking.IReadMessage msg)
                {
                    Warning($"Net: received affix message with unexpected arg type {args[0]?.GetType().Name ?? "null"}");
                    return;
                }
                ushort itemId = msg.ReadUInt16();
                string affixId = msg.ReadString();
                string prefabId = msg.ReadString();
                string uid = msg.ReadString();
                var item = FindItemById(itemId);
                DebugLog($"Net: received affix [{affixId}] for itemId={itemId}, itemFound={item != null && !item.Removed}");
                if (item == null || item.Removed) return;
                // ID 错位防护：客户端复用了该 ID 时找到的是另一个物品，不能把词缀安上去
                if (!string.IsNullOrEmpty(prefabId) && item.Prefab.Identifier.Value != prefabId)
                {
                    Warning($"Net: rejected affix [{affixId}] for itemId={itemId}: prefab mismatch " +
                        $"({item.Prefab.Identifier.Value} != {prefabId}), item is not the enchanted one");
                    return;
                }
                // 只有"已是同一组词缀"才跳过；换新词缀必须走 SetAffixes 完整替换流程，
                // 不然客户端永远停在第一次附魔的状态
                if (TryGetAffixData(item, out var existing) && JoinAffixIds(existing) == affixId)
                {
                    DebugLog($"Net: item {item.Name} (ID={item.ID}) already has affix [{affixId}], skipping");
                    return;
                }
                // affixId 可能是逗号连接的多词缀列表（奇迹）；逐个校验存在性
                var ids = new List<string>();
                foreach (var raw in affixId.Split(','))
                {
                    string id = raw.Trim();
                    if (id.Length == 0) continue;
                    if (AffixDefs.ContainsKey(id)) ids.Add(id);
                    else Warning($"Net: unknown affix identifier [{id}] in [{affixId}], skipped");
                }
                if (ids.Count > 0)
                {
                    // 服务端权威下发实例 UID，客户端镜像与存档写入都用它；空串（旧版本消息）走生成
                    SetAffixes(item, ids, string.IsNullOrEmpty(uid) ? null : uid);
                    DebugLog($"Net: applied [{affixId}] to {item.Name} (ID={item.ID})");
                }
            }
            catch (Exception ex)
            {
                Warning($"HandleApplyAffixMessage failed: {ex.Message}");
            }
        }

        // 组件级属性词条：射速 Reload÷倍率、散布 Spread×倍率、技能需求 Level×倍率、修船壳速度×倍率。
        // 原值记进 AffixData 移除时恢复——这些值不序列化，不恢复就是污染。
        // 幂等标记：本地开服双上下文共享同一物品实例，两边都会走 ApplyAffix，
        // 没这层拦截实例属性会被乘两次（恢复只恢复一次，残留还反过来污染原值）
        static readonly ConditionalWeakTable<Item, object> StatAppliedItems = new();

        static void ApplyStatChanges(Item item, AffixDef affix, AffixData data)
        {
            if (item.Components == null) return;
            bool hasFireRate = Math.Abs(affix.FireRateMult - 1f) >= 0.0001f;
            bool hasSpread = Math.Abs(affix.SpreadMult - 1f) >= 0.0001f;
            bool hasSkillReq = Math.Abs(affix.SkillReqMult - 1f) >= 0.0001f;
            bool hasStructureFix = Math.Abs(affix.StructureFixMult - 1f) >= 0.0001f;
            bool hasTwoHanded = affix.ForceTwoHanded || affix.ForceTwoHandedRanged;
            if (!hasFireRate && !hasSpread && !hasSkillReq && !hasStructureFix && !hasTwoHanded) return;
            if (StatAppliedItems.TryGetValue(item, out _)) return; // 另一上下文已改过一次，跳过

            foreach (var component in item.Components)
            {
                if (component == null) continue;
                var compType = component.GetType();

                // 射速词条：Reload ÷ 倍率（属性引用已缓存，不按名字反射）
                if (hasFireRate)
                {
                    var prop = compType == ReflectionCache.MeleeWeaponType ? ReflectionCache.MeleeWeaponReloadProp
                             : compType == ReflectionCache.RangedWeaponType ? ReflectionCache.RangedWeaponReloadProp
                             : null;
                    TryApplyPropChange(data, prop, component, v => v / affix.FireRateMult, "firerate", item);
                }

                // 散布词条：RangedWeapon.Spread / UnskilledSpread 乘算（<1 更精准）
                if (hasSpread && compType == ReflectionCache.RangedWeaponType)
                {
                    TryApplyPropChange(data, ReflectionCache.RangedWeaponSpreadProp, component, v => v * affix.SpreadMult, "spread", item);
                    TryApplyPropChange(data, ReflectionCache.RangedWeaponUnskilledSpreadProp, component, v => v * affix.SpreadMult, "spread", item);
                }

                // 巧匠词条（船体）：RepairTool.StructureFixAmount / LevelWallFixAmount 乘算 = 修船壳更快
                if (hasStructureFix && compType == ReflectionCache.RepairToolType)
                {
                    TryApplyPropChange(data, ReflectionCache.RepairToolStructureFixProp, component, v => v * affix.StructureFixMult, "structurefix", item);
                    TryApplyPropChange(data, ReflectionCache.RepairToolLevelWallFixProp, component, v => v * affix.StructureFixMult, "structurefix", item);
                }

                // 专一：单手近战改双手握持。AllowedSlots 是 get-only 列表只能原地改：
                // 留 Any 让背包格能放，去掉单手槽、加双手组合槽，原内容记进 SlotChanges
                if (hasTwoHanded && ReflectionCache.HoldableType != null &&
                    ReflectionCache.HoldableType.IsInstanceOfType(component) &&
                    ReflectionCache.HoldableAllowedSlotsProp?.GetValue(component) is List<InvSlotType> slots)
                {
                    bool dual = slots.Contains(InvSlotType.RightHand | InvSlotType.LeftHand);
                    bool single = slots.Contains(InvSlotType.RightHand) || slots.Contains(InvSlotType.LeftHand);
                    if (!dual && single)
                    {
                        (data.SlotChanges ??= new List<(List<InvSlotType>, List<InvSlotType>)>())
                            .Add((slots, new List<InvSlotType>(slots)));
                        slots.Remove(InvSlotType.RightHand);
                        slots.Remove(InvSlotType.LeftHand);
                        if (!slots.Contains(InvSlotType.Any)) slots.Add(InvSlotType.Any);
                        slots.Add(InvSlotType.RightHand | InvSlotType.LeftHand);
                    }
                }

                // 技能需求：只乘组件实例的 RequiredSkills，绝不能碰 prefab 的 SkillRequirementHint——全类型共享的显示数据
                if (hasSkillReq && component.RequiredSkills != null)
                {
                    try
                    {
                        foreach (var skill in component.RequiredSkills)
                        {
                            if (skill == null) continue;
                            (data.SkillChanges ??= new List<(object, float)>()).Add((skill, skill.Level));
                            skill.Level *= affix.SkillReqMult;
                        }
                    }
                    catch (Exception ex)
                    {
                        Warning($"Failed to apply skillreq to {item.Name}: {ex.Message}");
                    }
                }
            }
            StatAppliedItems.Add(item, null); // 标记本实例已改，另一上下文/重复调用直接跳过
        }

        // 读原值→记录→写新值，移除词缀时按记录恢复
        static void TryApplyPropChange(AffixData data, PropertyInfo prop, object target,
            Func<float, float> mutator, string what, Item item)
        {
            if (prop == null || !prop.CanRead || !prop.CanWrite) return;
            try
            {
                float original = (float)prop.GetValue(target);
                (data.PropChanges ??= new List<(PropertyInfo, object, float)>()).Add((prop, target, original));
                prop.SetValue(target, mutator(original));
            }
            catch (Exception ex)
            {
                Warning($"Failed to apply {what} to {item.Name}: {ex.Message}");
            }
        }

        static void RestoreStatChanges(AffixData data, Item item)
        {
            if (data == null) return;
            // 只有本上下文真改过才移除幂等标记：另一上下文没改过值，误删标记会放进后续重复应用
            bool hadChanges = (data.PropChanges?.Count > 0) || (data.SkillChanges?.Count > 0) || (data.SlotChanges?.Count > 0);
            if (data.PropChanges != null)
            {
                foreach (var (prop, target, original) in data.PropChanges)
                {
                    try { prop.SetValue(target, original); }
                    catch { }
                }
                data.PropChanges.Clear();
            }
            if (data.SkillChanges != null)
            {
                foreach (var (skillObj, original) in data.SkillChanges)
                {
                    try
                    {
                        if (skillObj is Skill skill) skill.Level = original;
                    }
                    catch { }
                }
                data.SkillChanges.Clear();
            }
            if (data.SlotChanges != null)
            {
                foreach (var (target, original) in data.SlotChanges)
                {
                    try { target.Clear(); target.AddRange(original); }
                    catch { }
                }
                data.SlotChanges.Clear();
            }
            // 优先词条的条件射速：移除词缀时若在 1 号位加成生效中，一并恢复
            RestoreSlotFireRate(data);
            data.SlotBonusActive = false;
            if (hadChanges && item != null) StatAppliedItems.Remove(item);
        }

        // 是否在最左快捷栏。槽位索引不固定（原版人类槽 0 是 ID 卡、Any 区从 8 开始，模组角色还会重排），
        // 取"第一个 Any 槽"就当 1 号位
        public static bool IsInHotbarSlotOne(Item item)
        {
            if (item?.ParentInventory is not CharacterInventory inv) return false;
            int slotCount = Math.Min(inv.Capacity, inv.SlotTypes.Length);
            for (int i = 0; i < slotCount; i++)
            {
                if (inv.SlotTypes[i] == InvSlotType.Any) return inv.IsInSlot(item, i);
            }
            return false;
        }

        // 冷静词条：手持或外套槽穿戴带 noisemult 的物品即生效，多件乘算叠加。
        // SoundRange setter 每帧×每个 AI 都跑，同一角色同一帧只算一次（按 Timing.TotalTime 记帧）
        sealed class NoiseMultCacheEntry { public double Time = double.MinValue; public float Mult = 1f; }
        static readonly ConditionalWeakTable<Character, NoiseMultCacheEntry> noiseMultCache = new();

        public static float GetNoiseMultFor(Character c)
        {
            if (!AnyNoiseAffixes || ItemAffixes.Count == 0 || c == null) return 1f;
            double now = Timing.TotalTime;
            var entry = noiseMultCache.GetValue(c, _ => new NoiseMultCacheEntry());
            if (entry.Time == now) return entry.Mult;
            float mult = ComputeNoiseMultFor(c);
            entry.Time = now;
            entry.Mult = mult;
            return mult;
        }

        static float ComputeNoiseMultFor(Character c)
        {
            float mult = 1f;
            if (c.HeldItems != null)
            {
                foreach (var held in c.HeldItems)
                {
                    // 读生效词缀（奇迹物品为合成值：额外词缀的降噪也要算）
                    if (TryGetAffixData(held, out var hd) &&
                        GetEffectiveDef(hd) is { } hdef && hdef.NoiseMult < 1f)
                    {
                        mult *= hdef.NoiseMult;
                    }
                }
            }
            if (c.Inventory is CharacterInventory inv)
            {
                // 只算装备槽，手持/背包格不算；多槽位穿戴物每个占位槽都会出现一次，同一件只乘一次
                List<Item> seen = null;
                int slotCount = Math.Min(inv.Capacity, inv.SlotTypes.Length);
                for (int i = 0; i < slotCount; i++)
                {
                    var slotType = inv.SlotTypes[i];
                    if (slotType == InvSlotType.Any || slotType == InvSlotType.None ||
                        slotType.HasFlag(InvSlotType.LeftHand) || slotType.HasFlag(InvSlotType.RightHand))
                    {
                        continue;
                    }
                    var worn = inv.GetItemAt(i);
                    if (worn == null) continue;
                    if (seen != null && seen.Contains(worn)) continue;
                    if (TryGetAffixData(worn, out var wd) &&
                        GetEffectiveDef(wd) is { } wdef && wdef.NoiseMult < 1f)
                    {
                        (seen ??= new List<Item>(2)).Add(worn);
                        mult *= wdef.NoiseMult;
                    }
                }
            }
            return mult;
        }

        // 优先词条：条件射速，进出 1 号快捷栏时应用/恢复 Reload

        static double nextSlotOneCheck;

        static double nextLoneWolfCheck;

        // 单打独斗：0.25s 查一次。手部+快捷栏范围内只有这一把附魔武器才激活；
        // 只扫固定槽位（≤12 格），不遍历背包
        public static void TickLoneWolfBonuses()
        {
            if (!IsGameplayAuthority) return; // 单上下文执行：双上下文会把条件增伤状态机跑两遍
            if (!AnyLoneWolfAffixes || ItemAffixes.Count == 0) return;
            if (Timing.TotalTime < nextLoneWolfCheck) return;
            nextLoneWolfCheck = Timing.TotalTime + 0.25;

            foreach (var kv in ItemAffixes.ToList()) // 快照：TryGetAffixData 可能清除过期条目
            {
                // 读生效词缀（奇迹物品为合成值：额外词缀的 lonewolf 词条也要参与状态机）
                var def = GetEffectiveDef(kv.Value);
                if (def == null || def.LoneWolfDamageMult <= 1f) continue;
                var item = Entity.FindEntityByID(kv.Key) as Item;
                if (item == null || item.Removed) continue;
                if (!TryGetAffixData(item, out var data) || data != kv.Value) continue; // 过期条目已被清除

                bool active = false;
                if (item.ParentInventory?.Owner is Character owner
                    && owner.Inventory is CharacterInventory inv)
                {
                    int affixedWeapons = 0;
                    bool thisIsIn = false;
                    Item lastCounted = null; // 双手武器同时占用左右手槽，同一物品只数一次
                    int slotCount = Math.Min(inv.Capacity, inv.SlotTypes.Length);
                    for (int i = 0; i < slotCount; i++)
                    {
                        var slotType = inv.SlotTypes[i];
                        // 只看快捷栏（Any）与手部槽——背包格不算"携带备战"
                        if (slotType != InvSlotType.Any
                            && !slotType.HasFlag(InvSlotType.LeftHand) && !slotType.HasFlag(InvSlotType.RightHand))
                            continue;
                        var slotItem = inv.GetItemAt(i);
                        if (slotItem == null || slotItem == lastCounted) continue;
                        if (!IsMeleeWeapon(slotItem) && !IsRangedWeapon(slotItem) && !slotItem.HasTag("weapon")) continue;
                        if (!TryGetAffixData(slotItem, out _)) continue;
                        lastCounted = slotItem;
                        affixedWeapons++;
                        if (slotItem == item) thisIsIn = true;
                    }
                    active = thisIsIn && affixedWeapons == 1;
                }
                // 状态变化时打诊断日志：方便定位"没生效"是条件不满足还是伤害管线问题
                if (active != data.LoneWolfActive)
                {
                    string reason;
                    if (item.ParentInventory?.Owner is not Character)
                        reason = "武器不在任何角色身上";
                    else if (active)
                        reason = "快捷栏+手部只有这一把附魔武器";
                    else
                        reason = "快捷栏/手部存在其他附魔武器，或武器不在快捷栏与手部";
                    DebugLog($"[lonewolf] {item.Name} (ID={item.ID}): {(active ? "激活" : "失效")}——{reason}");
                }
                data.LoneWolfActive = active;
            }
        }

        static double nextTagHealCheck;

        // 标签自愈：充能开关/电箱 tickbox 的 tags setvalue 会整串重写 Tags 把 __affix_ 抹掉。
        // 标签不是权威载体（内存表+存档 affixid 才是）但是兜底通道，必须能用。5 秒扫一遍缺了就补
        public static void TickAffixTagSelfHeal()
        {
            if (ItemAffixes.Count == 0) return;
            if (Timing.TotalTime < nextTagHealCheck) return;
            nextTagHealCheck = Timing.TotalTime + 5.0;
            StampAllAffixTags();
        }

        // 立即重写全部词缀标签。除 5s 自愈外，巡回结束清内存表前、每次存档写入前也强制调：
        // 这些时刻之后内存表可能不可用，tags 是 affixid 存档属性的唯一兜底来源，不最新就丢词缀
        public static void StampAllAffixTags()
        {
            var items = SnapshotItems();
            if (items == null) return; // 快照失败（加载线程正在改动物品列表），放弃本轮

            // 先按 ID 归组一次：同 ID 的多实例都要补，逐条词缀扫全表太伤
            var byId = new Dictionary<ushort, List<Item>>();
            foreach (var item in items)
            {
                if (item == null || item.Removed) continue;
                if (!byId.TryGetValue(item.ID, out var list)) byId[item.ID] = list = new List<Item>();
                list.Add(item);
            }

            foreach (var kv in ItemAffixes.ToList()) // 快照：TryGetAffixData 可能清除过期条目
            {
                // 期望的完整标签串（多词缀时每词缀一个标签 + 一个 UID 标签），逐个检查缺失
                string pairTags = BuildAffixTagsString(JoinAffixIds(kv.Value), kv.Value.Uid);
                string[] expected = pairTags.Split(',');
                // 同 BroadcastAffixApplied：双上下文共享实体列表，所有同 ID 实例都要补，不然另一边读不到
                if (!byId.TryGetValue(kv.Key, out var matches)) continue;
                foreach (var item in matches)
                {
                    if (!TryGetAffixData(item, out var data) || data != kv.Value) break; // 过期条目已被清除
                    bool allPresent = true;
                    foreach (var want in expected)
                    {
                        bool found = false;
                        foreach (var t in item.GetTags())
                        {
                            if (t.Value.Equals(want, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                        }
                        if (!found) { allPresent = false; break; }
                    }
                    if (allPresent) continue;
                    RemoveAffixTag(item); // 先清残留的旧词缀标签，再补当前词缀组 + UID（成对）
                    item.Tags = string.IsNullOrEmpty(item.Tags) ? pairTags : item.Tags + "," + pairTags;
                    Log($"Self-healed affix tag '{pairTags}' on {item.Name} (ID={item.ID}): tag was wiped (e.g. by a component's tags effect)");
                }
            }
        }

        // 槽位监视：0.25s 扫一遍（进出快捷栏不用帧级精度），状态翻转才读写组件属性
        public static void TickSlotOneBonuses()
        {
            if (!IsGameplayAuthority) return; // 单上下文执行：双上下文会把条件射速修改应用两遍（Reload ÷ 倍率两次）
            if (!AnySlotOneAffixes || ItemAffixes.Count == 0) return;
            if (Timing.TotalTime < nextSlotOneCheck) return;
            nextSlotOneCheck = Timing.TotalTime + 0.25;

            foreach (var kv in ItemAffixes.ToList()) // 快照：TryGetAffixData 可能清除过期条目
            {
                // 读生效词缀（奇迹物品为合成值：额外词缀的 slot1 词条也要参与状态机）
                var def = GetEffectiveDef(kv.Value);
                if (def == null) continue;
                // 伤害侧加成读同一个激活标记，只有 slot1damagemult 的词缀也要跑状态机
                if (Math.Abs(def.SlotOneFireRateMult - 1f) < 0.0001f && def.SlotOneDamageMult <= 1f) continue;
                var item = Entity.FindEntityByID(kv.Key) as Item;
                if (item == null || item.Removed) continue;
                if (!TryGetAffixData(item, out var data) || data != kv.Value) continue; // 过期条目已被清除

                // 状态机：在 1 号槽 → 激活并记住本槽；在手里 → 保持记忆（按数字键装备会交换手持物和槽位物，
                // 开火时武器必在手部槽，没记忆就永远不生效）；都不在 → 清除
                bool inSlot1 = IsInHotbarSlotOne(item);
                bool inHands = (item.ParentInventory?.Owner as Character)?.HeldItems?.Contains(item) == true;
                bool active;
                if (inSlot1) { data.SlotOneHome = true; active = true; }
                else if (inHands) { active = data.SlotOneHome; }
                else { data.SlotOneHome = false; active = false; }

                if (active == data.SlotBonusActive) continue;
                if (active) ApplySlotFireRate(item, def, data);
                else RestoreSlotFireRate(data);
                data.SlotBonusActive = active;
            }
        }

        static void ApplySlotFireRate(Item item, AffixDef affix, AffixData data)
        {
            if (item.Components == null) return;
            if (Math.Abs(affix.SlotOneFireRateMult - 1f) < 0.0001f) return; // 纯伤害词缀不动 Reload
            foreach (var component in item.Components)
            {
                if (component == null) continue;
                var compType = component.GetType();
                var prop = compType == ReflectionCache.MeleeWeaponType ? ReflectionCache.MeleeWeaponReloadProp
                         : compType == ReflectionCache.RangedWeaponType ? ReflectionCache.RangedWeaponReloadProp
                         : null;
                if (prop == null || !prop.CanRead || !prop.CanWrite) continue;
                try
                {
                    float original = (float)prop.GetValue(component);
                    (data.SlotPropChanges ??= new List<(PropertyInfo, object, float)>()).Add((prop, component, original));
                    prop.SetValue(component, original / affix.SlotOneFireRateMult);
                }
                catch (Exception ex)
                {
                    Warning($"Failed to apply slot1 firerate to {item.Name}: {ex.Message}");
                }
            }
        }

        static void RestoreSlotFireRate(AffixData data)
        {
            if (data?.SlotPropChanges == null) return;
            foreach (var (prop, target, original) in data.SlotPropChanges)
            {
                try { prop.SetValue(target, original); }
                catch { }
            }
            data.SlotPropChanges.Clear();
        }

        static void RegisterEffectsForDisplay(Item item, AffixDef affix)
        {
            if (affix.Effects == null || affix.Effects.Count == 0) return;
            if (item.Components == null) return;
            // 药物词缀不注册进组件：改由注入补丁统一触发，不然引擎直接触发，没法加"剩余耐久>20%"的闸门
            if (IsMedicalItem(item)) return;

            foreach (var component in item.Components)
            {
                if (component.statusEffectLists == null) continue;
                foreach (var effect in affix.Effects)
                {
                    // 带 interval 的命中特效不注册：引擎的 interval 按命中次数递减（每击 -1s），高射速武器
                    // 秒烧完冷却，这类改由注入补丁按真实秒数管。例外是 OnWearing：Wearable.Update 用真实
                    // deltaTime 驱动，interval 语义正常，必须注册
                    if (effect.Interval > 0f && effect.type != ActionType.OnWearing) continue;
                    if (!component.statusEffectLists.TryGetValue(effect.type, out var list))
                    {
                        list = new List<StatusEffect>();
                        component.statusEffectLists.Add(effect.type, list);
                    }
                    if (!list.Contains(effect))
                        list.Add(effect);

                    // OnWearing 由 Item.ApplyStatusEffects 驱动，只读构造时合并好的物品级列表——
                    // 运行时注册要同步进去并打开快速检查位
                    if (effect.type == ActionType.OnWearing)
                        RegisterItemLevelEffect(item, effect);
                }
            }
        }

        static void RegisterItemLevelEffect(Item item, StatusEffect effect)
        {
            if (ReflectionCache.ItemStatusEffectListsField?.GetValue(item) is not
                Dictionary<ActionType, List<StatusEffect>> itemLists) return;
            if (!itemLists.TryGetValue(effect.type, out var list))
            {
                list = new List<StatusEffect>();
                itemLists.Add(effect.type, list);
            }
            if (!list.Contains(effect))
                list.Add(effect);
            if (ReflectionCache.ItemHasStatusEffectsField?.GetValue(item) is bool[] has
                && (int)effect.type < has.Length)
                has[(int)effect.type] = true;
        }

        static void UnregisterItemLevelEffect(Item item, StatusEffect effect)
        {
            if (ReflectionCache.ItemStatusEffectListsField?.GetValue(item) is not
                Dictionary<ActionType, List<StatusEffect>> itemLists) return;
            if (itemLists.TryGetValue(effect.type, out var list))
                list.Remove(effect);
        }

        static void UnregisterEffects(Item item, List<StatusEffect> effects)
        {
            if (effects == null || effects.Count == 0) return;
            if (item.Components == null) return;

            foreach (var component in item.Components)
            {
                if (component.statusEffectLists == null) continue;
                foreach (var effect in effects)
                {
                    if (component.statusEffectLists.TryGetValue(effect.type, out var list))
                        list.Remove(effect);
                    if (effect.type == ActionType.OnWearing)
                        UnregisterItemLevelEffect(item, effect);
                }
            }
        }

        public static void Log(object msg, Color? color = null)
        {
            color ??= Color.Cyan;
            LuaCsLogger.LogMessage($"[Touhou.Affixes]:{msg ?? "null"}", color.Value * 0.8f, color.Value);
        }

        // 只调试模式输出的日志：Net 同步、状态机切换这类开发期噪音走这里
        public static void DebugLog(object msg)
        {
            if (DebugMode) Log(msg);
        }

        // 同内容每次会话只打一遍：本地服务器双上下文各打一遍，用它去重
        static readonly HashSet<string> loggedOnceMessages = new();
        public static void LogOnce(object msg, Color? color = null)
        {
            string key = msg?.ToString() ?? "null";
            if (!loggedOnceMessages.Add(key)) return;
            Log(msg, color);
        }

        public static void Warning(object msg)
        {
            LuaCsLogger.LogMessage($"[Touhou.Affixes]:{msg ?? "null"}", Color.Yellow);
        }

        // 按 key 节流，同一 key intervalSeconds 内只打一次（热路径状态提示用）
        static readonly Dictionary<string, double> throttledLogs = new();
        public static void LogThrottled(string key, double intervalSeconds, object msg)
        {
            if (throttledLogs.TryGetValue(key, out double next) && Timing.TotalTime < next) return;
            throttledLogs[key] = Timing.TotalTime + intervalSeconds;
            Log(msg);
        }

        public static (string tierKey, TierWeights weights)? TryGetEnchantingTarget(IEnumerable<Item> inputItems, out Item weapon, out Item material)
        {
            weapon = null;
            material = null;
            string materialKey = null;

            Item firstEnchantable = null;          // 非石头的可附魔物品
            Item firstStone = null;                // 第一块附魔石（无论是否带词缀）
            Item secondStone = null;               // 第二块附魔石（石头转石头时用）
            string stoneAffix = null;

            foreach (var item in inputItems)
            {
                if (item == null) continue;
                if (item.HasTag(STONE_TAG))
                {
                    if (firstStone == null)
                    {
                        firstStone = item;
                        TryGetAffixData(item, out var sd);
                        stoneAffix = sd?.AffixId;
                    }
                    else if (secondStone == null) secondStone = item;
                    continue;
                }
                if (IsEnchantableItem(item) && firstEnchantable == null)
                {
                    firstEnchantable = item;
                    continue;
                }
                // HasTag 走哈希集合，O(1)；不再 Split 分配字符串数组做预筛
                var key = MaterialTiers.Keys.FirstOrDefault(k => item.HasTag(k));
                // 多种材料同时放入时取最高档：数字越小越高级（affixes_1 > affixes_2 > affixes_3）
                if (key != null && (material == null || MaterialTierRank(key) < MaterialTierRank(materialKey)))
                {
                    material = item;
                    materialKey = key;
                }
            }

            // 有普通材料时走随机附魔：石头（无论是否已带词缀）作为目标重掷/附魔
            if (material != null)
            {
                weapon = firstEnchantable ?? firstStone;
                if (weapon == null) return null;
                return (materialKey, MaterialTiers[materialKey]);
            }

            // 没有普通材料时，带词缀的石头作为转移材料：消耗它把词缀覆盖到目标物品上
            if (firstStone != null && stoneAffix != null)
            {
                weapon = firstEnchantable ?? secondStone;
                if (weapon == null) return null;
                material = firstStone;
                return (STONE_TAG, new TierWeights { FixedAffix = stoneAffix });
            }

            return null;
        }

        // 可附魔：武器/工具/医疗/外套槽穿戴/附魔石。纯装饰摆件不给，词缀只上有用的物品
        static bool IsEnchantableItem(Item item)
        {
            return item.HasTag(STONE_TAG)
                || IsMeleeWeapon(item) || IsRangedWeapon(item) || item.HasTag("weapon")
                || IsTool(item) || IsMedicalItem(item)
                || IsOuterClothes(item);
        }

        static int MaterialTierRank(string key)
        {
            if (string.IsNullOrEmpty(key)) return int.MaxValue;
            int i = key.Length - 1;
            while (i >= 0 && char.IsDigit(key[i])) i--;
            if (i == key.Length - 1) return int.MaxValue;
            return int.TryParse(key.Substring(i + 1), out int n) ? n : int.MaxValue;
        }

        public static AffixDef PickAffixByWeight(TierWeights weights, Item item, int seed)
        {
            if (!string.IsNullOrEmpty(weights.FixedAffix))
            {
                // 石头转移：词缀由石头指定，必须适用目标；不适用返回 null 让调用方取消（石头不消耗）
                if (!AffixDefs.TryGetValue(weights.FixedAffix, out var fixedDef) || !IsAffixApplicable(fixedDef, item))
                    return null;
                // 目标是奇迹物品（石头除外，转石头永远整组替换）且转的不是奇迹本身 → 过额外槽校验，违规就取消
                if (fixedDef.Identifier != MIRACLE_AFFIX_ID && !item.HasTag(STONE_TAG)
                    && TryGetAffixData(item, out var targetData) && targetData.AffixId == MIRACLE_AFFIX_ID
                    && !CanAddExtraAffix(targetData, fixedDef, out var slotReason))
                {
                    LogThrottled("miracle_slot_" + item.ID, 5.0,
                        $"EnchantingStation: cannot add [{fixedDef.Identifier}] to {item.Name}: {slotReason}, transfer cancelled (stone not consumed)");
                    return null;
                }
                return fixedDef;
            }

            var pool = new List<(string tier, float weight)>
            {
                ("Broken", weights.Broken), ("Normal", weights.Normal), ("Rare", weights.Rare),
                ("Epic", weights.Epic), ("Legendary", weights.Legendary), ("Special", weights.Special)
            }.Where(t => t.weight > 0).ToList();

            if (pool.Count == 0) return null;

            var deterministicRand = new System.Random(seed);
            // stonerollonly 词缀（奇迹）只能roll到附魔石上，再由石头转移到物品
            bool targetIsStone = item.HasTag(STONE_TAG);
            List<AffixDef> candidates = null;
            // 逐档抽取：抽中的档位没有可用词缀就从剩余档位重掷。旧逻辑回退全池均匀抽取，
            // 药物这类词缀少的品类空档概率全摊到几个词缀上，亢奋/史诗传奇满地都是
            while (pool.Count > 0)
            {
                float total = pool.Sum(t => t.weight);
                float roll = (float)(deterministicRand.NextDouble() * total);
                float cumulative = 0;
                int chosenIdx = pool.Count - 1;
                for (int i = 0; i < pool.Count; i++)
                {
                    cumulative += pool[i].weight;
                    if (roll <= cumulative) { chosenIdx = i; break; }
                }
                string chosenTier = pool[chosenIdx].tier;
                candidates = AffixDefs.Values.Where(a => a.Tier == chosenTier
                    && (!a.StoneRollOnly || targetIsStone)
                    && IsAffixApplicable(a, item)).ToList();
                if (candidates.Count > 0) break;
                pool.RemoveAt(chosenIdx);
                candidates = null;
            }

            if (candidates == null || candidates.Count == 0) return null;

            // 工具类（含河童工具枪这种双功能）：工具专属词缀权重×5，其余也出但概率大降；高档没工具词缀自然回落
            if (candidates.Count > 1 && IsTool(item))
            {
                float totalW = 0f;
                foreach (var c in candidates) totalW += IsToolAffix(c) ? ToolAffixPriorityWeight : 1f;
                float r = (float)(deterministicRand.NextDouble() * totalW);
                foreach (var c in candidates)
                {
                    r -= IsToolAffix(c) ? ToolAffixPriorityWeight : 1f;
                    if (r <= 0f) return c;
                }
                return candidates[candidates.Count - 1];
            }

            int idx = deterministicRand.Next(0, candidates.Count);
            return candidates[idx];
        }

        const float ToolAffixPriorityWeight = 5f;

        static bool IsToolAffix(AffixDef a) =>
            a.Applicable != null &&
            a.Applicable.Split(',').Any(t => t.Trim().Equals("tool", StringComparison.OrdinalIgnoreCase));
    }

    public class AffixData
    {
        public string AffixId;
        public string Tier;
        public string NamePrefix;
        public Color DisplayColor;
        // 附魔时的 prefab 标识，读取时双因子校验用；null = 旧数据没法校验
        public string PrefabId;
        // 词缀实例 UID，跨巡回随存档属性/桥接文件往返；null = 旧存档不可验证
        public string Uid;
        public List<StatusEffect> Effects;
        // 被词条改过的组件属性+原值，移除时恢复
        public List<(PropertyInfo Prop, object Target, float Original)> PropChanges;
        // 被改过的技能等级+原值
        public List<(object Skill, float OriginalLevel)> SkillChanges;
        // 被改成双手的槽位列表+原内容
        public List<(List<InvSlotType> Target, List<InvSlotType> Original)> SlotChanges;
        // 优先词条的 Reload 修改+原值
        public List<(PropertyInfo Prop, object Target, float Original)> SlotPropChanges;
        public bool SlotBonusActive;
        // "本槽"记忆：数字键装备会把手持物和槽位物交换，记住它来自 1 号槽，持有期间保持加成
        public bool SlotOneHome;
        // 单打独斗激活标记，TickLoneWolfBonuses 维护
        public bool LoneWolfActive;
        // 奇迹解锁的额外词缀（最多 2 个，传说/特殊档最多 1 个，不重复、不能再有奇迹）
        public List<string> ExtraAffixIds;
        // 生效词缀：单词缀=定义本身，奇迹=ComposeAffixDef 合成结果，效果补丁都读它
        public AffixDef EffectiveDef;
        // 预拼好的富文本前缀，名称补丁直接用
        public string RichPrefixDisplay;
        // 预拼好的纯文本前缀，名称防重用
        public string PlainPrefixDisplay;
        public bool HasExtras => ExtraAffixIds != null && ExtraAffixIds.Count > 0;
    }

    public class AffixDef
    {
        public string Identifier;
        public string Tier;
        public string NamePrefix;
        public string Applicable;
        // 效果说明，显示在物品描述下方（desc 属性）
        public string Description = "";
        // 护甲形态说明（descarmor，武器护甲两用词条才需要）
        public string DescriptionArmor = "";
        // 本地化解析结果（语言切换时 LocalizedString 自动更新）
        public LocalizedString NamePrefixLoc;
        public LocalizedString DescriptionLoc;
        public LocalizedString DescriptionArmorLoc;
        // 名称前缀必须用 .Value 取原始标记文本：TextManager.Get 可能返回 RichString，
        // 其 ToString() 返回的是剥离颜色标记后的 SanitizedValue，直接拼进物品名会丢颜色
        public string DisplayPrefix => NamePrefixLoc != null ? NamePrefixLoc.Value : NamePrefix;
        public string DisplayDesc => DescriptionLoc?.ToString() ?? Description;
        public string DisplayDescArmor => DescriptionArmorLoc?.ToString() ?? DescriptionArmor;
        public Color DisplayColor;
        public List<StatusEffect> Effects;
        // 伤害倍率（近战乘 Attack.DamageMultiplier，枪械乘投射物 damageMultiplier）
        public float DamageMult = 1f;
        // 射速/挥速倍率（实现为 Reload /= 倍率）
        public float FireRateMult = 1f;
        // 穿戴者受伤倍率，>1 易伤 <1 减伤
        public float DamageTakenMult = 1f;
        // 工具耗材消耗倍率
        public float FuelConsumeMult = 1f;
        // 开火不耗弹药的概率（返还本次弹匣/电池耐久）
        public float AmmoSaveChance = 0f;
        // 用药不耗耐久的概率（一次性药物除外）
        public float DurabilitySaveChance = 0f;
        // 修船壳速度倍率（StructureFixAmount/LevelWallFixAmount 实例级乘算）
        public float StructureFixMult = 1f;
        // 设备修理速度倍率（GetStatValue(RepairSpeed) 加算）
        public float DeviceRepairMult = 1f;
        // 冷却就绪那一下的伤害倍率（金冠）
        public float CooldownDamageMult = 1f;
        public float CooldownDamageInterval = 30f;
        // 弹匣最后一发的伤害倍率（王冠）
        public float LastShotDamageMult = 1f;
        // 单手近战改双手握持（专一）
        public bool ForceTwoHanded;
        // 散布倍率（Spread/UnskilledSpread × 倍率，<1 更精准）
        public float SpreadMult = 1f;
        // 技能等级需求倍率（RequiredSkills.Level × 倍率）
        public float SkillReqMult = 1f;
        // 优先：在 1 号快捷栏时的伤害倍率，离开即失效（动态判定不写属性）
        public float SlotOneDamageMult = 1f;
        // 优先：在 1 号快捷栏时的射速倍率（条件式 Reload 修改，进出槽位时应用/恢复）
        public float SlotOneFireRateMult = 1f;
        // 冷静：噪音倍率（0.2 = 降噪 80%），手持或穿戴生效
        public float NoiseMult = 1f;
        // 稳定：手部受伤不再抖瞄准
        public bool NoAimWobble;
        // 踏步：腿部受伤不再减速
        public bool NoMovePenalty;
        // 受击反击（荆棘/失重共用）：反给攻击者的流血强度
        public float ThornsBleeding = 0f;
        // 受击反击：撕裂伤强度
        public float ThornsLacerations = 0f;
        // 受击反击：反给攻击者的眩晕秒数（0 = 无眩晕反伤）
        public float ThornsStun = 0f;
        // 受击反击：眩晕反伤的触发概率（1 = 必定眩晕，失重用默认值）
        public float ThornsStunChance = 1f;
        public float ThornsInterval = 2f;
        // 专注：单手远程改双手握持
        public bool ForceTwoHandedRanged;
        // 巨人杀手：对重生物增伤
        public float MassBonusMult = 1f;
        // 质量阈值（人类约 70-80）
        public float MassThreshold = 100f;
        // 蓄力时间倍率（紧张 0.5 / 等候 1.5，MaxChargeTime getter 实时乘）
        public float ChargeTimeMult = 1f;
        // 领先：满弹匣第一发增伤（仅耐久式弹匣）
        public float FirstShotDamageMult = 1f;
        // 单打独斗：快捷栏只有这一把附魔武器时增伤
        public float LoneWolfDamageMult = 1f;
        // 只能 roll 到附魔石上（奇迹），再经石头转移
        public bool StoneRollOnly;
        // ComposeAffixDef 的合成标记：蓄力门槛已在合成时烘焙，补丁见到就跳过 ChargeGatedDamageApplies
        public bool IsComposite;
        // 没识别的自定义 XML 属性收这里，给后续拓展用
        public Dictionary<string, string> CustomProps;
        public string GetCustomProp(string name)
        {
            if (CustomProps == null) return null;
            foreach (var kv in CustomProps)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
    }

    public struct TierWeights
    {
        public float Broken, Normal, Rare, Epic, Legendary, Special;
        public string FixedAffix;
    }
}
