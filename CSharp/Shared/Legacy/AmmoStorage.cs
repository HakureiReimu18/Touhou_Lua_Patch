using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Client = Barotrauma.Networking.Client;
using IReadMessage = Barotrauma.Networking.IReadMessage;
using IWriteMessage = Barotrauma.Networking.IWriteMessage;
using ILuaCsNetworking = Barotrauma.LuaCs.Compatibility.ILuaCsNetworking;

#if CLIENT
using Microsoft.Xna.Framework.Input;
#endif

namespace Touhou.AmmoStorage
{
    /// <summary>
    /// v5：弹药袋即弹匣（每种弹一个袋子）+ 空袋自选 + 「装填」按钮。
    /// 独立文件 / 独立命名空间 / 独立 Harmony 实例（touhou.ammostorage），补丁逐类手动注册，Dispose 里全卸干净。
    /// 配置：Config/magazine_config.xml（缺文件自动生成模板），mag_reload 热重载、mag_list 查看、mag_debug 切日志。
    ///
    /// ── v5 数据模型（Items/AmmoStorage.xml 顶部注释为权威说明） ──
    ///   · 弹种袋 condition = 袋内存量（余弹），上限 = Health（配置里每个弹种可单独调）。
    ///   · 袋内「待发实体弹」数 ∈ [0, min(N, condition)]，N = 该武器最大齐射弹数（配置 readyRounds，
    ///     默认 1 / 蓝玫瑰 2 / 痛苦 5）。引擎只看得见这几发，齐射数/伤害/射速还是武器原设计。
    ///   · 插弹药 → 吸收成耐久（实体物品移除）+ condition+1 → 补足待发弹；
    ///     开火消耗 / 从袋里拖走一发 → condition−1 → 补足待发弹（只从存量里挪，不凭空生弹）。
    ///   · condition=0（新出的袋子/打空）→ 武器判「无弹药」，不会空放。
    ///   · 没有单独的弹匣物品了：袋子自己就是插进武器的那件东西（武器按 ammo tag 收它）。
    ///   · 权威端（单人/服务器）才改 condition、生成/移除实体、转移物品；客户端只做本地校验/拒绝与提示。
    /// </summary>
    public sealed class AmmoPouchPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public static readonly string[] CommandNames = { "mag_reload", "mag_list", "mag_debug" };

        static string packageDir;
        /// <summary>模组目录：优先用主插件已解析的路径，插件初始化顺序不保证它先跑。</summary>
        public static string PackageDir
        {
            get
            {
                if (packageDir != null) return packageDir;
                packageDir = Touhou.Affixes.Mod.Package?.Dir;
                if (string.IsNullOrEmpty(packageDir) &&
                    LuaCsSetup.Instance.PluginPackageManager.TryGetPackageForPlugin<AmmoPouchPlugin>(out var pkg))
                    packageDir = pkg.Dir;
                return packageDir ?? ".";
            }
        }

        public void Initialize()
        {
            harmony = new Harmony("touhou.ammostorage");
        }

        // ⚠ 封存开关（2026-10）：整套弹药袋系统暂停——作者认为效果不理想，之后重做。
        // 恢复办法：把下面 SEALED 改成 false（或删掉这两行 + OnLoadCompleted 里的 if (SEALED) 分支），
        // 其余代码原样可用；物品侧记得同时把 filelist.xml 里的 Items/AmmoStorage.xml 与
        // Lua/Autorun/init.lua 里那三个 dofile 的反注释一起恢复。
        static readonly bool SEALED = true;

        public void OnLoadCompleted()
        {
            if (SEALED)
            {
                PouchLog.Log("弹药袋体系已封存（AmmoStorage.cs 的 SEALED）：补丁 / 命令 / 网络消息 / 钩子均未注册");
                return;
            }
            if (oneTimeInitDone)
            {
                PouchLog.Log("OnLoadCompleted：内容重载，补丁/命令已注册，跳过");
                return;
            }
            oneTimeInitDone = true;

            // 先清掉本插件名下的历史补丁再重打，防止 reloadlua 残留堆叠
            harmony.UnpatchSelf();

            PouchConfig.Load();
            RegisterPatches();

            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.AmmoStorage.RoundStart", OnRoundStart);
            LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.AmmoStorage.RoundEnd", OnRoundEnd);
            PouchReloadNet.Register();
            RegisterCommands();

            PouchLog.Log("弹药袋体系 v5 插件已加载");
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.AmmoStorage.RoundStart");
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.AmmoStorage.RoundEnd");
            foreach (var cmd in CommandNames)
                LuaCsSetup.Instance.Game.RemoveCommand(cmd);
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
            PouchRuntime.Clear();
            PouchLog.ClearThrottle();
            PouchLog.Log("弹药袋体系 v5 插件已卸载");
        }

        /// <summary>按 TargetMethod 解析器手动打补丁：目标缺失只警告，不拖垮其余补丁。</summary>
        void Register(Type patchType, string resolver, string prefix, string postfix)
        {
            try
            {
                var target = patchType
                    .GetMethod(resolver, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.Invoke(null, null) as MethodBase;
                if (target == null)
                {
                    PouchLog.Warn($"{patchType.Name}.{resolver}：目标方法未找到，跳过");
                    return;
                }
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(patchType, prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(patchType, postfix) : null);
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"{patchType.Name} 注册失败：{ex.Message}");
            }
        }

        void RegisterPatches()
        {
            Register(typeof(PouchContainPatch), nameof(PouchContainPatch.TargetMethodContained),
                null, nameof(PouchContainPatch.OnContainedPostfix));
            Register(typeof(PouchContainPatch), nameof(PouchContainPatch.TargetMethodRemoved),
                null, nameof(PouchContainPatch.OnRemovedPostfix));
            Register(typeof(PouchFirePatch), nameof(PouchFirePatch.TargetMethod),
                nameof(PouchFirePatch.Prefix), nameof(PouchFirePatch.Postfix));
            Register(typeof(PouchPickupPatch), nameof(PouchPickupPatch.TargetMethod),
                nameof(PouchPickupPatch.Prefix), null);
            Register(typeof(PouchFillPatch), nameof(PouchFillPatch.TargetMethod),
                nameof(PouchFillPatch.Prefix), null);
            Register(typeof(PouchTickPatch), nameof(PouchTickPatch.TargetMethod),
                null, nameof(PouchTickPatch.Postfix));
        }

        object OnRoundStart(object[] args)
        {
            PouchConfig.Load();
            PouchRuntime.Clear();
            PouchLog.ClearThrottle();
            // 读档后立刻兜一次：存档可能正好卡在"打掉一发、补足待发弹的物品还在生成队列"的瞬间，
            // 不修的话玩家一进场就是"有弹药却打不响"。只权威端动手，加载中的物品会被跳过。
            PouchRuntime.SweepDeficientPouches();
            return null;
        }

        object OnRoundEnd(object[] args)
        {
            PouchRuntime.Clear();
            PouchLog.ClearThrottle();
            return null;
        }

