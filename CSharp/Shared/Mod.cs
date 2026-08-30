using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace ItemAffixes
{
    public class Mod : Barotrauma.LuaCs.IAssemblyPlugin
    {
        public static ContentPackage Package;
        public static string Name => Package?.Name ?? "ItemAffixes";
        public static bool DebugMode = false;

        private Harmony harmony;
        public static Dictionary<string, AffixDef> AffixDefs = new();
        public static Dictionary<ushort, AffixData> ItemAffixes = new();
        public static Dictionary<ushort, string> PendingAffixes = new();

        /// <summary>拓展点：词缀应用/替换完成后触发（物品、新词缀）。订阅者必须轻量，异常会被隔离</summary>
        public static event Action<Item, AffixDef> AffixApplied;
        /// <summary>拓展点：词缀被移除或被替换前触发（物品、旧词缀）。订阅者必须轻量，异常会被隔离</summary>
        public static event Action<Item, AffixDef> AffixRemoved;
        /// <summary>拓展点：额外适用性判定。返回 true/false 直接覆盖默认判定（可放行非东方模组物品），null 走默认逻辑</summary>
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

        // 材料档位：数字越小越昂贵（affixes_1=核心 500 / _2=棱镜 100 / _3=合金 25），权重也随之更好
        // 注意键是物品 tag（affixes_material_N），不是物品 identifier（affixes_N）
        // Special 档（亢奋/失重）概率最低：高级材料约 4.8%，中级约 2.0%，低级约 1.0%
        public static Dictionary<string, TierWeights> MaterialTiers = new()
        {
            ["affixes_material_1"] = new TierWeights { Normal = 15, Rare = 45, Epic = 25, Legendary = 15, Special = 5 },
            ["affixes_material_2"] = new TierWeights { Broken = 10, Normal = 30, Rare = 30, Epic = 20, Legendary = 10, Special = 2 },
            ["affixes_material_3"] = new TierWeights { Broken = 35, Normal = 35, Rare = 15, Epic = 10, Legendary = 5, Special = 1 },
        };

        static bool savedDataLoaded = false;

        /// <summary>
        /// 主线程延迟任务队列：替代 Task.Delay 的线程池回调——游戏状态（Item.ItemList 等）不是线程安全的，
        /// 恢复词缀这类操作必须在主线程执行。由 GameMain.Update 补丁每帧检查（开销≈一次 Count 判断）。
        /// 后续功能也可用 ScheduleOnMainThread 挂自己的延迟任务。
        /// </summary>
        static readonly List<(double At, Action Task)> mainThreadTasks = new();

        /// <summary>在主线程延迟 delaySeconds 秒后执行 task（随游戏时间走，暂停时不走）</summary>
        public static void ScheduleOnMainThread(double delaySeconds, Action task)
        {
            mainThreadTasks.Add((Timing.TotalTime + delaySeconds, task));
        }

        /// <summary>每帧由 MainThreadSchedulerPatch 调用，执行到期任务</summary>
        public static void RunMainThreadScheduled()
        {
            if (mainThreadTasks.Count == 0) return;
            for (int i = mainThreadTasks.Count - 1; i >= 0; i--)
            {
                if (Timing.TotalTime < mainThreadTasks[i].At) continue;
                var task = mainThreadTasks[i].Task;
                mainThreadTasks.RemoveAt(i);
                try { task(); } catch (Exception ex) { Warning($"Scheduled task failed: {ex.Message}"); }
            }
        }

        /// <summary>
        /// 会话令牌：每次游戏进程启动时生成。桥接文件（affix_save.xml）只在同一进程内有效，
        /// 物品 ID 跨进程会漂移，令牌不匹配时整份文件作废，防止上个会话的词缀贴到无辜物品上。
        /// </summary>
        static readonly string SessionToken = Guid.NewGuid().ToString("N");

        public const string AFFIX_TAG_PREFIX = "__affix_";
        /// <summary>服务端→客户端的词缀应用同步消息 id（附魔台在服务端执行，客户端需要本地镜像才有显示与效果）</summary>
        public const string NET_APPLY_AFFIX = "itemaffixes_apply";

        /// <summary>东方模组内容包名前缀：本地测试版叫"东方潜渊行动组测试"，创意工坊版叫"东方潜渊行动组"，前缀匹配同时覆盖</summary>
        public const string TouhouPackageNamePrefix = "东方潜渊行动组";

        // 全局快速退出标志：没有这类词缀时对应补丁整体跳过（LoadAffixDefs 时计算）
        public static bool AnyDamageMultAffixes;
        public static bool AnyDamageTakenAffixes;
        public static bool AnyFuelMultAffixes;
        public static bool AnyRepairBonusAffixes;

        public void Initialize()
        {
            if (!LuaCsSetup.Instance.PluginPackageManager.TryGetPackageForPlugin<Mod>(out ContentPackage package))
            {
                LuaCsLogger.LogMessage("[ItemAffixes] Could not find ContentPackage", Color.Red);
                return;
            }
            Package = package;
            if (Package.Dir.Contains("LocalMods"))
            {
                DebugMode = true;
                Log($"Found [{Package.Name}] in LocalMods, debug mode enabled");
            }

            harmony = new Harmony("com.itemaffixes.mod");

            Log("ItemAffixes mod initialized");
        }

        // Harmony 不去重同 owner 补丁：OnLoadCompleted 每次内容重载都会触发，
        // 不防护的话 PatchAll/命令/钩子会层层叠加（tooltip 词缀行显示 2 遍、4 遍……就是因此）
        static bool oneTimeInitDone;

        public void OnLoadCompleted()
        {
            LoadAffixDefs();
            if (oneTimeInitDone)
            {
                Log($"Reloaded {AffixDefs.Count} affix definitions (patches/hooks already registered, skipped)");
                return;
            }
            oneTimeInitDone = true;

            harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());

            AddCommands();

            LuaCsSetup.Instance.Hook.Add("roundEnd", "ItemAffixes.RoundEnd", OnRoundEnd);
            LuaCsSetup.Instance.Hook.Add("roundStart", "ItemAffixes.RoundStart", OnRoundStart);

            Log($"Loaded {AffixDefs.Count} affix definitions, patching complete");
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
            LuaCsSetup.Instance.Game.RemoveCommand("debugaffix");
            LuaCsSetup.Instance.Game.RemoveCommand("saveaffixes");
            LuaCsSetup.Instance.Game.RemoveCommand("loadaffixes");
            LuaCsSetup.Instance.Game.RemoveCommand("restoreaffixes");
            LuaCsSetup.Instance.Game.RemoveCommand("listaffixes");
            LuaCsSetup.Instance.Game.RemoveCommand("listaffixdefs");
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;   // Dispose 后补丁已卸载，允许下次完整重注册
            ItemAffixes.Clear();
            AffixDefs.Clear();
            PendingAffixes.Clear();
            mainThreadTasks.Clear();
            AffixApplied = null;
            AffixRemoved = null;
            ApplicabilityOverride = null;
        }

        /// <summary>Affixes.xml 里已被解析占用的属性名，之外的属性会收进 AffixDef.CustomProps</summary>
        static readonly HashSet<string> KnownAffixAttrs = new(StringComparer.OrdinalIgnoreCase)
        {
            "identifier", "tier", "nameprefix", "applicable", "desc", "descarmor",
            "damagemult", "fireratemult", "damagetakenmult", "fuelconsumemult",
            "repairbonuspercent", "spreadmult", "skillreqmult"
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

                    // 本地化：Text/ 下的 affix.prefix/desc/descarmor.<id> 优先，
                    // 未翻译的语言回落到默认语言文本，最后兜底 XML 属性；语言切换自动生效
                    def.NamePrefixLoc = TextManager.Get($"affix.prefix.{id}").Fallback(def.NamePrefix, true);
                    def.DescriptionLoc = TextManager.Get($"affix.desc.{id}").Fallback(def.Description, true);
                    def.DescriptionArmorLoc = TextManager.Get($"affix.descarmor.{id}").Fallback(def.DescriptionArmor, true);

                    def.DamageMult = ParseInvariantFloat(element.Attribute("damagemult")?.Value, 1f);
                    def.FireRateMult = ParseInvariantFloat(element.Attribute("fireratemult")?.Value, 1f);
                    def.DamageTakenMult = ParseInvariantFloat(element.Attribute("damagetakenmult")?.Value, 1f);
                    def.FuelConsumeMult = ParseInvariantFloat(element.Attribute("fuelconsumemult")?.Value, 1f);
                    def.RepairBonusPercent = ParseInvariantFloat(element.Attribute("repairbonuspercent")?.Value, 0f);
                    def.SpreadMult = ParseInvariantFloat(element.Attribute("spreadmult")?.Value, 1f);
                    def.SkillReqMult = ParseInvariantFloat(element.Attribute("skillreqmult")?.Value, 1f);

                    // 拓展点：未识别的自定义属性原样收进 CustomProps，
                    // 后续新词缀参数不用改解析代码，直接读字典即可
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
                AnyRepairBonusAffixes = AffixDefs.Values.Any(a => a.RepairBonusPercent > 0f);
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

        /// <summary>
        /// LuaCsAction 的包参约定：真正的命令行参数被包成 args[0] 里的 string[]。
        /// 直接 args[0].ToString() 只会得到 "System.String[]"。
        /// </summary>
        static string[] CmdArgs(object[] args)
        {
            if (args == null || args.Length == 0) return Array.Empty<string>();
            if (args.Length == 1 && args[0] is string[] real) return real;
            return args.Select(a => a?.ToString()).ToArray();
        }

        public static void AddCommands()
        {
            // 控制台自动补全：列出全部词缀 id
            LuaCsFunc affixIdArgs = _ => new[] { AffixDefs.Keys.OrderBy(id => id).ToArray() };

            // 服务端附魔台应用词缀后广播给客户端：客户端本地镜像一份（标签/名称/穿戴效果）。
            // 物品 Tags 的实例级修改不会自动网络同步，没有这一步时服务器上附魔成功但客户端看不到。
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
                    var pool = AffixDefs.Values.Where(a => IsAffixApplicable(a, heldItem)).ToList();
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

            // 定向附魔：严格校验适用性；多人需服务端给玩家勾选该命令权限。
            // RelayToServer=false 让有权限的客户端在本地对自己的角色执行（服务器控制台可用第二参数指定玩家）。
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

                if (ItemAffixes.TryGetValue(heldItem.ID, out var data))
                {
                    AffixDefs.TryGetValue(data.AffixId, out var oldDef);
                    UnregisterEffects(heldItem, data.Effects);
                    RestoreStatChanges(data);
                    ItemAffixes.Remove(heldItem.ID);
                    RemoveAffixTag(heldItem);
                    SaveAffixData();
                    savedDataLoaded = false;
                    RaiseAffixEvent(AffixRemoved, heldItem, oldDef);
                    Log($"Removed affix [{data.NamePrefix}] from {heldItem.Name}");
                }
                else
                {
                    Log($"No affix on {heldItem.Name}");
                }
            }, null, false);

            LuaCsSetup.Instance.Game.AddCommand("clearallaffixes", "Remove ALL affixes from every item, then SAVE to write the clean state to file", (args) =>
            {
                int cleared = 0;
                foreach (var item in Item.ItemList.ToList())
                {
                    bool hadAffix = ItemAffixes.TryGetValue(item.ID, out var data);
                    bool hadTag = item.GetTags().Any(t =>
                        t.Value.StartsWith(AFFIX_TAG_PREFIX, StringComparison.OrdinalIgnoreCase));
                    if (!hadAffix && !hadTag) continue;

                    // 事件需要旧词缀定义：优先内存表，标签兜底（移除前解析）
                    AffixDef oldDef = null;
                    if (hadAffix) AffixDefs.TryGetValue(data.AffixId, out oldDef);
                    if (oldDef == null) Helpers.TryReadAffixFromTags(item, out oldDef);

                    if (hadAffix)
                    {
                        UnregisterEffects(item, data.Effects);
                        RestoreStatChanges(data);
                        ItemAffixes.Remove(item.ID);
                    }
                    RemoveAffixTag(item);
                    RaiseAffixEvent(AffixRemoved, item, oldDef);
                    cleared++;
                }

                PendingAffixes.Clear();
                try { File.Delete(SaveFilePath); } catch { }
                savedDataLoaded = false;

                // 存档文件（.save/.sub）里的 affixid 属性不需要手工清理：
                // Item.Save 只在物品带 __affix_ 标签时才写 affixid，
                // 标签已清空，下一次正常保存（编辑器存潜艇/战役存档）写出的就是干净文件。
                Log($"Cleared {cleared} affixes. NOW SAVE (editor: save submarine / campaign: save game) to make it permanent.", Color.Orange);
            }, null, false);

            // 诊断命令：定位"词缀效果不生效"断在哪一环（标签→内存表→组件注册→引擎触发）
            LuaCsSetup.Instance.Game.AddCommand("debugaffix", "Debug affix state of held item (or all items on you)", (args) =>
            {
                var targets = new List<Item>();
                var held = Character.Controlled?.HeldItems?.FirstOrDefault();
                if (held != null) targets.Add(held);
                if (targets.Count == 0 && Character.Controlled != null)
                {
                    foreach (var item in Item.ItemList)
                    {
                        if (item.Removed) continue;
                        if (item.ParentInventory?.Owner is Character c && c == Character.Controlled)
                            targets.Add(item);
                    }
                }
                if (targets.Count == 0)
                {
                    Log("No held/worn items found", Color.Yellow);
                    return;
                }

                foreach (var item in targets)
                {
                    bool hasTag = Helpers.TryReadAffixFromTags(item, out var def);
                    bool inMemory = ItemAffixes.ContainsKey(item.ID);
                    Log($"--- {item.Name} (ID={item.ID}) affixTag={(hasTag ? def.Identifier : "none")} inMemory={inMemory} outerClothes={IsOuterClothes(item)}");
                    if (!hasTag) continue;
                    if (item.Components == null)
                    {
                        Log("  !! no components at all", Color.Orange);
                        continue;
                    }
                    foreach (var comp in item.Components)
                    {
                        if (comp == null) continue;
                        var lists = ReflectionCache.StatusEffectListsField?.GetValue(comp)
                            as Dictionary<ActionType, List<StatusEffect>>;
                        string summary = lists == null || lists.Count == 0
                            ? "(no effect lists)"
                            : string.Join(", ", lists.Select(kv => $"{kv.Key}x{kv.Value.Count}"));
                        Log($"  comp {comp.GetType().Name}: {summary}");
                    }
                    if (def.Effects != null)
                    {
                        foreach (var eff in def.Effects)
                        {
                            bool reg = item.Components.Any(c =>
                                c != null && AffixEffectInjectionPatch.IsEffectRegistered(c, eff.type, eff));
                            bool regItem = false;
                            if (ReflectionCache.ItemStatusEffectListsField?.GetValue(item) is
                                Dictionary<ActionType, List<StatusEffect>> itemLists)
                                regItem = itemLists.TryGetValue(eff.type, out var il) && il.Contains(eff);
                            Log($"  affix effect type={eff.type} interval={eff.Interval} registeredInComponent={reg} registeredInItem={regItem}");
                        }
                    }
                }
                if (Character.Controlled != null)
                    Log($"Character.SpeedMultiplier = {Character.Controlled.SpeedMultiplier}");
            }, null, false);

            LuaCsSetup.Instance.Game.AddCommand("saveaffixes", "Save all affix data to file", (args) =>
            {
                SaveAffixData();
            }, null, false);

            LuaCsSetup.Instance.Game.AddCommand("loadaffixes", "Load affix data from file into pending", (args) =>
            {
                LoadAffixData();
            }, null, false);

            LuaCsSetup.Instance.Game.AddCommand("restoreaffixes", "Force restore pending affixes now", (args) =>
            {
                RestoreAffixes();
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
                    Log($"  [{kv.Value.AffixId}] ID={kv.Key} {info}");
                }
            }, null, false);

            LuaCsSetup.Instance.Game.AddCommand("listaffixdefs", "List all affix definitions and their applicable targets", (args) =>
            {
                foreach (var def in AffixDefs.Values.OrderBy(d => d.Tier).ThenBy(d => d.Identifier))
                {
                    Log($"  {def.Identifier} [{def.Tier}] applicable={def.Applicable}");
                }
            }, null, false);
        }

        /// <summary>
        /// LuaCs 注册的命令默认 RelayToServer=true，多人下会转发到服务器执行，
        /// 但服务器拿不到"是谁按的回车"，Character.Controlled 恒为空。
        /// 对"给自己手上物品附魔"这类命令改为本地执行，权限仍由引擎的自定义命令权限把关。
        /// </summary>
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

        static Item FindItemById(ushort id)
        {
            foreach (var item in Item.ItemList)
            {
                if (item.ID == id) return item;
            }
            return null;
        }

        static void RemoveAffixTag(Item item)
        {
            foreach (var tag in item.GetTags().ToList())
            {
                if (tag.Value.StartsWith(AFFIX_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                    item.RemoveTag(tag);
            }
        }

        public static void SaveAffixData()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SaveFilePath));
                var doc = new XDocument(new XElement("AffixData",
                    new XAttribute("session", SessionToken)));
                int saved = 0;
                foreach (var kv in ItemAffixes)
                {
                    doc.Root.Add(new XElement("Item",
                        new XAttribute("id", kv.Key.ToString()),
                        new XAttribute("affixid", kv.Value.AffixId)));
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
                int loaded = 0;
                foreach (var el in doc.Root.Elements("Item"))
                {
                    if (ushort.TryParse(el.Attribute("id")?.Value, out var id) &&
                        !string.IsNullOrEmpty(el.Attribute("affixid")?.Value))
                    {
                        PendingAffixes[id] = el.Attribute("affixid").Value;
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

        static void RestoreAffixes()
        {
            int count = 0;
            int pruned = 0;
            foreach (var item in Item.ItemList)
            {
                if (item.Removed) continue;
                if (ItemAffixes.ContainsKey(item.ID)) continue;

                string affixId = null;

                if (PendingAffixes.TryGetValue(item.ID, out var pending))
                {
                    affixId = pending;
                }
                else
                {
                    foreach (var tag in item.GetTags())
                    {
                        if (tag.Value.StartsWith(AFFIX_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                        {
                            affixId = tag.Value.Substring(AFFIX_TAG_PREFIX.Length);
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(affixId) && AffixDefs.TryGetValue(affixId, out var def))
                {
                    // 规则收窄后（如护甲/穿戴仅限 OuterClothes）自动清掉旧存档里不再合法的词缀标签
                    if (!IsAffixApplicable(def, item))
                    {
                        RemoveAffixTag(item);
                        pruned++;
                        continue;
                    }
                    ApplyAffix(item, def);
                    count++;
                }
            }

            if (count > 0 || pruned > 0)
                Log($"Restored {count} affixes, pruned {pruned} incompatible (pending={PendingAffixes.Count}, items={Item.ItemList.Count(i => !i.Removed)})");
            else
                Log($"No affixes restored: pending={PendingAffixes.Count}, items={Item.ItemList.Count(i => !i.Removed)}");
        }

        static string SaveFilePath => Path.Combine(Package.Dir, "Data", "affix_save.xml");

        object OnRoundEnd(object[] args)
        {
            savedDataLoaded = false;
            PendingAffixes.Clear();
            ItemAffixes.Clear();
            AffixEffectInjectionPatch.ClearProcTimers();
            return null;
        }

        object OnRoundStart(object[] args)
        {
            try { File.Delete(SaveFilePath); } catch { }
            Log($"roundStart: pending={PendingAffixes.Count}, affixed={ItemAffixes.Count}. Scheduling restore in 3s");
            // 主线程调度：原来用 Task.Delay 的线程池回调直接改游戏状态，有线程安全隐患
            ScheduleOnMainThread(3.0, RestoreAffixes);
            return null;
        }

        public static bool IsAffixApplicable(AffixDef affix, Item item)
        {
            // 拓展点：订阅者返回 true/false 直接覆盖（可放行非东方模组物品），null 走默认逻辑
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
            // 只对东方模组物品生效：按内容包名前缀匹配，本地测试版与工坊版都覆盖。
            // 已附魔物品的恢复/显示不走这里，不受影响；enchant 调试指令仍可强制绕过
            if (!IsTouhouModItem(item)) return false;
            if (affix.Applicable == "all") return true;
            // 药物（吗啡、东方模组药剂等）也带 MeleeWeapon 组件可以敲人，但本质是药物：
            // 只给 medical 词缀，不给武器/近战/枪械词缀
            bool isMedical = IsMedicalItem(item);
            var tags = affix.Applicable.Split(',');
            foreach (var tag in tags)
            {
                switch (tag.Trim().ToLowerInvariant())
                {
                    // 组件判定优先：东方模组部分近战武器（金刚杵等）没有 weapon tag，
                    // 服装挂着 clothing tag 却带减伤——tag 判定不可靠
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

        /// <summary>
        /// 物品是否来自东方模组。主判定用 identifier 白名单：扫描所有包名含"东方潜渊行动组"的内容包
        /// 的 Item XML 构建——补丁模组覆盖原物品定义后 ContentPackage 会易主，但 identifier 不变；
        /// 包名前缀匹配作为兜底，覆盖补丁新增的东方系物品。
        /// </summary>
        public static bool IsTouhouModItem(Item item)
        {
            if (item?.Prefab == null) return false;
            if (!touhouItemIdsBuilt) BuildTouhouItemIdSet();
            if (touhouItemIds.Contains(item.Prefab.Identifier.Value)) return true;
            string pkgName = item.Prefab.ContentPackage?.Name;
            return !string.IsNullOrEmpty(pkgName)
                && pkgName.StartsWith(TouhouPackageNamePrefix, StringComparison.OrdinalIgnoreCase);
        }

        static HashSet<string> touhouItemIds;
        static bool touhouItemIdsBuilt;

        /// <summary>扫描东方系内容包的 filelist.xml → Item 文件，收集全部物品 identifier（首次使用时惰性构建）</summary>
        static void BuildTouhouItemIdSet()
        {
            touhouItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            touhouItemIdsBuilt = true;
            try
            {
                foreach (var pkg in ContentPackageManager.RegularPackages)
                {
                    if (pkg?.Name == null) continue;
                    if (pkg.Name.IndexOf(TouhouPackageNamePrefix, StringComparison.OrdinalIgnoreCase) < 0) continue;
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
                Log($"Touhou item whitelist built: {touhouItemIds.Count} identifiers");
            }
            catch (Exception ex)
            {
                Warning($"BuildTouhouItemIdSet failed: {ex.Message}");
            }
        }

        /// <summary>按物品形态选择词缀描述：外套槽穿戴物优先用 descarmor，其余用 desc</summary>
        public static string GetDescriptionFor(AffixDef def, Item item)
        {
            if (item != null && IsOuterClothes(item)
                && !string.IsNullOrEmpty(def.DisplayDescArmor))
                return def.DisplayDescArmor;
            return def.DisplayDesc;
        }

        /// <summary>
        /// 药物判定：必须能在健康界面使用（UseInHealthInterface），且 tag 或 category 标了 medical。
        /// 这类物品即使带 MeleeWeapon 组件（可以敲人注射）也只算药物，不算近战武器。
        /// </summary>
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

        public static bool IsTool(Item item) =>
            HasComponentNamed(item, "RepairTool") || item.HasTag("tool");

        /// <summary>有 Wearable 组件且带 damagemodifier = 有减伤的护甲（潜水服/防弹衣/东方角色服装）</summary>
        public static bool IsArmor(Item item) => TryGetWearableDamageModifierCount(item, out int n) && n > 0;

        /// <summary>有 Wearable 组件但无减伤 = 纯穿戴物（耳机/护身符/吊坠）</summary>
        public static bool IsPlainWearable(Item item) => TryGetWearableDamageModifierCount(item, out int n) && n == 0;

        /// <summary>
        /// 外套槽（OuterClothes）可穿戴物：潜水服/防弹衣/外套。
        /// 护甲/穿戴类词缀只给它——头饰/耳机槽的多件小装备不能附魔，防止叠效果。
        /// Wearable 继承自 Pickable，槽位在 Pickable.AllowedSlots（List&lt;InvSlotType&gt;）。
        /// </summary>
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
                        if (s.ToString() == "OuterClothes") return true;
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

        public static void ApplyAffix(Item item, AffixDef affix)
        {
            if (ItemAffixes.TryGetValue(item.ID, out var oldData))
            {
                AffixDefs.TryGetValue(oldData.AffixId, out var oldDef);
                RaiseAffixEvent(AffixRemoved, item, oldDef);
                if (oldData.Effects != null) UnregisterEffects(item, oldData.Effects);
                RestoreStatChanges(oldData);
            }

            RemoveAffixTag(item);

            var data = new AffixData
            {
                AffixId = affix.Identifier,
                Tier = affix.Tier,
                NamePrefix = affix.NamePrefix,
                DisplayColor = affix.DisplayColor,
                Effects = affix.Effects
            };

            ItemAffixes[item.ID] = data;
            string affixTag = AFFIX_TAG_PREFIX + affix.Identifier;
            if (string.IsNullOrEmpty(item.Tags) || !item.Tags.Contains(affixTag))
                item.Tags = string.IsNullOrEmpty(item.Tags) ? affixTag : item.Tags + "," + affixTag;

            RegisterEffectsForDisplay(item, affix);
            ApplyStatChanges(item, affix, data);
            RaiseAffixEvent(AffixApplied, item, affix);
            Log($"Applied [{affix.NamePrefix}] ({affix.Tier}) to {item.Name} (ID={item.ID})");
        }

        /// <summary>
        /// 服务端应用词缀后的同步：①把词缀标签打到所有同 ID 的真实物品实例上——
        /// 本地创建服务器（listen server）时服务器/客户端是两个互相隔离的脚本上下文，
        /// 但共享同一份游戏实体列表，直接改客户端侧实例的 Tags 即可让显示/效果补丁读到；
        /// ②通过跨版本兼容层 ILuaCsNetworking.Send 广播给远端客户端（编译期调用，不用反射猜重载）。
        /// </summary>
        public static void BroadcastAffixApplied(Item item, AffixDef affix)
        {
            try
            {
                if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsServer) return;

                string affixTag = AFFIX_TAG_PREFIX + affix.Identifier;
                int mirrored = 0;
                foreach (var it in Item.ItemList)
                {
                    if (it == null || it.Removed || it.ID != item.ID || ReferenceEquals(it, item)) continue;
                    // 重复附魔时必须先清掉旧词缀标签——旧标签排在前面，不清则客户端仍显示旧词缀
                    bool hasNew = false, hasOld = false;
                    foreach (var t in it.GetTags())
                    {
                        if (t.Value.Equals(affixTag, StringComparison.OrdinalIgnoreCase)) hasNew = true;
                        else if (t.Value.StartsWith(AFFIX_TAG_PREFIX, StringComparison.OrdinalIgnoreCase)) hasOld = true;
                    }
                    bool changed = false;
                    if (hasOld) { RemoveAffixTag(it); changed = true; }
                    if (!hasNew)
                    {
                        it.Tags = string.IsNullOrEmpty(it.Tags) ? affixTag : it.Tags + "," + affixTag;
                        changed = true;
                    }
                    if (changed) mirrored++;
                }
                if (mirrored > 0)
                    Log($"Net: mirrored [{affix.Identifier}] to {mirrored} same-ID instance(s) for local client");

                var net = LuaCsSetup.Instance.Networking;

                // 双端程序集签名完全不同：客户端 Send(IWriteMessage, DeliveryMethod) 是广播（发给服务器），
                // 服务端只有 Send(IWriteMessage, NetworkConnection, DeliveryMethod) 单播。
                // Shared 代码同时按两端程序集编译，任何直接调用都会在某一端编译失败，必须全程反射。
                // 服务端广播 = 遍历 GameServer.ConnectedClients 逐个单播，每条消息重新 Start 避免复用已消费的缓冲区。
                var sendToConn = net.GetType().GetMethods().FirstOrDefault(m =>
                    m.Name == "Send" && m.GetParameters() is { Length: 3 } p &&
                    p[1].ParameterType.Name == "NetworkConnection" && p[2].ParameterType.IsEnum);
                var clients = GameMain.NetworkMember.GetType()
                    .GetProperty("ConnectedClients")?.GetValue(GameMain.NetworkMember) as System.Collections.IEnumerable;
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
                        m2.WriteString(affix.Identifier);
                        sendToConn.Invoke(net, new object[] { m2, conn, reliable });
                        sent++;
                    }
                    Log($"Net: broadcast [{affix.Identifier}] for {item.Name} (ID={item.ID}) unicast to {sent} client(s)");
                    return;
                }

                // 兜底：少数版本可能存在 Send(msg, DeliveryMethod) 广播或单参 Send
                var msg = net.Start(NET_APPLY_AFFIX);
                msg.WriteUInt16(item.ID);
                msg.WriteString(affix.Identifier);
                var send = net.GetType().GetMethods().FirstOrDefault(m =>
                    m.Name == "Send" && m.GetParameters() is { Length: 2 } p &&
                    p[1].ParameterType.IsEnum && p[1].ParameterType.Name == "DeliveryMethod");
                if (send != null)
                {
                    object reliable = Enum.Parse(send.GetParameters()[1].ParameterType, "Reliable");
                    send.Invoke(net, new[] { msg, reliable });
                    Log($"Net: broadcast [{affix.Identifier}] for {item.Name} (ID={item.ID}) via Send+DeliveryMethod");
                }
                else
                {
                    var send1 = net.GetType().GetMethods().FirstOrDefault(m =>
                        m.Name == "Send" && m.GetParameters().Length == 1);
                    if (send1 != null)
                    {
                        send1.Invoke(net, new object[] { msg });
                        Log($"Net: broadcast [{affix.Identifier}] for {item.Name} (ID={item.ID}) via Send");
                    }
                    else Warning("BroadcastAffixApplied: no usable Networking.Send overload");
                }
            }
            catch (Exception ex)
            {
                Warning($"BroadcastAffixApplied failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 注册词缀同步接收器。优先走兼容接口的 LuaCsAction（object[] 包参，跨版本稳定）；
        /// 旧版本没有该接口时用表达式树按 Receive 委托的真实签名动态构造，避免硬编码参数个数。
        /// </summary>
        static void RegisterNetReceiver()
        {
            try
            {
                var net = LuaCsSetup.Instance.Networking;
                if (net is Barotrauma.LuaCs.Compatibility.ILuaCsNetworking compat)
                {
                    compat.Receive(NET_APPLY_AFFIX, (LuaCsAction)(args => HandleApplyAffixMessage(args)));
                    Log($"Net: receiver [{NET_APPLY_AFFIX}] registered via ILuaCsNetworking");
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
                Log($"Net: receiver [{NET_APPLY_AFFIX}] registered via reflection ({delType.Name})");
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
                var item = FindItemById(itemId);
                Log($"Net: received affix [{affixId}] for itemId={itemId}, itemFound={item != null && !item.Removed}");
                if (item == null || item.Removed) return;
                // 只有"已是同一个词缀"才跳过；重复附魔换成新词缀时必须走 ApplyAffix 的替换流程
                //（注销旧效果、恢复旧属性、清旧标签），否则客户端永远停留在第一次附魔的状态
                if (ItemAffixes.TryGetValue(item.ID, out var existing) && existing.AffixId == affixId)
                {
                    Log($"Net: item {item.Name} (ID={item.ID}) already has affix [{affixId}], skipping");
                    return;
                }
                if (AffixDefs.TryGetValue(affixId, out var def))
                {
                    ApplyAffix(item, def);
                    Log($"Net: applied [{affixId}] to {item.Name} (ID={item.ID})");
                }
                else
                {
                    Warning($"Net: unknown affix identifier [{affixId}]");
                }
            }
            catch (Exception ex)
            {
                Warning($"HandleApplyAffixMessage failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 组件级属性词条：射速（Reload ÷ 倍率）、散布（Spread × 倍率）、技能需求（RequiredSkills.Level × 倍率）。
        /// 原值记入 AffixData，移除词缀时恢复，避免污染存档（这些值不序列化）。
        /// </summary>
        static void ApplyStatChanges(Item item, AffixDef affix, AffixData data)
        {
            if (item.Components == null) return;
            bool hasFireRate = Math.Abs(affix.FireRateMult - 1f) >= 0.0001f;
            bool hasSpread = Math.Abs(affix.SpreadMult - 1f) >= 0.0001f;
            bool hasSkillReq = Math.Abs(affix.SkillReqMult - 1f) >= 0.0001f;
            if (!hasFireRate && !hasSpread && !hasSkillReq) return;

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

                // 技能需求词条：组件实例级 RequiredSkills 的 Level 乘算。
                // 注意绝不能改 prefab 的 SkillRequirementHint——那是全类型共享的显示数据。
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
        }

        /// <summary>读原值→记录→写新值；原值存进 AffixData.PropChanges，移除词缀时恢复</summary>
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

        static void RestoreStatChanges(AffixData data)
        {
            if (data == null) return;
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
        }

        static void RegisterEffectsForDisplay(Item item, AffixDef affix)
        {
            if (affix.Effects == null || affix.Effects.Count == 0) return;
            if (item.Components == null) return;

            foreach (var component in item.Components)
            {
                if (component.statusEffectLists == null) continue;
                foreach (var effect in affix.Effects)
                {
                    // 带 interval 的命中特效不注册进组件列表：
                    // 引擎的 interval 按命中次数递减（每击 -1.0s），高射速武器会更快烧完冷却，
                    // 这类效果改由注入补丁以真实秒数统一管理
                    if (effect.Interval > 0f) continue;
                    if (!component.statusEffectLists.TryGetValue(effect.type, out var list))
                    {
                        list = new List<StatusEffect>();
                        component.statusEffectLists.Add(effect.type, list);
                    }
                    if (!list.Contains(effect))
                        list.Add(effect);

                    // OnWearing 由 Item.ApplyStatusEffects 驱动，它只读 Item 构造时合并好的
                    // 物品级 statusEffectLists——运行时注册必须同步进物品级列表并打开快速检查位
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
            LuaCsLogger.LogMessage($"[ItemAffixes]:{msg ?? "null"}", color.Value * 0.8f, color.Value);
        }

        public static void Warning(object msg)
        {
            LuaCsLogger.LogMessage($"[ItemAffixes]:{msg ?? "null"}", Color.Yellow);
        }

        public static (string tierKey, TierWeights weights)? TryGetEnchantingTarget(IEnumerable<Item> inputItems, out Item weapon, out Item material)
        {
            weapon = null;
            material = null;
            string materialKey = null;

            foreach (var item in inputItems)
            {
                if (item == null) continue;
                if (IsEnchantableItem(item) && weapon == null)
                {
                    weapon = item;
                }
                else
                {
                    // HasTag 走哈希集合，O(1)；不再 Split 分配字符串数组做预筛
                    var key = MaterialTiers.Keys.FirstOrDefault(k => item.HasTag(k));
                    // 多种材料同时放入时取最高档：数字越小越高级（affixes_1 > affixes_2 > affixes_3）
                    if (key != null && (material == null || MaterialTierRank(key) < MaterialTierRank(materialKey)))
                    {
                        material = item;
                        materialKey = key;
                    }
                }
            }

            if (weapon == null || material == null) return null;
            if (materialKey == null) return null;
            return (materialKey, MaterialTiers[materialKey]);
        }

        /// <summary>
        /// 可附魔物品：武器 / 工具 / 医疗 / 有减伤的护甲 / 纯穿戴功能装备。
        /// 纯装饰道具（无 Wearable 组件的摆件类）不可附魔——词缀只给到有意义的物品上。
        /// </summary>
        static bool IsEnchantableItem(Item item)
        {
            return IsMeleeWeapon(item) || IsRangedWeapon(item) || item.HasTag("weapon")
                || IsTool(item) || IsMedicalItem(item)
                || IsOuterClothes(item);
        }

        /// <summary>材料档位序号：affixes_material_1 → 1（最高级），数字越大越低档；解析失败返回 int.MaxValue</summary>
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
                if (AffixDefs.TryGetValue(weights.FixedAffix, out var fixedDef) && IsAffixApplicable(fixedDef, item))
                    return fixedDef;
            }

            var pool = new List<(string tier, float weight)>
            {
                ("Broken", weights.Broken), ("Normal", weights.Normal), ("Rare", weights.Rare),
                ("Epic", weights.Epic), ("Legendary", weights.Legendary), ("Special", weights.Special)
            }.Where(t => t.weight > 0).ToList();

            if (pool.Count == 0) return null;

            float total = pool.Sum(t => t.weight);
            var deterministicRand = new System.Random(seed);
            float roll = (float)(deterministicRand.NextDouble() * total);
            float cumulative = 0;
            string chosenTier = pool[0].tier;
            foreach (var (tier, weight) in pool)
            {
                cumulative += weight;
                if (roll <= cumulative) { chosenTier = tier; break; }
                chosenTier = tier;
            }

            var candidates = AffixDefs.Values.Where(a => a.Tier == chosenTier && IsAffixApplicable(a, item)).ToList();
            if (candidates.Count == 0)
            {
                candidates = AffixDefs.Values.Where(a => IsAffixApplicable(a, item)).ToList();
            }
            if (candidates.Count == 0) return null;

            int idx = deterministicRand.Next(0, candidates.Count);
            return candidates[idx];
        }
    }

    public class AffixData
    {
        public string AffixId;
        public string Tier;
        public string NamePrefix;
        public Color DisplayColor;
        public List<StatusEffect> Effects;
        /// <summary>被属性词条（射速/散布等）修改过的组件属性（缓存的属性信息 + 目标 + 原值），移除词缀时恢复</summary>
        public List<(PropertyInfo Prop, object Target, float Original)> PropChanges;
        /// <summary>被技能需求词条修改过的 Skill 及其原始等级，移除词缀时恢复</summary>
        public List<(object Skill, float OriginalLevel)> SkillChanges;
    }

    public class AffixDef
    {
        public string Identifier;
        public string Tier;
        public string NamePrefix;
        public string Applicable;
        /// <summary>词缀效果说明，显示在物品描述下方（Affixes.xml 的 desc 属性）</summary>
        public string Description = "";
        /// <summary>护甲/穿戴物形态的效果说明（descarmor 属性，仅武器+护甲两用词条需要）</summary>
        public string DescriptionArmor = "";
        // 本地化解析结果（语言切换时 LocalizedString 自动更新）
        public LocalizedString NamePrefixLoc;
        public LocalizedString DescriptionLoc;
        public LocalizedString DescriptionArmorLoc;
        public string DisplayPrefix => NamePrefixLoc?.ToString() ?? NamePrefix;
        public string DisplayDesc => DescriptionLoc?.ToString() ?? Description;
        public string DisplayDescArmor => DescriptionArmorLoc?.ToString() ?? DescriptionArmor;
        public Color DisplayColor;
        public List<StatusEffect> Effects;
        /// <summary>伤害倍率（近战乘 Attack.DamageMultiplier，枪械乘投射物 damageMultiplier）</summary>
        public float DamageMult = 1f;
        /// <summary>射速/挥速倍率（实现为 Reload /= 倍率）</summary>
        public float FireRateMult = 1f;
        /// <summary>穿戴者受到攻击伤害的倍率（>1 易伤 / <1 减伤）</summary>
        public float DamageTakenMult = 1f;
        /// <summary>工具耗材消耗倍率（按引擎实际消耗量比例增减）</summary>
        public float FuelConsumeMult = 1f;
        /// <summary>修理成功时按目标最大耐久额外恢复的比例/秒</summary>
        public float RepairBonusPercent = 0f;
        /// <summary>枪械散布倍率（实现为 Spread/UnskilledSpread × 倍率，<1 更精准）</summary>
        public float SpreadMult = 1f;
        /// <summary>使用所需技能等级倍率（实现为组件 RequiredSkills 的 Level × 倍率）</summary>
        public float SkillReqMult = 1f;
        /// <summary>未识别的自定义 XML 属性原样收在这里（键小写不敏感由写入方保证原样，读取自行 OrdinalIgnoreCase），供后续拓展词缀使用</summary>
        public Dictionary<string, string> CustomProps;
        /// <summary>读自定义属性（不区分大小写），不存在返回 null</summary>
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