        void RegisterCommands()
        {
            var game = LuaCsSetup.Instance.Game;
            game.AddCommand("mag_reload", "重载弹药袋体系配置 Config/magazine_config.xml", args =>
            {
                PouchConfig.Load();
            }, null, false);
            game.AddCommand("mag_list", "列出弹药袋体系配置与场上袋子统计", args =>
            {
                PouchConfig.DumpToLog();
                PouchRuntime.DumpStats();
            }, null, false);
            game.AddCommand("mag_debug", "切换弹药袋体系的调试日志", args =>
            {
                PouchLog.Verbose = !PouchLog.Verbose;
                // 调试开关的反馈走日志/控制台即可，不用占屏幕滚动消息
                LuaCsLogger.LogMessage($"[弹药袋] 调试日志：{(PouchLog.Verbose ? "开" : "关")}", Color.LightGreen);
            }, null, false);
        }
    }

    /// <summary>
    /// 玩家提示用的文本键与取词。键名/文本由作者维护在 Text/*.xml；取不到键就退回硬编码中文，
    /// 保证没补文本也能玩。占位符按作者文本里的 %s / %d 顺序替换（同一句里可能出现两次 %s，
    /// 所以自己按顺序扫，不用 LocalizedString.Replace —— 它没法区分同名的多个占位符）。
    /// </summary>
    public static class PouchText
    {
        public const string KeyNeedReload = "touhou.ammopouch.msg.needreload";
        public const string KeyReloadDone = "touhou.ammopouch.msg.reloaddone";
        public const string KeyPouchEmpty = "touhou.ammopouch.msg.pouchempty";
        public const string KeyPouchFull = "touhou.ammopouch.msg.pouchfull";
        public const string KeyWrongAmmo = "touhou.ammopouch.msg.wrongammo";
        public const string KeyLoaded = "touhou.ammopouch.msg.loaded";
        // 这几个键作者没列，先用硬编码兜底；补了文本会自动生效
        public const string KeyNothingToLoad = "touhou.ammopouch.msg.nothingtoload";
        public const string KeyNotAllowed = "touhou.ammopouch.msg.notallowed";
        public const string KeyNoHolder = "touhou.ammopouch.msg.noholder";

        /// <summary>取本地化文本（缺键/取词失败退回 fallback），再按顺序填 %s/%d 占位符</summary>
        public static string Format(string key, string fallback, params string[] args)
        {
            string text = fallback;
            try
            {
                var localized = TextManager.Get(key).Fallback(fallback, true);
                if (localized != null)
                {
                    string value = localized.Value;
                    if (!string.IsNullOrEmpty(value)) text = value;
                }
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"取文本键 {key} 失败：{ex.Message}");
            }
            return ApplyArgs(text, args);
        }

        static string ApplyArgs(string text, string[] args)
        {
            if (string.IsNullOrEmpty(text) || args == null || args.Length == 0) return text;
            int next = 0;
            var sb = new System.Text.StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '%' || i + 1 >= text.Length) { sb.Append(c); continue; }
                char spec = text[i + 1];
                if (spec == '%') { sb.Append('%'); i++; continue; }      // %% → 字面量 %
                if (spec != 's' && spec != 'd' && spec != 'i' && spec != 'f') { sb.Append(c); continue; }
                if (next < args.Length) sb.Append(args[next++] ?? "");
                i++; // 占位符用光了就把 %x 原样去掉，免得漏出奇怪的符号
            }
            return sb.ToString();
        }
    }

    public static class PouchLog
    {
        /// <summary>低频信息日志开关（插件加载/配置加载/结算细节）。默认关，mag_debug 切换。</summary>
        public static bool Verbose = false;

        public static void Log(string msg)
        {
            if (Verbose) LuaCsLogger.LogMessage($"[弹药袋] {msg}", Color.LightGreen);
        }

        public static void Warn(string msg) => LuaCsLogger.LogMessage($"[弹药袋] {msg}", Color.Orange);

        // 提示去重：同一 key 一段时间内只出一条
        static readonly Dictionary<string, double> throttled = new Dictionary<string, double>();

        public static void LogThrottled(string key, double intervalSeconds, string msg)
        {
            if (throttled.TryGetValue(key, out double next) && Timing.TotalTime < next) return;
            throttled[key] = Timing.TotalTime + intervalSeconds;
            LuaCsLogger.LogMessage($"[弹药袋] {msg}", Color.Orange);
        }

        /// <summary>
        /// 给玩家看的提示：走原版那种屏幕上方滚动消息（GUI.AddMessage，原版 ItemMsgAmmoRequired 同款）。
        /// 只在单机/客户端上下文弹——服务端与专用服务器没有 HUD，只记日志。
        /// 注意 GUI 只在客户端程序集里存在，所以真正调用点裹在 #if CLIENT 里。
        /// </summary>
        public static void Notify(string textKey, string fallbackText, Color color, params string[] args)
        {
            Show(PouchText.Format(textKey, fallbackText, args), color);
        }

        /// <summary>同上，但同一 key 在 intervalSeconds 内只弹一条（"需要装填"会被按住开火键狂刷）</summary>
        public static void NotifyThrottled(string textKey, double intervalSeconds, string fallbackText,
            Color color, params string[] args)
        {
            if (throttled.TryGetValue(textKey, out double next) && Timing.TotalTime < next) return;
            throttled[textKey] = Timing.TotalTime + intervalSeconds;
            Show(PouchText.Format(textKey, fallbackText, args), color);
        }

        static void Show(string message, Color color)
        {
            if (string.IsNullOrEmpty(message)) return;
            // 服务端/专用服务器不弹 HUD（它们也该看到信息，但只能落到日志里）
            bool hasHud = GameMain.NetworkMember == null || GameMain.NetworkMember.IsClient;
            if (!hasHud)
            {
                LuaCsLogger.LogMessage($"[弹药袋] {message}", color);
                return;
            }
#if CLIENT
            try { GUI.AddMessage(message, color); }
            catch (Exception ex) { Warn($"提示弹出失败：{ex.Message}"); }
#else
            LuaCsLogger.LogMessage($"[弹药袋] {message}", color);
#endif
        }

        public static void ClearThrottle() => throttled.Clear();
    }

    /// <summary>一个弹种袋：identifier + 专属弹药 + 容量（= 存量上限，XML 的 Health）。</summary>
    public sealed class PouchEntry
    {
        public string Id;
        public string AmmoId;
        public int Capacity;
        /// <summary>0 = 没配，运行时按"能用这种弹的武器"的 readyRounds 反查</summary>
        public int ReadyRounds;
    }

    /// <summary>一把武器：允许的弹药 + 最大齐射弹数 N + 原装填上限（本次装填能打几发）。</summary>
    public sealed class PouchWeaponEntry
    {
        public string Id;
        public int ReadyRounds = 1;
        /// <summary>
        /// 该武器原本的装填上限（= 武器 XML 里 ItemContainer 的 maxstacksize：转轮 18 / 蓝玫瑰 12 /
        /// 痛苦 5 / 喇叭枪 12 / 退魔 7）。插袋或按装填键时"本次装填量" = min(magCap, 袋内存量)，
        /// 打满就拦开火、要求重新装填——这是防止通用袋子把小容量武器变强的那道闸。
        /// 0 = 不设上限（老行为：只要有存量就一直打）。
        /// </summary>
        public int MagCap = 18;
        public readonly List<string> Ammo = new List<string>();

        public bool HasCap => MagCap > 0;

        public bool Allows(Item ammo)
        {
            if (ammo?.Prefab == null) return false;
            string id = ammo.Prefab.Identifier.Value;
            for (int i = 0; i < Ammo.Count; i++)
            {
                if (string.Equals(Ammo[i], id, StringComparison.OrdinalIgnoreCase)) return true;
                // 也允许按 tag 匹配（换皮弹药只改 identifier 时不用改配置）
                if (ammo.HasTag(Ammo[i])) return true;
            }
            return false;
        }

        public bool AllowsAmmoId(string ammoId)
        {
            if (string.IsNullOrEmpty(ammoId)) return false;
            for (int i = 0; i < Ammo.Count; i++)
            {
                if (string.Equals(Ammo[i], ammoId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 配置：Config/magazine_config.xml。缺文件自动生成模板；代码里内置同样的默认表，
    /// 配置缺失/写坏也能跑（退回默认值）。roundStart 与 mag_reload 重新加载。
    /// </summary>
    public static class PouchConfig
    {
        public static bool AllowUnlistedWeapons = false;
        /// <summary>从存量补待发实体弹的最小间隔（秒）</summary>
        public static float PouchRefillInterval = 0.25f;
        /// <summary>装填快捷键（XNA Keys 名）</summary>
        public static string LoadKey = "R";
        /// <summary>装填耗时（秒）：装填时给武器加这么多 ReloadTimer</summary>
        public static float ReloadDelay = 1.2f;
        public const string FillMarkerId = "Touhou_Ammo_Fill_Marker";
        public const string BlankPouchId = "Touhou_Ammo_Pouch_Blank";

        static readonly Dictionary<string, PouchEntry> pouches =
            new Dictionary<string, PouchEntry>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, PouchWeaponEntry> weapons =
            new Dictionary<string, PouchWeaponEntry>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> ammoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string ConfigPath => Path.Combine(AmmoPouchPlugin.PackageDir, "Config", "magazine_config.xml");

        public static bool TryGetPouch(string identifier, out PouchEntry entry)
        {
            entry = null;
            return !string.IsNullOrEmpty(identifier) && pouches.TryGetValue(identifier, out entry);
        }

        public static bool TryGetWeapon(string identifier, out PouchWeaponEntry entry)
        {
            entry = null;
            return !string.IsNullOrEmpty(identifier) && weapons.TryGetValue(identifier, out entry);
        }

        /// <summary>能用这种弹的武器（袋子没插在枪上时用它反查 readyRounds）</summary>
        public static PouchWeaponEntry FindWeaponByAmmoId(string ammoId)
        {
            if (string.IsNullOrEmpty(ammoId)) return null;
            foreach (var kv in weapons)
            {
                if (kv.Value.AllowsAmmoId(ammoId)) return kv.Value;
            }
            return null;
        }

        /// <summary>是不是配置里认识的弹药（袋子的 Containable 已经限死单一弹种，这里再兜一道）</summary>
        public static bool IsKnownAmmo(Item item) =>
            item?.Prefab != null && ammoIds.Contains(item.Prefab.Identifier.Value);

        public static void Load()
        {
            ResetToCodeDefaults();
            EnsureOwnTemplate();

            int files = 0;
            try
            {
                // 扫描所有启用内容包的 Config/magazine_config.xml，按加载顺序合并（同 id 后者覆盖前者）
                foreach (var pkg in ContentPackageManager.RegularPackages)
                {
                    string path;
                    try { path = Path.Combine(pkg.Dir, "Config", "magazine_config.xml"); }
                    catch { continue; }
                    if (!File.Exists(path)) continue;
                    MergeFile(path);
                    files++;
                }
            }
            catch (Exception ex) { PouchLog.Warn($"配置扫描失败：{ex.Message}"); }

            RebuildAmmoIndex();
            PouchLog.Log($"配置已加载：文件 {files}，弹种袋 {pouches.Count}，武器条目 {weapons.Count}，" +
                         $"补待发弹最小间隔 {PouchRefillInterval:F2}s，装填键 {LoadKey}，装填耗时 {ReloadDelay:F2}s，" +
                         $"未列出武器 {(AllowUnlistedWeapons ? "允许" : "拒绝插入")}");
        }

        /// <summary>代码内置默认表：配置文件缺失/被删/写坏时的兜底，与 magazine_config.xml 模板一致。</summary>
        static void ResetToCodeDefaults()
        {
            AllowUnlistedWeapons = false;
            PouchRefillInterval = 0.25f;
            LoadKey = "R";
            ReloadDelay = 1.2f;

            pouches.Clear();
            AddDefaultPouch("Touhou_Ammo_Pouch_Revolver", "Touhou_Maple_Leaf_Revolver_Bullet", 128);
            AddDefaultPouch("Touhou_Ammo_Pouch_BlueRose", "Color_Up_Bullet", 128);
            AddDefaultPouch("Touhou_Ammo_Pouch_Anguish", "Touhou_Anguish_Bullet", 128);
            AddDefaultPouch("Touhou_Ammo_Pouch_Blunderbuss", "Mind_Bullet", 128);
            AddDefaultPouch("Touhou_Ammo_Pouch_Winchester", "Exorcism_Bullet", 128);

            weapons.Clear();
            // magCap = 该武器原装填上限（武器 XML 的 ItemContainer maxstacksize）
            AddDefaultWeapon("Touhou_Maple_Leaf_Revolver", 1, 18, "Touhou_Maple_Leaf_Revolver_Bullet");
            AddDefaultWeapon("Touhou_Blue_Rose", 2, 12, "Color_Up_Bullet");
            AddDefaultWeapon("Touhou_Anguish", 5, 5, "Touhou_Anguish_Bullet");
            AddDefaultWeapon("Blunderbuss", 1, 12, "Mind_Bullet");
            AddDefaultWeapon("Touhou_Winchester", 1, 7, "Exorcism_Bullet");
        }

        static void AddDefaultPouch(string id, string ammoId, int capacity)
        {
            pouches[id] = new PouchEntry { Id = id, AmmoId = ammoId, Capacity = capacity, ReadyRounds = 0 };
        }

        static void AddDefaultWeapon(string id, int readyRounds, int magCap, params string[] ammo)
        {
            var e = new PouchWeaponEntry
            {
                Id = id,
                ReadyRounds = Math.Max(1, readyRounds),
                MagCap = Math.Max(0, magCap)
            };
            e.Ammo.AddRange(ammo);
            weapons[id] = e;
        }

        static void RebuildAmmoIndex()
        {
            ammoIds.Clear();
            foreach (var kv in pouches)
            {
                if (!string.IsNullOrEmpty(kv.Value.AmmoId)) ammoIds.Add(kv.Value.AmmoId);
            }
            foreach (var kv in weapons)
            {
                for (int i = 0; i < kv.Value.Ammo.Count; i++) ammoIds.Add(kv.Value.Ammo[i]);
            }
        }

        static void EnsureOwnTemplate()
        {
            if (File.Exists(ConfigPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath, TemplateXml);
                PouchLog.Log("配置文件不存在，已生成模板");
            }
            catch (Exception ex) { PouchLog.Warn($"配置模板写入失败：{ex.Message}"); }
        }

        static void MergeFile(string path)
        {
            XElement root;
            try { root = XDocument.Load(path).Root; }
            catch (Exception ex) { PouchLog.Warn($"配置解析失败（{path}）：{ex.Message}"); return; }
            if (root == null) return;

            AllowUnlistedWeapons = ParseBool(SafeAttr(root, "allowUnlistedWeapons"), AllowUnlistedWeapons);
            PouchRefillInterval = ParseFloat(SafeAttr(root, "pouchRefillInterval"), PouchRefillInterval);
            LoadKey = SafeAttr(root, "loadKey") ?? LoadKey;
            ReloadDelay = ParseFloat(SafeAttr(root, "reloadDelay"), ReloadDelay);

            foreach (var el in root.Elements("Pouch"))
            {
                string id = SafeAttr(el, "id");
                if (string.IsNullOrEmpty(id)) { PouchLog.Warn("配置 <Pouch> 缺 id，已跳过"); continue; }
                string ammoId = SafeAttr(el, "ammo");
                int capacity = ParseInt(SafeAttr(el, "capacity"), 0);
                int readyRounds = ParseInt(SafeAttr(el, "readyRounds"), 0);
                if (string.IsNullOrEmpty(ammoId) && pouches.TryGetValue(id, out var old)) ammoId = old.AmmoId;
                if (capacity <= 0) capacity = 1;
                pouches[id] = new PouchEntry
                {
                    Id = id,
                    AmmoId = ammoId,
                    Capacity = capacity,
                    ReadyRounds = readyRounds > 0 ? readyRounds : 0 // 0 = 运行期按武器反查
                };
            }

            foreach (var el in root.Elements("Weapon"))
            {
                string id = SafeAttr(el, "id");
                if (string.IsNullOrEmpty(id)) { PouchLog.Warn("配置 <Weapon> 缺 id，已跳过"); continue; }
                var entry = new PouchWeaponEntry
                {
                    Id = id,
                    ReadyRounds = Math.Max(1, ParseInt(SafeAttr(el, "readyRounds"), 1)),
                    // magCap 缺省沿用代码默认表里的值（默认表已按武器原装填上限填好），
                    // 显式写 magCap="0" 才是"不设装填上限"
                    MagCap = ParseInt(SafeAttr(el, "magCap"),
                        weapons.TryGetValue(id, out var oldW) ? oldW.MagCap : 18)
                };
                foreach (var ammoEl in el.Elements("Ammo"))
                {
                    string ammoId = SafeAttr(ammoEl, "id");
                    if (!string.IsNullOrEmpty(ammoId)) entry.Ammo.Add(ammoId);
                }
                weapons[id] = entry; // 逐条覆盖代码默认表，没写到的默认武器继续生效
            }
        }

        static string SafeAttr(XElement el, string name)
        {
            try { return el?.Attribute(name)?.Value; } catch { return null; }
        }

        static float ParseFloat(string raw, float fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return v;
            PouchLog.Warn($"参数 \"{raw}\" 解析失败，用 {fallback}");
            return fallback;
        }

        static int ParseInt(string raw, int fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            PouchLog.Warn($"参数 \"{raw}\" 解析失败，用 {fallback}");
            return fallback;
        }

        static bool ParseBool(string raw, bool fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (bool.TryParse(raw, out bool v)) return v;
            PouchLog.Warn($"参数 \"{raw}\" 解析失败，用 {fallback}");
            return fallback;
        }

        public static void DumpToLog()
        {
            LuaCsLogger.LogMessage($"[弹药袋] 补待发弹最小间隔 {PouchRefillInterval:F2}s；装填键 {LoadKey}，装填耗时 {ReloadDelay:F2}s；" +
                                   $"未列出武器：{(AllowUnlistedWeapons ? "允许插入" : "拒绝插入")}；" +
                                   $"空袋 {BlankPouchId}（选弹种在 Lua 侧），装填标记 {FillMarkerId}", Color.LightGreen);
            foreach (var p in pouches.Values)
            {
                LuaCsLogger.LogMessage($"[弹药袋]   袋子 {p.Id}：弹药 {p.AmmoId}，容量 {p.Capacity}，" +
                                       $"N={(p.ReadyRounds > 0 ? p.ReadyRounds.ToString() : "按武器反查")}", Color.LightGreen);
            }
            foreach (var w in weapons.Values)
            {
                LuaCsLogger.LogMessage($"[弹药袋]   武器 {w.Id}：N={w.ReadyRounds}，" +
                                       $"装填上限 {(w.HasCap ? w.MagCap.ToString() : "不限")}，" +
                                       $"允许弹药 [{string.Join(", ", w.Ammo)}]",
                    Color.LightGreen);
            }
        }

        const string TemplateXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<AmmoPouchConfig allowUnlistedWeapons=\"false\" pouchRefillInterval=\"0.25\" loadKey=\"R\" reloadDelay=\"1.2\">\n" +
            "  <!-- 详见模组内 Config/magazine_config.xml 的完整注释版本 -->\n" +
            "  <Pouch id=\"Touhou_Ammo_Pouch_Revolver\" ammo=\"Touhou_Maple_Leaf_Revolver_Bullet\" capacity=\"128\"/>\n" +
            "  <Pouch id=\"Touhou_Ammo_Pouch_BlueRose\" ammo=\"Color_Up_Bullet\" capacity=\"128\"/>\n" +
            "  <Pouch id=\"Touhou_Ammo_Pouch_Anguish\" ammo=\"Touhou_Anguish_Bullet\" capacity=\"128\"/>\n" +
            "  <Pouch id=\"Touhou_Ammo_Pouch_Blunderbuss\" ammo=\"Mind_Bullet\" capacity=\"128\"/>\n" +
            "  <Pouch id=\"Touhou_Ammo_Pouch_Winchester\" ammo=\"Exorcism_Bullet\" capacity=\"128\"/>\n" +
            "  <Weapon id=\"Touhou_Maple_Leaf_Revolver\" readyRounds=\"1\" magCap=\"18\"><Ammo id=\"Touhou_Maple_Leaf_Revolver_Bullet\"/></Weapon>\n" +
            "  <Weapon id=\"Touhou_Blue_Rose\" readyRounds=\"2\" magCap=\"12\"><Ammo id=\"Color_Up_Bullet\"/></Weapon>\n" +
            "  <Weapon id=\"Touhou_Anguish\" readyRounds=\"5\" magCap=\"5\"><Ammo id=\"Touhou_Anguish_Bullet\"/></Weapon>\n" +
            "  <Weapon id=\"Blunderbuss\" readyRounds=\"1\" magCap=\"12\"><Ammo id=\"Mind_Bullet\"/></Weapon>\n" +
            "  <Weapon id=\"Touhou_Winchester\" readyRounds=\"1\" magCap=\"7\"><Ammo id=\"Exorcism_Bullet\"/></Weapon>\n" +
            "</AmmoPouchConfig>\n";
    }

    /// <summary>物品识别与查找小工具。全部走已核对过的公开/友元 API，不做反射。</summary>
    public static class PouchItems
    {
        /// <summary>
        /// 是不是"弹种袋"。空袋（Touhou_Ammo_Pouch_Blank）故意不算：它没有 Touhou_Ammo_Pouch tag，
        /// 也不在配置表里，选完弹种由 Lua 侧换成真正的弹种袋。
        /// </summary>
        public static bool IsPouch(Item item)
        {
            if (item?.Prefab == null) return false;
            if (PouchConfig.TryGetPouch(item.Prefab.Identifier.Value, out _)) return true;
            return item.HasTag("Touhou_Ammo_Pouch");
        }

        /// <summary>「装填」按钮塞进来的标记物（它不是弹药，别被吸收/结算逻辑误伤）</summary>
        public static bool IsFillMarker(Item item)
        {
            if (item?.Prefab == null) return false;
            if (string.Equals(item.Prefab.Identifier.Value, PouchConfig.FillMarkerId, StringComparison.OrdinalIgnoreCase))
                return true;
            return item.HasTag(PouchConfig.FillMarkerId);
        }

        public static ItemContainer GetContainer(Item item) => item?.GetComponent<ItemContainer>();

        public static RangedWeapon GetWeapon(Item item) => item?.GetComponent<RangedWeapon>();

        public static bool IsWeapon(Item item) => GetWeapon(item) != null;

        /// <summary>
        /// 袋子对应的弹药 identifier。配置里有就用配置的；没有（作者新加了变体还没写配置）就
        /// 退回读它自己容器的 &lt;Containable&gt; —— XML 已经把每个袋子限死成单一弹种。
        /// </summary>
        public static string AmmoIdOf(Item pouch)
        {
            if (pouch?.Prefab == null) return null;
            if (PouchConfig.TryGetPouch(pouch.Prefab.Identifier.Value, out var entry) &&
                !string.IsNullOrEmpty(entry.AmmoId))
                return entry.AmmoId;
            var container = GetContainer(pouch);
            var ids = container?.ContainableItemIdentifiers;
            if (ids != null)
            {
                foreach (var id in ids) return id.Value;
            }
            return null;
        }

        /// <summary>
        /// 袋子存量上限。优先配置，再与 prefab Health 取小的那个，
        /// 免得配置写大了超过引擎的 condition 上限（Condition setter 会 clamp 到 MaxCondition）。
        /// </summary>
        public static int CapacityOf(Item pouch)
        {
            int cap = PouchConfig.TryGetPouch(pouch?.Prefab?.Identifier.Value, out var entry) && entry.Capacity > 0
                ? entry.Capacity
                : (int)Math.Round(Math.Max(1f, pouch?.MaxCondition ?? 1f));
            int byPrefab = (int)Math.Round(Math.Max(1f, pouch?.Prefab?.Health ?? 1f));
            return Math.Max(1, Math.Min(cap, byPrefab));
        }

        /// <summary>弹药 prefab（生成待发实体弹用）。查不到只警告。</summary>
        public static ItemPrefab FindAmmoPrefab(string ammoId)
        {
            if (string.IsNullOrEmpty(ammoId)) return null;
            if (ItemPrefab.Prefabs.TryGet(ammoId, out ItemPrefab prefab) && prefab != null) return prefab;
            PouchLog.Warn($"找不到弹药 prefab「{ammoId}」，无法生成待发实体弹");
            return null;
        }

        /// <summary>查弹药 prefab 但不打日志（校验用）</summary>
        static ItemPrefab PeekAmmoPrefab(string ammoId)
        {
            if (string.IsNullOrEmpty(ammoId)) return null;
            return ItemPrefab.Prefabs.TryGet(ammoId, out ItemPrefab prefab) ? prefab : null;
        }

        /// <summary>
        /// 该武器的弹药表认不认这种弹：先比 identifier，再拿弹药 prefab 的 tag 比一遍
        /// （换皮弹药只改 identifier、tag 沿用原版时不用改配置）。
        /// </summary>
        public static bool WeaponAllowsAmmoId(PouchWeaponEntry entry, string ammoId)
        {
            if (entry == null || string.IsNullOrEmpty(ammoId)) return false;
            if (entry.AllowsAmmoId(ammoId)) return true;
            var prefab = PeekAmmoPrefab(ammoId);
            if (prefab?.Tags == null) return false;
            foreach (var tag in prefab.Tags)
            {
                if (entry.AllowsAmmoId(tag.Value)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 运行时状态与业务逻辑。会改物品状态的一切操作都只在权威端执行（IsAuthority），
    /// 客户端只做本地校验/拒绝与提示，避免双端各算一份。
    /// </summary>
    public static class PouchRuntime
    {
        /// <summary>
        /// 权威端。本版本 GameMain 是 partial：客户端程序集里 NetworkMember =&gt; Client（GameClient），
        /// 服务端程序集里 =&gt; Server（GameServer）。单人（null）与服务器都算权威。
        /// </summary>
        public static bool IsAuthority =>
            GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;

        /// <summary>每帧最多处理几个袋子的"补待发弹"请求（按帧摊平）</summary>
        const int MaxChamberPerFrame = 4;

        /// <summary>
        /// 一把武器"本次装填还剩几发"。插袋/按装填键时 = min(magCap, 袋内存量)，每打一发 −1，
        /// 归 0 就拦开火要求重新装填。装填量打光后还剩的存量属于"储备"，不是连打上限。
        /// </summary>
        sealed class WeaponLoad
        {
            public ushort PouchId;
            public int Loaded;
            /// <summary>本次装填装了多少发（提示文案用）</summary>
            public int Size;
        }

        // 已吸收、等引擎本帧末移除的实体（OnItemRemoved 要跳过它们，不然会把"吸收"当成"打掉一发"）
        static readonly HashSet<ushort> absorbed = new HashSet<ushort>();
        // 容器 ID → 已排队待生成的实体弹数（生成是延后的，计数必须算上它）
        static readonly Dictionary<ushort, int> pendingSpawn = new Dictionary<ushort, int>();
        // 待补待发弹的袋子（事件驱动的请求，按 pouchRefillInterval 节流后执行）
        static readonly Dictionary<ushort, Item> chamberPending = new Dictionary<ushort, Item>();
        static readonly List<Item> chamberScratch = new List<Item>();
        static readonly Dictionary<ushort, double> nextChamberAt = new Dictionary<ushort, double>();
        // 武器实例 ID → 本次装填剩余发数（按武器实例记，插袋/装填时重置）
        static readonly Dictionary<ushort, WeaponLoad> weaponLoads = new Dictionary<ushort, WeaponLoad>();
        // 袋子"上一个容器"与"最近一次搬运它的角色"：插入被拒时用来放回原处
        static readonly Dictionary<ushort, Item> origin = new Dictionary<ushort, Item>();
        static readonly Dictionary<ushort, Character> droppers = new Dictionary<ushort, Character>();

        static double nextScanAt;
        /// <summary>注意：不在 RangedWeapon.Use 这段窗口里由 OnItemRemoved 扣 condition（改由开火补丁统一结算）</summary>
        static double inWeaponUseUntil;

        /// <summary>补丁内部搬动物品时置位，避免自己的操作被当成玩家操作再校验一遍</summary>
        public static bool Suppress;

        public static void Clear()
        {
            absorbed.Clear();
            pendingSpawn.Clear();
            chamberPending.Clear();
            chamberScratch.Clear();
            nextChamberAt.Clear();
            weaponLoads.Clear();
            origin.Clear();
            droppers.Clear();
            nextScanAt = 0; // 下一帧立刻补一次扫描
            inWeaponUseUntil = 0;
        }

        // ─────────────────────────────── 来源记录（拒绝插入时回退用） ───────────────────────────────

        public static void RecordOrigin(Item pouch, Item container)
        {
            if (pouch == null || container == null) return;
            origin[pouch.ID] = container;
        }

        public static Item TakeOrigin(Item pouch)
        {
            if (pouch == null) return null;
            if (origin.TryGetValue(pouch.ID, out var c)) { origin.Remove(pouch.ID); return c; }
            return null;
        }

        public static void RecordDropper(Item item, Character dropper)
        {
            if (item == null || dropper == null) return;
            if (droppers.Count > 512) droppers.Clear(); // 防长期累积
            droppers[item.ID] = dropper;
        }

        static Character TakeDropper(Item item)
        {
            if (item == null) return null;
            if (droppers.TryGetValue(item.ID, out var c)) { droppers.Remove(item.ID); return c; }
            return null;
        }

        // ─────────────────────────────── 查找 / 计数 ───────────────────────────────

        /// <summary>武器容器里插着的弹种袋（只扫一层：袋子直接放在武器的 ItemContainer 里）</summary>
        public static Item FindPouchInWeapon(Item weapon)
        {
            if (weapon == null) return null;
            foreach (var container in weapon.GetComponents<ItemContainer>())
            {
                var inv = container?.Inventory;
                if (inv == null) continue;
                foreach (var it in inv.AllItems)
                {
                    if (it != null && !it.Removed && PouchItems.IsPouch(it)) return it;
                }
            }
            return null;
        }

        static int PendingSpawns(Item container)
        {
            if (container == null) return 0;
            return pendingSpawn.TryGetValue(container.ID, out int n) ? n : 0;
        }

        /// <summary>
        /// 袋内待发实体弹数：真实存在的（已吸收待移除的不算）+ 已排队待生成的。
        /// 生成/移除都是延后到引擎本帧末的，不这样算就会重复生成或漏膛。
        /// </summary>
        public static int PhysicalCount(Item pouch)
        {
            int n = 0;
            var inv = pouch?.OwnInventory;
            if (inv != null)
            {
                foreach (var it in inv.AllItems)
                {
                    if (it == null || it.Removed || it.IsInRemoveQueue) continue;
                    if (PouchItems.IsFillMarker(it)) continue; // 标记物不是待发弹
                    n++;
                }
            }
            return n + PendingSpawns(pouch);
        }

        public static int ConditionOf(Item item) => item == null ? 0 : (int)Math.Round(item.Condition);

        /// <summary>
        /// 袋子的 N（最大齐射弹数）。优先级：配置给袋子写死的 readyRounds → 它正插着的那把枪 →
        /// 能用它这种弹的武器（袋子还没插进枪时就是这条）。
        /// </summary>
        public static int ReadyRoundsOf(Item pouch)
        {
            if (pouch?.Prefab == null) return 1;
            if (PouchConfig.TryGetPouch(pouch.Prefab.Identifier.Value, out var p) && p.ReadyRounds > 0)
                return p.ReadyRounds;
            var weapon = FindWeaponHolding(pouch);
            if (weapon?.Prefab != null && PouchConfig.TryGetWeapon(weapon.Prefab.Identifier.Value, out var w))
                return Math.Max(1, w.ReadyRounds);
            var byAmmo = PouchConfig.FindWeaponByAmmoId(PouchItems.AmmoIdOf(pouch));
            return byAmmo != null ? Math.Max(1, byAmmo.ReadyRounds) : 1;
        }

        /// <summary>袋子当前插在哪把枪里（没插在枪上返回 null）</summary>
        public static Item FindWeaponHolding(Item pouch)
        {
            var holder = pouch?.ParentInventory?.Owner as Item;
            if (holder == null) return null;
            return PouchItems.IsWeapon(holder) ? holder : null;
        }

        static void SetCondition(Item pouch, int value)
        {
            if (pouch == null) return;
            pouch.Condition = MathHelper.Clamp(value, 0, PouchItems.CapacityOf(pouch));
        }

        // ─────────────────────────────── 生成 / 移除 ───────────────────────────────

        /// <summary>
        /// 把待发实体弹排进生成队列并放进袋子。用"世界坐标 + onSpawned 手动放入"而不是
        /// AddItemToSpawnQueue 的 Inventory 重载：这样放入动作能罩在 Suppress 里，
        /// 容器补丁不会把我们自己的补弹当成玩家插弹（否则会再吸收一次、condition 连环涨）。
        /// </summary>
        static void SpawnAmmoInto(Item pouch, string ammoId, int count)
        {
            if (!IsAuthority || pouch == null || count <= 0) return;
            var prefab = PouchItems.FindAmmoPrefab(ammoId);
            if (prefab == null || Entity.Spawner == null) return;

            ushort id = pouch.ID;
            var inv = pouch.OwnInventory;
            pendingSpawn[id] = PendingSpawns(pouch) + count;
            for (int i = 0; i < count; i++)
            {
                Entity.Spawner.AddItemToSpawnQueue(prefab, pouch.Position, pouch.Submarine, null, null, spawned =>
                {
                    pendingSpawn[id] = Math.Max(0, (pendingSpawn.TryGetValue(id, out int p) ? p : 0) - 1);
                    if (spawned == null || spawned.Removed) return;
                    try
                    {
                        Suppress = true;
                        if (inv == null || !inv.TryPutItem(spawned, null, null))
                            Entity.Spawner?.AddItemToRemoveQueue(spawned); // 放不进去（容器没了/满了）就别留在世上
                    }
                    catch (Exception ex) { PouchLog.Warn($"补待发弹放入失败：{ex.Message}"); }
                    finally { Suppress = false; }
                });
            }
        }

        /// <summary>吸收一枚实体弹/标记物（排进移除队列，本帧末真正消失）</summary>
        public static void AbsorbItem(Item ammo)
        {
            if (ammo == null || ammo.Removed) return;
            absorbed.Add(ammo.ID);
            Entity.Spawner?.AddItemToRemoveQueue(ammo);
        }

        // ─────────────────────────────── 补足待发弹（从存量里挪，不凭空生弹） ───────────────────────────────

        /// <summary>把待发弹补到 min(N, condition)：condition 不变，只是把"存量"换成看得见的实体弹</summary>
        public static void ChamberNow(Item pouch)
        {
            if (!IsAuthority || pouch == null) return;
            int target = Math.Min(ReadyRoundsOf(pouch), ConditionOf(pouch));
            int current = PhysicalCount(pouch);
            if (current >= target) return; // 多出来的实体弹不主动移除（可能是玩家手动塞的）
            nextChamberAt[pouch.ID] = Timing.TotalTime + PouchConfig.PouchRefillInterval;
            SpawnAmmoInto(pouch, PouchItems.AmmoIdOf(pouch), target - current);
        }

        /// <summary>事件驱动的补弹请求：按帧摊平地进队列，按 pouchRefillInterval 节流后执行</summary>
        public static void RequestChamber(Item pouch)
        {
            if (!IsAuthority || pouch == null) return;
            chamberPending[pouch.ID] = pouch;
        }

        static void DrainChamberRequests(double now)
        {
            if (chamberPending.Count == 0) return;
            // 不能边遍历边让 ChamberNow 往里写，先挪进暂存表
            chamberScratch.Clear();
            foreach (var kv in chamberPending) chamberScratch.Add(kv.Value);
            chamberPending.Clear();

            int processed = 0;
            for (int i = 0; i < chamberScratch.Count; i++)
            {
                var pouch = chamberScratch[i];
                if (pouch == null || pouch.Removed) continue;
                // 节流：上次补弹不足 pouchRefillInterval 就留到下一帧再试
                if (nextChamberAt.TryGetValue(pouch.ID, out double at) && now < at)
                {
                    chamberPending[pouch.ID] = pouch;
                    continue;
                }
                if (processed++ >= MaxChamberPerFrame)
                {
                    chamberPending[pouch.ID] = pouch; // 本帧配额用完，剩下的下一帧继续
                    continue;
                }
                try { ChamberNow(pouch); }
                catch (Exception ex) { PouchLog.Warn($"补待发弹失败：{ex.Message}"); }
            }
            chamberScratch.Clear();
        }

        // ─────────────────────────────── 装填量（原武器的装填上限） ───────────────────────────────
        // 这一段是为了"不超模"：袋子只是储备，本次装填能连打几发仍然由武器原本的装填上限决定。
        // 两个量要分清：
        //   N（readyRounds）= 引擎同一时刻看得见几发待发弹（齐射数）；
        //   装填量（Loaded）= 这次装填还能打几发（≤ magCap），打满就拦开火要求重新装填。

        static readonly PropertyInfo reloadTimerProp =
            typeof(RangedWeapon).GetProperty("ReloadTimer", BindingFlags.Public | BindingFlags.Instance);

        /// <summary>
        /// ReloadTimer 在本机是 get;private set（反编译核实），编译期写不了，只能反射走 setter/后备字段。
        /// </summary>
        static void SetReloadTimer(RangedWeapon weapon, float seconds)
        {
            if (weapon == null || seconds <= 0f) return;
            try
            {
                var setter = reloadTimerProp?.GetSetMethod(nonPublic: true);
                if (setter != null) { setter.Invoke(weapon, new object[] { seconds }); return; }
                var field = typeof(RangedWeapon).GetField("<ReloadTimer>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null) { field.SetValue(weapon, seconds); return; }
                PouchLog.Warn("RangedWeapon.ReloadTimer 不可写：装填耗时未生效");
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"设置 ReloadTimer 失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 装填：装填量 = min(原装填上限, 袋内存量) + 加装填耗时 + 提示。
        /// 插袋进武器与按装填键走的是同一条路（拔插一次 = 装填一次）。
        /// 装填量是纯本地的内存状态，所以双端都记：客户端本地拦开火的判定要跟得上服务端，
        /// 否则会出现"客户端以为打空了、服务端以为还有"的错位。
        /// </summary>
        public static void ReloadWeapon(Item weapon, Item pouch, bool notify = true)
        {
            if (weapon == null || weapon.Removed) return;
            if (pouch == null || pouch.Removed) return;
            if (!PouchConfig.TryGetWeapon(weapon.Prefab.Identifier.Value, out var entry)) return;

            int cap = entry.HasCap ? entry.MagCap : int.MaxValue;
            int amount = Math.Min(cap, ConditionOf(pouch));
            weaponLoads[weapon.ID] = new WeaponLoad { PouchId = pouch.ID, Loaded = amount, Size = amount };
            SetReloadTimer(PouchItems.GetWeapon(weapon), PouchConfig.ReloadDelay);
            if (!IsAuthority) return;
            PouchLog.Log($"{weapon.Name} 装填 {amount} 发（上限 {cap}，袋内存量 {ConditionOf(pouch)}）");
            LogWeaponState("装填", weapon, pouch, notify ? "插入/按键" : "预判");
            if (notify)
                PouchLog.Notify(PouchText.KeyReloadDone, "装填完成：%s（%d 发）", Color.LightGreen,
                    weapon.Name, amount.ToString());
        }

        /// <summary>
        /// 诊断日志（mag_debug 打开时可见）：武器名 / 装填量 / 本次装填量 / 袋内存量 / 待发弹数 / 触发来源。
        /// 卡住时一眼能看出是"装填量为 0"还是"存量打空"还是"待发弹没补上"。
        /// </summary>
        public static void LogWeaponState(string what, Item weapon, Item pouch, string who)
        {
            if (!PouchLog.Verbose) return;
            int loaded = -1, size = -1;
            if (weapon != null && weaponLoads.TryGetValue(weapon.ID, out var st)) { loaded = st.Loaded; size = st.Size; }
            PouchLog.Log($"[{what}] 武器={weapon?.Name ?? "?"} 装填量={loaded}/{size} " +
                         $"存量={ConditionOf(pouch)} 待发弹={PhysicalCount(pouch)} 触发={who}");
        }

        /// <summary>袋子离开武器：清掉这把枪的装填状态（再插回来 = 重新装填）</summary>
        public static void ClearWeaponLoad(Item weapon)
        {
            if (weapon != null && weaponLoads.Remove(weapon.ID)) PouchLog.Log($"袋子离开 {weapon.Name}，装填状态已清空");
        }

        /// <summary>
        /// 取这把枪当前的装填状态。没记录（刚读档/插件重载）就按"刚装填"初始化：
        /// 内存里的装填量不落盘，读档后给一次满装填是最不意外的选择（否则玩家会看到一个"打空"的枪）。
        /// </summary>
        public static bool TryGetLoadState(Item weapon, Item pouch, out int loaded, out int size)
        {
            loaded = 0;
            size = 0;
            if (weapon?.Prefab == null || pouch == null) return false;
            if (!PouchConfig.TryGetWeapon(weapon.Prefab.Identifier.Value, out var entry) || !entry.HasCap) return false;

            if (!weaponLoads.TryGetValue(weapon.ID, out var st) || st.PouchId != pouch.ID)
            {
                // 只在"确实没有记录"或"换了另一个袋子"时自愈初始化：已有记录（哪怕 Loaded=0）绝不悄悄补满，
                // 否则拦开火就形同虚设。读档/插件重载后这条会给一次满装填（已知取舍）。
                int amount = Math.Min(entry.MagCap, ConditionOf(pouch));
                st = new WeaponLoad { PouchId = pouch.ID, Loaded = amount, Size = amount };
                weaponLoads[weapon.ID] = st;
                PouchLog.Log($"[自愈初始化] {weapon.Name} 装填量={amount}（存量 {ConditionOf(pouch)}，无记录或换了袋子）");
            }
            loaded = st.Loaded;
            size = st.Size;
            return true;
        }

        /// <summary>开火/取弹扣装填量（不会扣成负数）</summary>
        static void ConsumeLoad(Item weapon, Item pouch, int count)
        {
            if (weapon == null || count <= 0) return;
            if (!TryGetLoadState(weapon, pouch, out _, out _)) return; // 顺带把状态建起来
            if (weaponLoads.TryGetValue(weapon.ID, out var st)) st.Loaded = Math.Max(0, st.Loaded - count);
        }

        // ─────────────────────────────── 结算入口 ───────────────────────────────

        /// <summary>
        /// 开火结算：按本次真实消耗的发数扣存量 **和** 装填量（齐射一轮一起算），再补足待发弹。
        /// 装填量双端都扣（客户端本地拦开火要跟得上），存量与生成只在权威端改。
        /// </summary>
        public static void SettleFire(Item weapon, Item pouch, int consumed)
        {
            if (pouch == null || consumed <= 0) return;
            ConsumeLoad(weapon, pouch, consumed);
            if (!IsAuthority) return;
            SetCondition(pouch, ConditionOf(pouch) - consumed);
            RequestChamber(pouch);
            PouchLog.Log($"{pouch.Name} 开火消耗 {consumed} 发 → 存量 {ConditionOf(pouch)}，" +
                         $"待发弹 {PhysicalCount(pouch)}");
        }

        /// <summary>玩家插一发实体弹进袋子：吸收 + condition+1 + 补足待发弹（装填量不动）。</summary>
        public static void AbsorbInsertedRound(Item pouch, Item ammo)
        {
            if (!IsAuthority) return;
            AbsorbItem(ammo);
            SetCondition(pouch, ConditionOf(pouch) + 1);
            RequestChamber(pouch);
            PouchLog.Log($"{pouch.Name} 吸收 1 发 → 存量 {ConditionOf(pouch)}，待发弹 {PhysicalCount(pouch)}");
        }

        /// <summary>玩家从袋里拖走一枚待发弹：存量 −1 + 装填量 −1 + 补足待发弹（等价"取出 1 发"）。</summary>
        public static void SettleRoundTaken(Item pouch)
        {
            if (pouch == null) return;
            // 这发已经不在武器供给里了，装填量跟着减，免得提示的"还能打几发"和实际不符。
            // 装填量双端都减，存量只在权威端改。
            var weapon = FindWeaponHolding(pouch);
            if (weapon != null) ConsumeLoad(weapon, pouch, 1);
            if (!IsAuthority) return;
            SetCondition(pouch, ConditionOf(pouch) - 1);
            RequestChamber(pouch);
            PouchLog.Log($"{pouch.Name} 取走 1 发 → 存量 {ConditionOf(pouch)}");
        }

        /// <summary>
        /// 「装填」按钮：把持有者背包里的同种弹逐枚吸收进袋子，直到袋子到上限或背包没弹。
        /// 一发都没装进去时也给一句提示（别让按钮看起来没反应）。
        /// </summary>
        public static void LoadPouchFromButton(Item pouch, Character user)
        {
            if (pouch == null) return;
            if (!PouchItems.IsPouch(pouch)) return;
            if (!IsAuthority) return; // 客户端那一次跳过：服务端会做，结果靠常规同步下发

            int cap = PouchItems.CapacityOf(pouch);
            int condition = ConditionOf(pouch);
            if (condition >= cap)
            {
                PouchLog.NotifyThrottled(PouchText.KeyPouchFull + pouch.ID, 1.0,
                    "弹药袋已满（%d）", Color.Red, cap.ToString());
                return;
            }

            string ammoId = PouchItems.AmmoIdOf(pouch);
            if (string.IsNullOrEmpty(ammoId))
            {
                PouchLog.Warn($"装填失败：认不出 {pouch.Name} 的弹种");
                return;
            }

            // 持有者：按钮点的人 → 袋子的根持有者（袋子插在枪上、枪在手上时也能解析到角色）→ 本地受控角色
            var who = user ?? (pouch.GetRootInventoryOwner() as Character) ?? Character.Controlled;
            if (who?.Inventory == null)
            {
                PouchLog.NotifyThrottled(PouchText.KeyNoHolder + pouch.ID, 1.0,
                    "装填失败：找不到弹药袋的持有者", Color.Red);
                return;
            }

            int absorbedCount = 0;
            // 只取背包槽里的松散弹药（袋内已装的弹不在角色背包里，不会被重复吸收）
            foreach (var it in who.Inventory.AllItems)
            {
                if (condition >= cap) break;
                if (it == null || it.Removed || it.Prefab == null) continue;
                if (!string.Equals(it.Prefab.Identifier.Value, ammoId, StringComparison.OrdinalIgnoreCase)) continue;
                AbsorbItem(it);          // 只排队移除，不改背包集合，所以这里边遍历边吸收是安全的
                SetCondition(pouch, ++condition);
                absorbedCount++;
            }

            if (absorbedCount > 0)
            {
                RequestChamber(pouch);
                PouchLog.Log($"装填 {pouch.Name}：吸收 {absorbedCount} 发 → 存量 {condition}/{cap}");
                PouchLog.Notify(PouchText.KeyLoaded, "装填：吸收 %d 发（存量 %d/%d）", Color.LightGreen,
                    absorbedCount.ToString(), condition.ToString(), cap.ToString());
            }
            else
            {
                PouchLog.NotifyThrottled(PouchText.KeyNothingToLoad + pouch.ID, 1.0,
                    "装填：没有可补的弹（%s 背包里没有 %s）", Color.Red, who.Name, ammoId);
            }
        }

        // ─────────────────────────────── 每帧 / 低频扫描 ───────────────────────────────

        /// <summary>每帧驱动（权威端）：节流补待发弹 + 低频全量扫描兜底。</summary>
        public static void Tick(double now)
        {
            if (!IsAuthority) return;
            DrainChamberRequests(now);
            if (now >= nextScanAt)
            {
                nextScanAt = now + 2.0;
                ScanMaintenance();
            }
        }

        /// <summary>
        /// 2 秒一次的全量扫描（权威端）：把"有存量但待发弹不足 min(N, condition)"的袋子补齐。
        /// 事件队列（chamberPending）只在内存里，读档时若存档卡在"打掉一发、补弹物品还在生成队列里"
        /// 的瞬间，袋子会变成 condition&gt;0 却没有待发弹：武器判定"有弹药"却找不到投射物，打不响。
        /// 这一遍专门修这种局面。
        /// </summary>
        static void ScanMaintenance()
        {
            int fixedPouches = 0, fixedRounds = 0;
            try
            {
                foreach (var item in Item.ItemList)
                {
                    if (item == null || item.Removed) continue;
                    if (!PouchItems.IsPouch(item)) continue;
                    TryFixPouch(item, ref fixedPouches, ref fixedRounds);
                }
            }
            catch (Exception ex) { PouchLog.Warn($"维护扫描失败：{ex.Message}"); return; }
            ReportSweep(fixedPouches, fixedRounds);
        }

        /// <summary>roundStart 里直接跑一遍：读档后玩家可能立刻开火，等不到下一个 2 秒节拍</summary>
        public static int SweepDeficientPouches()
        {
            if (!IsAuthority || Entity.Spawner == null) return 0;
            int fixedPouches = 0, fixedRounds = 0;
            try
            {
                foreach (var item in Item.ItemList)
                {
                    if (item == null || item.Removed || !PouchItems.IsPouch(item)) continue;
                    TryFixPouch(item, ref fixedPouches, ref fixedRounds);
                }
            }
            catch (Exception ex) { PouchLog.Warn($"兜底扫描失败：{ex.Message}"); return 0; }
            ReportSweep(fixedPouches, fixedRounds);
            return fixedRounds;
        }

        /// <summary>单个袋子的兜底判定：只有 condition&gt;0 且待发弹不足 min(N, condition) 才补</summary>
        static void TryFixPouch(Item pouch, ref int fixedPouches, ref int fixedRounds)
        {
            // condition=0 是正常状态（新出的空袋/打空的袋），本来就不该有待发弹，直接跳过
            int condition = ConditionOf(pouch);
            if (condition <= 0) return;
            if (IsLoadingLevel(pouch)) return; // 关卡还在加载，别往半成品容器里塞东西
            int target = Math.Min(ReadyRoundsOf(pouch), condition);
            int current = PhysicalCount(pouch);
            if (current >= target) return;
            ChamberNow(pouch);
            fixedPouches++;
            fixedRounds += target - current;
            PouchLog.Log($"兜底补弹 {pouch.Name}：待发弹 {current} → 补至 {target}（存量 {condition}）");
        }

        static void ReportSweep(int fixedPouches, int fixedRounds)
        {
            if (fixedRounds <= 0) return;
            // 兜底命中说明场上真出现过"有存量却打不响"的袋子（多半是读档卡在生成队列那一瞬），不静默
            PouchLog.Warn($"兜底补弹：{fixedPouches} 个袋子共补 {fixedRounds} 发待发弹");
        }

        // ─────────────────────────────── 校验 / 拒绝 ───────────────────────────────

        /// <summary>袋子插进武器：用袋子自己 prefab 对应的弹药查该武器的允许弹药表。unlisted = 武器不在白名单</summary>
        public static bool TryValidatePouchInsert(Item weapon, Item pouch, out string reason, out bool unlisted)
        {
            reason = null;
            unlisted = false;
            if (weapon?.Prefab == null) return true;
            if (!PouchConfig.TryGetWeapon(weapon.Prefab.Identifier.Value, out var entry))
            {
                if (PouchConfig.AllowUnlistedWeapons) return true;
                unlisted = true;
                reason = $"这把枪用不了弹药袋（{weapon.Name} 不在配置白名单里）";
                return false;
            }
            string ammoId = PouchItems.AmmoIdOf(pouch);
            if (string.IsNullOrEmpty(ammoId)) return true; // 认不出袋子弹种就放行（可能是新增变体）
            if (PouchItems.WeaponAllowsAmmoId(entry, ammoId)) return true;
            reason = $"这把枪用不了这种弹（{weapon.Name} 不吃 {ammoId}）";
            return false;
        }

        /// <summary>往"已插在武器里的袋子"塞弹：弹种必须属于那把枪。unlisted = 武器不在白名单</summary>
        public static bool TryValidateAmmoInsert(Item pouch, Item ammo, out string reason, out bool unlisted)
        {
            reason = null;
            unlisted = false;
            var weapon = FindWeaponHolding(pouch);
            if (weapon?.Prefab == null) return true;
            if (!PouchConfig.TryGetWeapon(weapon.Prefab.Identifier.Value, out var entry))
            {
                if (PouchConfig.AllowUnlistedWeapons) return true;
                unlisted = true;
                reason = $"这把枪用不了这种弹（{weapon.Name} 不在配置白名单里）";
                return false;
            }
            if (entry.Allows(ammo)) return true;
            reason = $"这把枪用不了这种弹（{ammo.Name}）";
            return false;
        }

        /// <summary>弹种不匹配 / 未列出武器的玩家提示（两处拒绝共用，键与文案保持一处）</summary>
        public static void NotifyWrongAmmo(bool unlisted, Item weapon, string ammoText)
        {
            if (unlisted)
            {
                PouchLog.NotifyThrottled(PouchText.KeyNotAllowed + (weapon?.ID ?? 0), 2.0,
                    "这把枪用不了弹药袋（%s 不在配置白名单里）", Color.Red, weapon?.Name ?? "?");
                return;
            }
            PouchLog.NotifyThrottled(PouchText.KeyWrongAmmo + (weapon?.ID ?? 0), 2.0,
                "%s 用不了这种弹（%s）", Color.Red, weapon?.Name ?? "?", ammoText ?? "?");
        }

        /// <summary>
        /// 撤销一次插入：放回"上一个容器"（非武器容器）或搬运者背包，都不行才丢地上。
        /// 不接受把武器当"上一个容器"：被拒的这次插入本身就会把武器记成来源，直接用会绕过校验塞回枪里。
        /// promptKey 兼作提示节流键；promptMessage 已由调用方取好词（可空 = 只写日志不弹提示）。
        /// </summary>
        public static void RejectInsertion(ItemContainer container, Item item, string reason,
            string promptKey = null, string promptMessage = null)
        {
            if (item == null || Suppress) return;
            Suppress = true;
            try
            {
                var from = TakeOrigin(item);
                bool returned = false;
                if (from != null && !from.Removed && from.OwnInventory != null &&
                    !ReferenceEquals(from, container?.Item) && !PouchItems.IsWeapon(from))
                {
                    returned = from.OwnInventory.TryPutItem(item, null, null);
                }
                if (!returned)
                {
                    var who = TakeDropper(item);
                    if (who != null && !who.Removed && who.Inventory != null)
                        returned = who.Inventory.TryPutItem(item, who, CharacterInventory.AnySlot);
                }
                if (!returned && Character.Controlled != null && !Character.Controlled.Removed &&
                    Character.Controlled.Inventory != null)
                {
                    returned = Character.Controlled.Inventory.TryPutItem(item, Character.Controlled, CharacterInventory.AnySlot);
                }
                // 放不回任何容器才丢出去（位置=容器位置，不会飞走）
                if (!returned) item.Drop(null, createNetworkEvent: true, setTransform: true);
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"撤销插入失败：{ex.Message}");
            }
            finally { Suppress = false; }

            PouchLog.Warn($"插入被拒：{reason}");
            if (!string.IsNullOrEmpty(promptMessage))
                PouchLog.NotifyThrottled(promptKey ?? "pouch_reject", 2.0, promptMessage, Color.Red);
        }

        /// <summary>关卡/存档正在加载时不插手：此时物品是被"放"进容器的，不是玩家操作。</summary>
        public static bool IsLoadingLevel(Item item)
        {
            try { return item?.Submarine != null && item.Submarine.Loading; }
            catch { return false; }
        }

        /// <summary>开火窗口标记：这段窗口里的实体弹消失由 PouchFirePatch 统一结算</summary>
        public static double InWeaponUseUntil
        {
            get => inWeaponUseUntil;
            set => inWeaponUseUntil = value;
        }

        public static bool InWeaponUseWindow => Timing.TotalTime <= inWeaponUseUntil;

        /// <summary>OnItemRemoved 用：这枚实体弹是不是我们刚吸收掉的</summary>
        public static bool ConsumeAbsorbed(ushort itemId) => absorbed.Remove(itemId);

        // ─────────────────────────────── 统计 ───────────────────────────────

        public static void DumpStats()
        {
            int pouchCount = 0, total = 0, chambered = 0, empty = 0;
            try
            {
                foreach (var item in Item.ItemList)
                {
                    if (item == null || item.Removed || !PouchItems.IsPouch(item)) continue;
                    pouchCount++;
                    int cond = ConditionOf(item);
                    total += cond;
                    chambered += PhysicalCount(item);
                    if (cond <= 0) empty++;
                }
            }
            catch (Exception ex) { PouchLog.Warn($"统计失败：{ex.Message}"); return; }

            LuaCsLogger.LogMessage($"[弹药袋] 场上：袋子 {pouchCount}（总存量 {total}，其中待发弹 {chambered}，空袋 {empty}）；" +
                                   $"待补弹请求 {chamberPending.Count}，待生成实体弹 {pendingSpawn.Count}",
                Color.LightGreen);
        }
    }

    /// <summary>
    /// ItemContainer.OnItemContained / OnItemRemoved 的 postfix。
    /// 双端都会走这两条：客户端做本地校验/拒绝与提示（保持观感一致），
    /// 改 condition、吸收、生成实体弹这些只在权威端做。
    /// </summary>
    public static class PouchContainPatch
    {
        public static MethodBase TargetMethodContained() => AccessTools.Method(typeof(ItemContainer), "OnItemContained");

        public static MethodBase TargetMethodRemoved() => AccessTools.Method(typeof(ItemContainer), "OnItemRemoved");

        public static void OnContainedPostfix(ItemContainer __instance, Item __0)
        {
            if (PouchRuntime.Suppress || __instance == null || __0 == null) return;
            try
            {
                Item item = __0;
                Item owner = __instance.Item;
                if (owner == null) return;
                if (PouchRuntime.IsLoadingLevel(owner)) return; // 关卡加载中：不是玩家操作，别插手

                // ① 袋子 → 武器容器：弹种校验（不通过就撤销这次插入），通过就当一次装填
                if (PouchItems.IsWeapon(owner) && PouchItems.IsPouch(item))
                {
                    if (!PouchRuntime.TryValidatePouchInsert(owner, item, out string reason, out bool unlisted))
                    {
                        PouchRuntime.NotifyWrongAmmo(unlisted, owner,
                            PouchItems.AmmoIdOf(item));
                        PouchRuntime.RejectInsertion(__instance, item, reason,
                            PouchText.KeyWrongAmmo + owner.ID,
                            PouchText.Format(PouchText.KeyWrongAmmo, "%s 用不了这种弹（%s）",
                                owner.Name, PouchItems.AmmoIdOf(item) ?? "?"));
                        return;
                    }
                    // 插袋 = 装填：装填量 = min(原装填上限, 袋内存量) + 装填耗时（拔插一次等价于按装填键）
                    PouchRuntime.ReloadWeapon(owner, item);
                    // 立刻补齐待发实体弹（无视 pouchRefillInterval 节流）：否则刚插上可能还没膛弹就开火
                    PouchRuntime.ChamberNow(item);
                    PouchRuntime.LogWeaponState("插袋=装填", owner, item, "插入");
                    return;
                }

                if (PouchItems.IsPouch(owner))
                {
                    // ② 「装填」按钮的标记物进袋子：按持有者背包装填，然后把标记物收掉
                    //    （当前 XML 的单格容器只收自己那种弹，引擎其实不会把标记物放进来；
                    //     真正的入口是 PouchFillPatch 拦的按钮效果，这条是兜底）
                    if (PouchItems.IsFillMarker(item))
                    {
                        PouchRuntime.LoadPouchFromButton(owner, owner.GetRootInventoryOwner() as Character);
                        PouchRuntime.AbsorbItem(item);
                        return;
                    }

                    // ③ 弹药 → 袋子：先校验弹种 vs 武器，再吸收成存量 + 补足待发弹
                    if (item.IsInRemoveQueue) return;
                    if (!PouchConfig.IsKnownAmmo(item)) return; // 配置外的物品不吸收，原样留着
                    if (!PouchRuntime.TryValidateAmmoInsert(owner, item, out string reason, out bool unlisted))
                    {
                        var weapon = PouchRuntime.FindWeaponHolding(owner);
                        PouchRuntime.NotifyWrongAmmo(unlisted, weapon, item.Name);
                        PouchRuntime.RejectInsertion(__instance, item, reason,
                            PouchText.KeyWrongAmmo + (weapon?.ID ?? 0),
                            PouchText.Format(PouchText.KeyWrongAmmo, "%s 用不了这种弹（%s）",
                                weapon?.Name ?? "?", item.Name));
                        return;
                    }
                    if (PouchRuntime.InWeaponUseWindow) return; // 开火窗口里的落弹：等开火结算
                    if (!PouchRuntime.IsAuthority) return;
                    if (PouchRuntime.ConditionOf(owner) >= PouchItems.CapacityOf(owner))
                    {
                        // 满袋不再吸收：直接吸收等于白白销毁一发，退回原处并提示
                        int cap = PouchItems.CapacityOf(owner);
                        PouchRuntime.RejectInsertion(__instance, item, $"袋子已满（{cap}）",
                            PouchText.KeyPouchFull + owner.ID,
                            PouchText.Format(PouchText.KeyPouchFull, "弹药袋已满（%d）", cap.ToString()));
                        return;
                    }
                    PouchRuntime.AbsorbInsertedRound(owner, item);
                }
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"OnItemContained 处理失败：{ex.Message}");
            }
        }

        public static void OnRemovedPostfix(ItemContainer __instance, Item __0)
        {
            if (PouchRuntime.Suppress || __instance == null || __0 == null) return;
            try
            {
                Item item = __0;
                Item owner = __instance.Item;
                if (owner == null) return;
                // 我们自己吸收掉的物品：先消费标记（容器被销毁时也需要清理这条残留），再当没事发生
                if (PouchRuntime.ConsumeAbsorbed(item.ID)) return;
                // 容器本身正在被销毁（拆解/移除）：里面的弹跟着消失，不做结算
                if (owner.Removed) return;

                // ① 袋子离开容器：记来源（插入被拒时放回去用）；从武器上拔下来就清掉这把枪的装填状态
                if (PouchItems.IsPouch(item))
                {
                    PouchRuntime.RecordOrigin(item, owner);
                    if (PouchItems.IsWeapon(owner))
                    {
                        PouchRuntime.LogWeaponState("拔袋", owner, item, "拔出");
                        PouchRuntime.ClearWeaponLoad(owner);
                    }
                    return;
                }

                // ② 待发弹被拿走：开火那部分由 PouchFirePatch 结算，其余（拖走/掉落）在这里扣
                if (PouchItems.IsPouch(owner))
                {
                    if (PouchItems.IsFillMarker(item)) return;     // 标记物不是弹药，不扣存量
                    if (PouchRuntime.InWeaponUseWindow) return;    // 开火窗口：交给开火补丁
                    if (PouchRuntime.IsLoadingLevel(owner)) return;
                    PouchRuntime.SettleRoundTaken(owner);
                }
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"OnItemRemoved 处理失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// RangedWeapon.Use 的 prefix/postfix。
    ///   prefix：装填量已经打满（=0）而袋里还有存量 → 拦下这次开火（跳过原方法、不出弹、不消耗）+ 提示；
    ///   postfix：按"调用前后袋内待发弹数之差"结清本次消耗（齐射 N 发一次算清），扣存量与装填量。
    /// </summary>
    public static class PouchFirePatch
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(RangedWeapon), "Use", new[] { typeof(float), typeof(Character) });

        public static bool Prefix(RangedWeapon __instance, ref bool __result, out int __state)
        {
            __state = -1;
            try
            {
                var weapon = __instance?.Item;
                var pouch = PouchRuntime.FindPouchInWeapon(weapon);
                if (pouch == null) return true;

                int condition = PouchRuntime.ConditionOf(pouch);
                if (condition <= 0)
                {
                    // 存量打空：不拦，交给引擎走原版那条"需要弹药"（FindProjectile 找不到弹，本来就不会空放），
                    // 我们只补一条自己的提示，好和"装填量打满"区分开
                    PouchLog.NotifyThrottled(PouchText.KeyPouchEmpty + weapon.ID, 3.0,
                        "弹药袋空了：%s 需要补充弹丸", Color.Red, weapon.Name);
                    PouchRuntime.LogWeaponState("拦开火?否(存量0)", weapon, pouch, "开火");
                    PouchRuntime.InWeaponUseUntil = Timing.TotalTime + 0.25;
                    __state = PouchRuntime.PhysicalCount(pouch);
                    return true;
                }

                if (PouchRuntime.TryGetLoadState(weapon, pouch, out int loaded, out int size) && loaded <= 0)
                {
                    __result = false;
                    PouchLog.NotifyThrottled(PouchText.KeyNeedReload + weapon.ID, 3.0,
                        "需要装填：%s 已打满 %d 发 —— 把弹药袋拿在手里点「装填」（或按 %s 键）", Color.Red,
                        weapon.Name, size.ToString(), PouchConfig.LoadKey);
                    PouchRuntime.LogWeaponState("拦开火(装填量0)", weapon, pouch, "开火");
                    return false; // 跳过原方法：不消耗、不出弹
                }

                // 标记开火窗口：这期间待发弹消失都算开火的账（OnItemRemoved 不重复扣）
                PouchRuntime.InWeaponUseUntil = Timing.TotalTime + 0.25;
                __state = PouchRuntime.PhysicalCount(pouch);
                return true;
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"开火前判定失败：{ex.Message}");
                return true; // 出错就放行原逻辑，绝不因为本插件把枪卡死
            }
        }

        public static void Postfix(RangedWeapon __instance, int __state)
        {
            try
            {
                PouchRuntime.InWeaponUseUntil = 0;
                if (__state < 0) return;
                var weapon = __instance?.Item;
                var pouch = PouchRuntime.FindPouchInWeapon(weapon);
                if (pouch == null) return;
                int after = PouchRuntime.PhysicalCount(pouch);
                int consumed = __state - after;
                if (consumed <= 0) return;
                PouchRuntime.SettleFire(weapon, pouch, consumed);
                PouchRuntime.LogWeaponState($"开火结算({consumed}发)", weapon, pouch, "开火");
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"开火结算失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// Item.Drop 的 prefix：记下"这次搬运是谁在操作"，插入被拒时用来把物品放回他的背包
    /// （从角色背包拖到武器/袋子时，物品没有 ItemContainer 来源可记）。
    /// </summary>
    public static class PouchPickupPatch
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Item), "Drop", new[] { typeof(Character), typeof(bool), typeof(bool) });

        public static void Prefix(Item __instance, Character __0)
        {
            if (__0 == null) return;
            try { PouchRuntime.RecordDropper(__instance, __0); }
            catch { }
        }
    }

    /// <summary>
    /// 「装填」按钮（XML 里的 uilabel.AmmoPouch_Load）。按钮的 XML 是"往袋子里生成一枚
    /// Touhou_Ammo_Fill_Marker"，但这条路走不通：
    /// 袋子的容器只有一格且只收自己那种弹（spawnposition="ThisInventory" 要求 CanBeContained 通过，
    /// 否则引擎什么都不生成；就算放行，同格已有子弹时 CanProbablyBePut 也会否掉）。
    /// 所以这里直接拦按钮效果本身：认"会给这个袋子生成标记物的 OnUse 效果" → 做装填 + 跳过原效果。
    /// </summary>
    public static class PouchFillPatch
    {
        public static MethodBase TargetMethod() => AccessTools.Method(typeof(Item), "ApplyStatusEffect");

        public static bool Prefix(Item __instance, StatusEffect __0, ActionType __1, Character __3)
        {
            try
            {
                if (__1 != ActionType.OnUse) return true;
                if (!PouchItems.IsPouch(__instance)) return true;
                if (!EffectSpawnsFillMarker(__0)) return true;

                // 双端都会走到这里：服务端那次真正搬运，客户端那次直接跳过（避免本地凭空改状态）
                PouchRuntime.LoadPouchFromButton(__instance, __3);
                return false; // 跳过原效果：不再尝试生成标记物
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"装填按钮处理失败：{ex.Message}");
                return true;
            }
        }

        // StatusEffect 的生成列表是私有字段、元素类型也是私有嵌套类型，只能反射读；
        // 每个效果类型只解析一次 FieldInfo，热路径上就是几次引用比较。
        static readonly FieldInfo spawnItemsField =
            typeof(StatusEffect).GetField("spawnItems", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly Dictionary<Type, FieldInfo> prefabFieldCache = new Dictionary<Type, FieldInfo>();
        static bool markerCheckUnavailable;

        /// <summary>这个效果会不会生成「装填」标记物</summary>
        static bool EffectSpawnsFillMarker(StatusEffect effect)
        {
            if (effect == null) return false;
            if (spawnItemsField == null)
            {
                if (!markerCheckUnavailable)
                {
                    markerCheckUnavailable = true;
                    PouchLog.Warn("StatusEffect.spawnItems 字段未找到：装填按钮改按「弹种袋上的 OnUse 效果」粗判");
                }
                return true; // 反射失效时的兜底：弹种袋上只有这一个 OnUse 效果
            }

            if (!(spawnItemsField.GetValue(effect) is System.Collections.IEnumerable list)) return false;
            foreach (var info in list)
            {
                if (info == null) continue;
                var type = info.GetType();
                if (!prefabFieldCache.TryGetValue(type, out var field))
                {
                    field = type.GetField("ItemPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    prefabFieldCache[type] = field;
                }
                if (field?.GetValue(info) is ItemPrefab prefab &&
                    string.Equals(prefab.Identifier.Value, PouchConfig.FillMarkerId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 每帧驱动。客户端/单人挂 GameMain.Update，专用服务器挂 GameServer.Update
    /// （GameServer 在 DedicatedServer.dll，编译期不一定引用得到，用反射解析）；
    /// 编译期分流保证不会两个挂点同时触发。
    /// </summary>
#if SERVER
    public static class PouchTickPatch
    {
        public static MethodBase TargetMethod()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Barotrauma.Networking.GameServer");
                var m = t?.GetMethod("Update", flags, null, new[] { typeof(float) }, null);
                if (m != null) return m;
            }
            PouchLog.Warn("PouchTickPatch：找不到 GameServer.Update，补待发弹/兜底扫描不会生效");
            return null;
        }

        public static void Postfix()
        {
            PouchRuntime.Tick(Timing.TotalTime);
        }
    }
#else
    public static class PouchTickPatch
    {
        public static MethodBase TargetMethod() => AccessTools.Method(typeof(GameMain), "Update");

        public static void Postfix()
        {
            if (GameMain.Instance != null && GameMain.Instance.Paused) return;
            PouchRuntime.Tick(Timing.TotalTime);
#if CLIENT
            PouchHotkey.Tick();
#endif
        }
    }
#endif

#if CLIENT
    /// <summary>
    /// 客户端装填键（loadKey，默认 R）：当前拿着的武器插着袋子时按一下 = 装填。
    /// 单人直接本地执行；联机发 C→S 消息（touhouMagReload），由服务端重置装填量 + 加装填耗时。
    /// </summary>
    public static class PouchHotkey
    {
        static bool wasDown;
        static bool keyParsed;
        static Keys loadKeys;

        public static void Tick()
        {
            try
            {
                if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsClient) return; // 服务器上下文不读键盘
                if (GUI.KeyboardDispatcher.Subscriber != null) return; // 聊天框/输入框激活时不触发
                var ch = Character.Controlled;
                if (ch == null || ch.Removed) { wasDown = false; return; }

                if (!keyParsed)
                {
                    keyParsed = true;
                    if (!Enum.TryParse(PouchConfig.LoadKey, true, out loadKeys))
                    {
                        loadKeys = Keys.R;
                        PouchLog.Warn($"配置的 loadKey「{PouchConfig.LoadKey}」不是合法的按键名，退回 R");
                    }
                }

                bool down = PlayerInput.GetKeyboardState.IsKeyDown(loadKeys);
                if (down && !wasDown)
                {
                    var weapon = FindCarriedWeaponWithPouch(ch);
                    if (weapon != null) PouchReloadNet.RequestReload(weapon);
                }
                wasDown = down;
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"装填键检测失败：{ex.Message}");
            }
        }

        /// <summary>手持优先，其次背包/装备槽里任何一把插了袋子的武器</summary>
        static Item FindCarriedWeaponWithPouch(Character ch)
        {
            foreach (var held in ch.HeldItems)
            {
                if (held != null && PouchItems.IsWeapon(held) && PouchRuntime.FindPouchInWeapon(held) != null) return held;
            }
            if (ch.Inventory == null) return null;
            foreach (var it in ch.Inventory.AllItems)
            {
                if (it != null && !it.Removed && PouchItems.IsWeapon(it) && PouchRuntime.FindPouchInWeapon(it) != null) return it;
            }
            return null;
        }
    }
#endif

    /// <summary>
    /// 网络：装填请求（C→S，touhouMagReload）。双端 Networking 签名不同，照 BondNet 那套走
    /// 兼容接口 + 反射兜底，全部 try-catch，失败只警告。
    /// </summary>
    public static class PouchReloadNet
    {
        public const string NET_RELOAD = "touhouMagReload";

        public static void Register()
        {
            ReceiveCompat(NET_RELOAD, HandleReloadRequest);
        }

        public static void RequestReload(Item weapon)
        {
            if (weapon == null) return;
            var pouch = PouchRuntime.FindPouchInWeapon(weapon);
            if (pouch == null) return;
            // 本地先按装填完更新（客户端预测：本地拦开火的判定要跟得上），服务端会做同样的重置
            PouchRuntime.ReloadWeapon(weapon, pouch, notify: GameMain.NetworkMember == null);
            // 单人没网：本地就是权威端，上面那次已经做完了
            if (GameMain.NetworkMember == null) return;
            ClientSend(NET_RELOAD, w => w.WriteUInt16(weapon.ID));
        }

        static void HandleReloadRequest(object[] args)
        {
            try
            {
                var (msg, sender) = ParseArgs(args);
                if (msg == null) return;
                if (!PouchRuntime.IsAuthority) return;
                ushort id = msg.ReadUInt16();
                var item = Entity.FindEntityByID(id) as Item;
                if (item == null || item.Removed)
                {
                    PouchLog.Log($"收到装填请求：ID={id} 的物品不存在，忽略");
                    return;
                }
                var pouch = PouchRuntime.FindPouchInWeapon(item);
                if (pouch == null)
                {
                    PouchLog.Log($"收到装填请求：{item.Name}(ID={id}) 没插袋子，忽略");
                    return;
                }
                PouchLog.Log($"服务端处理装填请求：{item.Name}(ID={id})，来自 {sender?.Name ?? "本地"}");
                PouchRuntime.ReloadWeapon(item, pouch);
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"处理装填请求失败：{ex.Message}");
            }
        }

        static (IReadMessage msg, Client sender) ParseArgs(object[] args)
        {
            IReadMessage msg = null;
            Client sender = null;
            if (args == null) return (msg, sender);
            foreach (var a in args)
            {
                if (a is IReadMessage m) msg = m;
                else if (a is Client c) sender = c;
            }
            return (msg, sender);
        }

        // 优先走兼容接口的 LuaCsAction（跨版本稳）；老版本没有就表达式树照 Receive 委托的真实签名动态构造
        static void ReceiveCompat(string name, Action<object[]> handler)
        {
            try
            {
                var net = LuaCsSetup.Instance.Networking;
                if (net is ILuaCsNetworking compat)
                {
                    compat.Receive(name, (LuaCsAction)(args => handler(args)));
                    PouchLog.Log($"网络接收器 [{name}] 已注册（ILuaCsNetworking）");
                    return;
                }
                var recv = FindReceiveMethod(net);
                if (recv == null) { PouchLog.Warn($"Networking.Receive 未找到（{name}）"); return; }
                var delType = recv.GetParameters()[1].ParameterType;
                var invoke = delType.GetMethod("Invoke");
                if (invoke == null) { PouchLog.Warn($"Networking.Receive 委托签名未解析（{name}）"); return; }
                var ps = invoke.GetParameters();
                var exprParams = new System.Linq.Expressions.ParameterExpression[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                    exprParams[i] = System.Linq.Expressions.Expression.Parameter(ps[i].ParameterType, ps[i].Name ?? $"p{i}");
                var arr = System.Linq.Expressions.Expression.NewArrayInit(typeof(object),
                    Array.ConvertAll(exprParams, p => (System.Linq.Expressions.Expression)System.Linq.Expressions.Expression.Convert(p, typeof(object))));
                var call = System.Linq.Expressions.Expression.Call(
                    typeof(PouchReloadNet).GetMethod(nameof(RunHandler), BindingFlags.NonPublic | BindingFlags.Static),
                    System.Linq.Expressions.Expression.Constant(handler), arr);
                var del = System.Linq.Expressions.Expression.Lambda(delType, call, exprParams).Compile();
                recv.Invoke(net, new object[] { name, del });
                PouchLog.Log($"网络接收器 [{name}] 已注册（反射 {delType.Name}）");
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"注册接收器 {name} 失败：{ex.Message}");
            }
        }

        static void RunHandler(Action<object[]> handler, object[] args) => handler(args);

        static MethodInfo FindReceiveMethod(object net)
        {
            if (net == null) return null;
            var methods = net.GetType().GetMethods();
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != "Receive") continue;
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;
                if (ps[0].ParameterType != typeof(string)) continue;
                if (!ps[1].ParameterType.IsSubclassOf(typeof(Delegate))) continue;
                return m;
            }
            return null;
        }

        // C→S：客户端 Send(IWriteMessage, DeliveryMethod) 在服务端不存在，编译期只能反射调用（照 BondNet）
        static MethodInfo clientSendMethod;
        static object clientSendReliable;

        static void ClientSend(string name, Action<IWriteMessage> write)
        {
            try
            {
                var net = LuaCsSetup.Instance.Networking;
                var msg = net.Start(name);
                write(msg);
                if (clientSendMethod == null)
                {
                    var methods = net.GetType().GetMethods();
                    for (int i = 0; i < methods.Length; i++)
                    {
                        var m = methods[i];
                        if (m.Name != "Send") continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 2 || !ps[1].ParameterType.IsEnum ||
                            ps[1].ParameterType.Name != "DeliveryMethod") continue;
                        clientSendMethod = m;
                        clientSendReliable = Enum.Parse(ps[1].ParameterType, "Reliable");
                        break;
                    }
                }
                if (clientSendMethod == null) { PouchLog.Warn("客户端 Networking.Send 未找到，装填请求未发出"); return; }
                clientSendMethod.Invoke(net, new[] { msg, clientSendReliable });
                PouchLog.Log($"已发出装填请求 [{name}]");
            }
            catch (Exception ex)
            {
                PouchLog.Warn($"发送 {name} 失败：{ex.Message}");
            }
        }
    }
}
