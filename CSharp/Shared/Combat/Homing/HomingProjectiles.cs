using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.Homing
{
    /// <summary>
    /// 通用射弹追踪插件入口。只 Patch 本命名空间的类，避免与主插件（Touhou.Affixes）重复打补丁。
    /// 方案见 Docs/通用射弹追踪-可行方案.md；配置在 Config/homing_config.xml，homing_reload 热重载。
    /// 外部模组兼容：prefab tag（touhou_homing / touhou_homing_mouse）/ 各自包内的同名配置文件 /
    /// 运行时 Hook（touhouHomingRegister / touhouHomingUnregister），详见文档"其他模组兼容"一节。
    /// </summary>
    public sealed class HomingPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public static readonly string[] CommandNames = { "homing_reload", "homing_list" };

        static string packageDir;
        /// <summary>模组目录：优先用主插件已解析的路径，插件初始化顺序不保证它先跑。</summary>
        public static string PackageDir
        {
            get
            {
                if (packageDir != null) return packageDir;
                packageDir = Touhou.Affixes.Mod.Package?.Dir;
                if (string.IsNullOrEmpty(packageDir) &&
                    LuaCsSetup.Instance.PluginPackageManager.TryGetPackageForPlugin<HomingPlugin>(out var pkg))
                    packageDir = pkg.Dir;
                return packageDir ?? ".";
            }
        }

        public void Initialize()
        {
            harmony = new Harmony("touhou.homing");
        }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone)
            {
                HomingLog.Log("OnLoadCompleted: 内容重载，补丁/命令已注册，跳过");
                return;
            }
            oneTimeInitDone = true;

            // 先清掉本插件名下的历史补丁再重打，防止 reloadlua 残留堆叠
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(HomingShootPatch));
            harmony.PatchAll(typeof(HomingTurretPatch));
            harmony.PatchAll(typeof(HomingTickPatch));
            harmony.PatchAll(typeof(MonarchShootPatch));

            HomingConfig.Load();
            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.Homing.RoundStart", OnRoundStart);
            LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.Homing.RoundEnd", OnRoundEnd);
            // 外部模组运行时注册接口
            LuaCsSetup.Instance.Hook.Add("touhouHomingRegister", "Touhou.Homing.Register", OnExternalRegister);
            LuaCsSetup.Instance.Hook.Add("touhouHomingUnregister", "Touhou.Homing.Unregister", OnExternalUnregister);
            RegisterCommands();

            HomingLog.Log("射弹追踪插件已加载");
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.Homing.RoundStart");
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.Homing.RoundEnd");
            LuaCsSetup.Instance.Hook.Remove("touhouHomingRegister", "Touhou.Homing.Register");
            LuaCsSetup.Instance.Hook.Remove("touhouHomingUnregister", "Touhou.Homing.Unregister");
            foreach (var cmd in CommandNames)
                LuaCsSetup.Instance.Game.RemoveCommand(cmd);
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
            HomingTracker.Clear();
            MonarchTracker.Clear();
            HomingLog.Log("射弹追踪插件已卸载");
        }

        object OnRoundStart(object[] args)
        {
            HomingTracker.Clear();
            MonarchTracker.Clear();
            HomingConfig.Load();
            return null;
        }

        object OnRoundEnd(object[] args)
        {
            HomingTracker.Clear();
            MonarchTracker.Clear();
            return null;
        }

        // 外部模组：Hook.Call("touhouHomingRegister", "identifier" [, paramsTable])
        // paramsTable 键名与 XML 属性一致（mode/coneDeg/range/steering/...），缺省用当前默认参数。
        // 注意时序：其他模组的 Autorun 可能先于本插件加载，请在 roundStart 等钩子里调用。
        static object OnExternalRegister(object[] args)
        {
            if (args == null || args.Length < 1 || !(args[0] is string id) || id.Length == 0)
            {
                HomingLog.Warn("touhouHomingRegister: 第一个参数须为射弹 identifier 字符串");
                return null;
            }
            HomingParams p = HomingConfig.NewRuntimeParams(args.Length > 1 ? args[1] : null);
            HomingConfig.RegisterRuntime(id, p);
            HomingLog.Log($"外部注册追踪射弹：{id}");
            return null;
        }

        static object OnExternalUnregister(object[] args)
        {
            if (args == null || args.Length < 1 || !(args[0] is string id)) return null;
            if (HomingConfig.UnregisterRuntime(id)) HomingLog.Log($"外部注销追踪射弹：{id}");
            return null;
        }

        static void RegisterCommands()
        {
            var game = LuaCsSetup.Instance.Game;
            game.AddCommand("homing_reload", "重载射弹追踪配置 Config/homing_config.xml", args =>
            {
                HomingConfig.Load();
            }, null, false);
            game.AddCommand("homing_list", "列出射弹追踪配置与当前活跃追踪数", args =>
            {
                // 主动查询命令：不走 HomingLog.Log（已被静默），直接输出
                LuaCsLogger.LogMessage($"[追踪] {HomingConfig.Describe()}", Color.LightGreen);
                LuaCsLogger.LogMessage($"[追踪] 当前活跃追踪射弹：{HomingTracker.ActiveCount}", Color.LightGreen);
            }, null, false);
        }
    }

    public enum HomingMode { Velocity, Mouse }

    /// <summary>单个射弹条目的追踪参数。加载时已完成角度→cos、距离→平方的预换算。</summary>
    public sealed class HomingParams
    {
        public HomingMode Mode;
        public float AcquireConeCos;
        public float AcquireRangeSqr;
        public float SteeringMagnitude;
        public float MinimumSpeed;
        public float AccelerationMagnitude;
        public float AcquireInterval;
        public bool LockOn;
        public float MaxLifetime;
        /// <summary>追踪解锁距离（已平方预换算）：射弹离出膛点超过该距离才开始追踪。</summary>
        public float ArmDistanceSqr;
        /// <summary>追踪解锁时间保底（秒）：超过该时间仍未飞出 armDist 也强制解锁，防止卡弹永不追踪。</summary>
        public float ArmTime;
        /// <summary>引导释放距离（模拟单位）：离目标这么近就交棒给弹道，之后不再转向。0 = 一直追踪（旧行为）。</summary>
        public float ReleaseDist;
        /// <summary>穿身补伤半径（模拟单位）：飞过最近点时最近距离在这以内还没被引擎判成命中，就补一刀。</summary>
        public float PassHitDist;
        /// <summary>是否允许追踪潜艇舱室内部的目标（登船敌人/跑进艇里的怪）。
        /// false = 艇内目标一律不锁、已锁定的进艇即脱锁，防止追踪弹被"勾"回自家船体。</summary>
        public bool TrackInsideSub;

        public HomingParams Clone() => (HomingParams)MemberwiseClone();
    }

    /// <summary>
    /// 配置：多来源合并，查询优先级 外部运行时注册 > 配置文件精确 id > 配置文件最长 idPrefix 前缀 > prefab tag。
    /// 配置文件扫描所有启用内容包的 Config/homing_config.xml，按加载顺序合并（同 id 后者覆盖前者）；
    /// 参数解析逐级回落：条目属性 → 该文件的 Default（可层叠） → 代码常量。
    /// </summary>
    public static class HomingConfig
    {
        /// <summary>外部模组约定 tag：射弹物品 XML 加此 tag 即按默认参数追踪（velocity 模式）。</summary>
        public const string HomingTag = "touhou_homing";
        /// <summary>同上，但用 mouse 模式（鼠标引导）。</summary>
        public const string HomingMouseTag = "touhou_homing_mouse";

        public static bool Enabled = true;
        public static int MaxActive = 128;
        public static int ScansPerFrame = 8;

        static HomingParams tagVelocity = CodeDefaults();
        static HomingParams tagMouse = NewMouseDefault();

        static readonly Dictionary<string, HomingParams> byId =
            new Dictionary<string, HomingParams>(StringComparer.OrdinalIgnoreCase);
        static readonly List<KeyValuePair<string, HomingParams>> byPrefix =
            new List<KeyValuePair<string, HomingParams>>();
        static readonly Dictionary<string, HomingParams> byIdRuntime =
            new Dictionary<string, HomingParams>(StringComparer.OrdinalIgnoreCase);

        static string ConfigPath =>
            Path.Combine(HomingPlugin.PackageDir, "Config", "homing_config.xml");

        public static void RegisterRuntime(string id, HomingParams p) => byIdRuntime[id] = p;
        public static bool UnregisterRuntime(string id) => byIdRuntime.Remove(id);

        /// <summary>外部运行时注册的参数表：以当前默认参数为底，覆写 Lua table 里给出的键。</summary>
        public static HomingParams NewRuntimeParams(object luaTable)
        {
            var p = (tagVelocity ?? CodeDefaults()).Clone();
            var reader = LuaTableReader.Wrap(luaTable);
            if (reader != null) ApplyParams(p, reader.GetAsString);
            return p;
        }

        public static bool Find(string identifier, out HomingParams p)
        {
            if (identifier != null && byIdRuntime.TryGetValue(identifier, out p)) return true;
            if (identifier != null && byId.TryGetValue(identifier, out p)) return true;
            HomingParams best = null;
            int bestLen = -1;
            foreach (var kv in byPrefix)
            {
                if (kv.Key.Length > bestLen &&
                    identifier != null && identifier.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    best = kv.Value;
                    bestLen = kv.Key.Length;
                }
            }
            p = best;
            return p != null;
        }

        /// <summary>tag 兜底：射弹 prefab 带 touhou_homing / touhou_homing_mouse 时按默认参数追踪。</summary>
        public static bool FindByTag(Item item, out HomingParams p)
        {
            var tags = item.Prefab.Tags;
            if (tags.Contains(HomingTag)) { p = tagVelocity; return true; }
            if (tags.Contains(HomingMouseTag)) { p = tagMouse; return true; }
            p = null;
            return false;
        }

        public static void Load()
        {
            byId.Clear();
            byPrefix.Clear();
            Enabled = true;
            MaxActive = 128;
            ScansPerFrame = 8;
            HomingParams runningDefault = CodeDefaults();
            int files = 0, packages = 0;

            EnsureOwnTemplate();
            try
            {
                // 扫描所有启用内容包的 Config/homing_config.xml，按加载顺序合并
                foreach (var pkg in ContentPackageManager.RegularPackages)
                {
                    packages++;
                    string path;
                    try { path = Path.Combine(pkg.Dir, "Config", "homing_config.xml"); }
                    catch { continue; }
                    if (!File.Exists(path)) continue;
                    MergeFile(path, pkg.Name, ref runningDefault);
                    files++;
                }
            }
            catch (Exception ex) { HomingLog.Warn($"配置扫描失败：{ex.Message}"); }

            tagVelocity = runningDefault.Clone();
            tagMouse = runningDefault.Clone();
            tagMouse.Mode = HomingMode.Mouse;
            HomingLog.Log($"配置已加载：{(Enabled ? "启用" : "停用")}，文件 {files}（扫描包 {packages}），" +
                          $"精确条目 {byId.Count}，前缀条目 {byPrefix.Count}，外部注册 {byIdRuntime.Count}");
        }

        static void MergeFile(string path, string sourceName, ref HomingParams runningDefault)
        {
            XElement root;
            try { root = XDocument.Load(path).Root; }
            catch (Exception ex) { HomingLog.Warn($"配置解析失败（{sourceName}）：{ex.Message}"); return; }
            if (root == null) return;

            Enabled = ParseBool((string)root.Attribute("enabled"), "enabled", Enabled);
            MaxActive = ParseInt((string)root.Attribute("maxActive"), "maxActive", MaxActive);
            ScansPerFrame = ParseInt((string)root.Attribute("scansPerFrame"), "scansPerFrame", ScansPerFrame);

            var defEl = root.Element("Default");
            if (defEl != null)
            {
                var d = runningDefault.Clone();
                ApplyParams(d, name => (string)defEl.Attribute(name));
                runningDefault = d;
            }
            foreach (var el in root.Elements("Projectile"))
            {
                string id = (string)el.Attribute("id");
                string prefix = (string)el.Attribute("idPrefix");
                if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(prefix))
                {
                    HomingLog.Warn($"配置条目缺 id/idPrefix（{sourceName}），已跳过");
                    continue;
                }
                HomingParams p = runningDefault.Clone();
                ApplyParams(p, name => (string)el.Attribute(name));
                if (!string.IsNullOrEmpty(id))
                {
                    byId[id] = p; // 同 id：加载顺序靠后的包覆盖先前的
                }
                else
                {
                    byPrefix.RemoveAll(kv => string.Equals(kv.Key, prefix, StringComparison.OrdinalIgnoreCase));
                    byPrefix.Add(new KeyValuePair<string, HomingParams>(prefix, p));
                }
            }
        }

        static HomingParams CodeDefaults() => new HomingParams
        {
            Mode = HomingMode.Velocity,
            AcquireConeCos = (float)Math.Cos(Math.PI / 4),
            AcquireRangeSqr = 2000f * 2000f,
            SteeringMagnitude = 5f,
            MinimumSpeed = 0f,
            AccelerationMagnitude = 0.25f,
            AcquireInterval = 0.2f,
            LockOn = true,
            MaxLifetime = 10f,
            // 追踪解锁：飞出 armDist 距离或超过 armTime 秒（保底）后才开始追踪，防止贴脸即锁定
            // 距离尺度参照：1 米 ≈ 100 模拟单位（见 homing_config.xml 注释），200 ≈ 2 米，远超贴脸/近战距离
            ArmDistanceSqr = 200f * 200f,
            ArmTime = 2f,
            // 引导释放 + 穿身补伤（见 Docs/通用射弹追踪-可行方案.md §4.6）：300 = 3 米交棒，100 = 1 米内算贴脸穿过
            ReleaseDist = 300f,
            PassHitDist = 100f,
            // 默认仍可追踪艇内目标（保持既有行为）；设为 false 可让艇内登船敌人不再"勾"走追踪弹
            TrackInsideSub = true,
        };

        static HomingParams NewMouseDefault()
        {
            var p = CodeDefaults();
            p.Mode = HomingMode.Mouse;
            return p;
        }

        /// <summary>参数解析：get(name) 返回原始字符串或 null。XML 属性与 Lua table 两个来源共用。</summary>
        static void ApplyParams(HomingParams p, Func<string, string> get)
        {
            string mode = get("mode");
            if (mode != null)
            {
                if (string.Equals(mode, "mouse", StringComparison.OrdinalIgnoreCase)) p.Mode = HomingMode.Mouse;
                else if (string.Equals(mode, "velocity", StringComparison.OrdinalIgnoreCase)) p.Mode = HomingMode.Velocity;
                else HomingLog.Warn($"未知 mode「{mode}」，保持 {p.Mode}");
            }
            float coneDeg = ParseFloat(get("coneDeg"), "coneDeg", -1f);
            if (coneDeg >= 0f) p.AcquireConeCos = (float)Math.Cos(coneDeg * Math.PI / 180.0);
            float range = ParseFloat(get("range"), "range", -1f);
            if (range >= 0f) p.AcquireRangeSqr = range * range;
            p.SteeringMagnitude = ParseFloat(get("steering"), "steering", p.SteeringMagnitude);
            p.MinimumSpeed = ParseFloat(get("minSpeed"), "minSpeed", p.MinimumSpeed);
            p.AccelerationMagnitude = ParseFloat(get("accel"), "accel", p.AccelerationMagnitude);
            p.AcquireInterval = ParseFloat(get("acquireInterval"), "acquireInterval", p.AcquireInterval);
            p.LockOn = ParseBool(get("lockOn"), "lockOn", p.LockOn);
            p.MaxLifetime = ParseFloat(get("maxLifetime"), "maxLifetime", p.MaxLifetime);
            float armDist = ParseFloat(get("armDist"), "armDist", -1f);
            if (armDist >= 0f) p.ArmDistanceSqr = armDist * armDist;
            p.ArmTime = ParseFloat(get("armTime"), "armTime", p.ArmTime);
            p.ReleaseDist = ParseFloat(get("releaseDist"), "releaseDist", p.ReleaseDist);
            p.PassHitDist = ParseFloat(get("passHitDist"), "passHitDist", p.PassHitDist);
            p.TrackInsideSub = ParseBool(get("trackInsideSub"), "trackInsideSub", p.TrackInsideSub);
        }

        public static string Describe()
        {
            var parts = new List<string>();
            foreach (var kv in byId) parts.Add($"{kv.Key}(mode={kv.Value.Mode})");
            foreach (var kv in byPrefix) parts.Add($"{kv.Key}*(mode={kv.Value.Mode})");
            foreach (var kv in byIdRuntime) parts.Add($"{kv.Key}(外部,mode={kv.Value.Mode})");
            return parts.Count > 0 ? string.Join(", ", parts) : "（无追踪条目）";
        }

        static void EnsureOwnTemplate()
        {
            if (File.Exists(ConfigPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                    "<HomingConfig enabled=\"true\" maxActive=\"128\" scansPerFrame=\"8\">\n" +
                    "  <Default range=\"2000\" coneDeg=\"45\" mode=\"velocity\" steering=\"5\"\n" +
                    "           minSpeed=\"0\" accel=\"0.25\" acquireInterval=\"0.2\" lockOn=\"true\" maxLifetime=\"10\"\n" +
                    "           armDist=\"200\" armTime=\"2\" releaseDist=\"300\" passHitDist=\"100\"/>\n" +
                    "  <!-- armDist/armTime：追踪解锁条件（任一满足即解锁，距离是主条件、时间是保底，armDist=0 表示立即追踪） -->\n" +
                    "  <!-- releaseDist：离目标这么近就释放引导、之后不再转向（0 = 一直追踪）；passHitDist：飞过最近点时多远以内算穿身、补一刀 -->\n" +
                    "  <!-- <Projectile id=\"射弹identifier\"/> 详见 Docs/通用射弹追踪-可行方案.md -->\n" +
                    "</HomingConfig>\n");
                HomingLog.Log("配置文件不存在，已生成模板");
            }
            catch (Exception ex) { HomingLog.Warn($"配置模板写入失败：{ex.Message}"); }
        }

        static float ParseFloat(string raw, string name, float fallback)
        {
            if (raw == null) return fallback;
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return v;
            HomingLog.Warn($"参数 {name}=\"{raw}\" 解析失败，用 {fallback}");
            return fallback;
        }

        static int ParseInt(string raw, string name, int fallback)
        {
            if (raw == null) return fallback;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            HomingLog.Warn($"参数 {name}=\"{raw}\" 解析失败，用 {fallback}");
            return fallback;
        }

        static bool ParseBool(string raw, string name, bool fallback)
        {
            if (raw == null) return fallback;
            if (bool.TryParse(raw, out bool v)) return v;
            HomingLog.Warn($"参数 {name}=\"{raw}\" 解析失败，用 {fallback}");
            return fallback;
        }
    }

    /// <summary>
    /// 反射读取 MoonSharp Table（避免编译期引用 MoonSharp.Interpreter：模组编译上下文不保证引用它）。
    /// 仅在外部模组注册时调用，非热路径。
    /// </summary>
    public sealed class LuaTableReader
    {
        readonly object table;
        readonly MethodInfo getMethod;
        readonly PropertyInfo typeProp, numberProp, boolProp, stringProp;

        LuaTableReader(object table, MethodInfo get, PropertyInfo type, PropertyInfo num, PropertyInfo b, PropertyInfo s)
        {
            this.table = table;
            getMethod = get;
            typeProp = type;
            numberProp = num;
            boolProp = b;
            stringProp = s;
        }

        public static LuaTableReader Wrap(object table)
        {
            if (table == null) return null;
            try
            {
                var get = table.GetType().GetMethod("Get", new[] { typeof(string) });
                if (get == null) return null;
                object sample = get.Invoke(table, new object[] { "mode" });
                if (sample == null) return null;
                var dt = sample.GetType();
                var reader = new LuaTableReader(table, get,
                    dt.GetProperty("Type"), dt.GetProperty("Number"),
                    dt.GetProperty("Boolean"), dt.GetProperty("String"));
                return reader.typeProp != null ? reader : null;
            }
            catch { return null; }
        }

        /// <summary>按键取值并统一转成 XML 属性风格的字符串（nil/缺失/异常 → null）。</summary>
        public string GetAsString(string name)
        {
            try
            {
                object dv = getMethod.Invoke(table, new object[] { name });
                if (dv == null) return null;
                switch (typeProp.GetValue(dv)?.ToString())
                {
                    case "String": return (string)stringProp.GetValue(dv);
                    case "Number": return ((double)numberProp.GetValue(dv)).ToString(CultureInfo.InvariantCulture);
                    case "Boolean": return (bool)boolProp.GetValue(dv) ? "true" : "false";
                    default: return null;
                }
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// 手持武器/状态效果入口：Projectile.Shoot postfix。
    /// 注意：炮塔不经过此方法（见 HomingTurretPatch）。
    /// </summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Shoot))]
    public static class HomingShootPatch
    {
        static void Postfix(Projectile __instance, Character user)
        {
            HomingLaunchCommon.OnProjectileLaunched(__instance.Item, __instance, user);
        }
    }

    /// <summary>
    /// 炮塔入口：Turret.Launch(Item projectile, Character user, float?, float) postfix。
    /// IL 验证（本机 1.12.7）：Projectile.Shoot 全游戏仅 RangedWeapon.Use / StatusEffect.SpawnItem /
    /// Projectile.ClientEventRead 三处调用，炮塔发射完全绕过它，由 Turret.Launch 手工完成
    /// SetTransform + LinearVelocity + Projectile.User/Attacker 赋值。
    /// 参数名未验证，用位置绑定 __0/__1。
    /// </summary>
    [HarmonyPatch(typeof(Turret), "Launch")]
    public static class HomingTurretPatch
    {
        // GetFriendlyTeam 是 private（dnlib 验证），缓存反射信息；仅 user=null（自动炮塔）的稀有路径才调用
        static readonly MethodInfo getFriendlyTeam =
            typeof(Turret).GetMethod("GetFriendlyTeam", BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(Turret __instance, Item __0, Character __1)
        {
            if (__0 == null) return;
            CharacterTeamType? teamHint = null;
            if (__1 == null)
            {
                if (getFriendlyTeam != null)
                    teamHint = (CharacterTeamType)getFriendlyTeam.Invoke(__instance, null);
                else
                    HomingLog.Warn("找不到 Turret.GetFriendlyTeam，自动炮塔射弹将无法追踪");
            }
            HomingLaunchCommon.OnProjectileLaunched(__0, __0.GetComponent<Projectile>(), __1, teamHint);
        }
    }

    /// <summary>两个发射入口的公共逻辑：追踪登记。</summary>
    public static class HomingLaunchCommon
    {
        public static void OnProjectileLaunched(Item item, Projectile proj, Character user,
                                                CharacterTeamType? teamHint = null)
        {
            if (item == null) return;
            if (!HomingConfig.Enabled) return;
            // 服务端权威：纯客户端上下文不登记
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) return;
            if (!HomingConfig.Find(item.Prefab.Identifier.Value, out HomingParams p) &&
                !HomingConfig.FindByTag(item, out p)) return;
            if (proj == null || proj.Hitscan) return;
            if (item.body == null) return;

            CharacterTeamType team;
            if (user != null) team = user.TeamID;
            else if (teamHint.HasValue) team = teamHint.Value;
            else return; // user=null 且无队伍信息：无法判定敌我，不登记

            HomingTracker.Register(item, p, user, team);
        }
    }

    /// <summary>
    /// 每帧驱动。客户端/单人挂 GameMain.Update，专用服务器挂 GameServer.Update
    /// （GameServer 在 DedicatedServer.dll，编译期不一定引用得到，用反射解析）。
    /// 编译期分流保证 listen server 不会两个挂点同时触发。
    /// </summary>
#if SERVER
    [HarmonyPatch]
    public static class HomingTickPatch
    {
        static MethodBase TargetMethod()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Barotrauma.Networking.GameServer");
                var m = t?.GetMethod("Update", flags);
                if (m != null) return m;
            }
            HomingLog.Warn("HomingTickPatch: 找不到 GameServer.Update，追踪不会生效");
            return null;
        }

        static void Postfix()
        {
            if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsServer) return;
            HomingProfiler.RunTrackers();
        }
    }
#else
    [HarmonyPatch(typeof(GameMain), "Update")]
    public static class HomingTickPatch
    {
        static void Postfix()
        {
            // 纯客户端上下文不跑；单人（null）与 listen server 宿主（IsServer）跑
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) return;
            if (GameMain.Instance != null && GameMain.Instance.Paused) return;
            HomingProfiler.RunTrackers();
        }
    }
#endif

    /// <summary>活跃射弹列表、目标捕获、转向。全部静态，零实例化；热循环无 LINQ/枚举器分配。</summary>
    public static class HomingTracker
    {
        sealed class Entry
        {
            public Item Item;
            public HomingParams Params;
            public Character Shooter;
            public CharacterTeamType Team;   // 敌我判定依据：射手队伍，或自动炮塔的 GetFriendlyTeam
            public Character Target;
            public double NextAcquire;
            public double ExpireAt;
            public bool Activated;
            public Vector2 LaunchPos;
            public double ArmAt;
            public bool Armed;
            /// <summary>已释放引导：进到 releaseDist 以内后不再施加任何冲量，永久弹道飞行。</summary>
            public bool Released;
            /// <summary>对当前目标的最近距离（换目标时重置成 float.MaxValue），用来判断"这一帧飞过了最近点"。</summary>
            public float MinDist = float.MaxValue;
        }

        static readonly List<Entry> entries = new List<Entry>(128);
        public static int ActiveCount => entries.Count;

        public static void Clear() => entries.Clear();

        public static void Register(Item item, HomingParams p, Character shooter, CharacterTeamType team)
        {
            if (entries.Count >= HomingConfig.MaxActive) return;
            // 捕获时刻按槽位错峰，避免同帧集体扫描
            double jitter = (entries.Count % 8) / 8.0 * p.AcquireInterval;
            entries.Add(new Entry
            {
                Item = item,
                Params = p,
                Shooter = shooter,
                Team = team,
                NextAcquire = Timing.TotalTime + jitter,
                ExpireAt = Timing.TotalTime + p.MaxLifetime,
                LaunchPos = item.WorldPosition,
                ArmAt = Timing.TotalTime + p.ArmTime,
            });
        }

        public static void Tick()
        {
            if (entries.Count == 0) return;
            double now = Timing.TotalTime;
            int scansLeft = HomingConfig.ScansPerFrame;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry e = entries[i];
                Item item = e.Item;
                if (item.Removed || now >= e.ExpireAt || item.body == null)
                {
                    entries[i] = entries[entries.Count - 1];
                    entries.RemoveAt(entries.Count - 1);
                    continue;
                }

                PhysicsBody body = item.body;
                Vector2 vel = body.LinearVelocity;
                float speedSqr = vel.LengthSquared();
                HomingParams p = e.Params;

                // 出膛帧速度方向未稳定，等获得初速再激活
                if (!e.Activated)
                {
                    if (speedSqr < 0.01f) continue;
                    e.Activated = true;
                }

                // 保险解锁：飞出足够距离或超过保底时间后才开始追踪，避免贴脸敌人被立刻锁定。
                // 每帧开销仅一次向量差 + LengthSquared，比下方转向/捕获计算便宜得多，性能可忽略。
                if (!e.Armed)
                {
                    float armDistSqr = (item.WorldPosition - e.LaunchPos).LengthSquared();
                    if (armDistSqr < p.ArmDistanceSqr && now < e.ArmAt) continue;
                    e.Armed = true;
                }

                Character target = e.Target;
                if (target != null && (target.Removed || target.IsDead ||
                                       (!p.TrackInsideSub && IsInsideSub(target))))
                {
                    target = null;
                    e.Target = null;
                    e.MinDist = float.MaxValue; // 目标没了/进艇脱锁，最近距离跟着作废
                }

                bool due = now >= e.NextAcquire;
                if (due && scansLeft > 0 && (target == null || !p.LockOn))
                {
                    scansLeft--;
                    HomingProfiler.Scans++;
                    Character newTarget = Acquire(e, vel);
                    if (newTarget != e.Target) e.MinDist = float.MaxValue; // 换目标才重置，LockOn=false 重扫到同一个不算换
                    e.Target = newTarget;
                    target = newTarget;
                    e.NextAcquire = now + p.AcquireInterval;
                }
                // 扫描预算用完则顺延到下帧（NextAcquire 保持过期状态，下帧自然优先处理）

                if (target == null) continue;

                Vector2 toTarget = target.WorldPosition - item.WorldPosition;
                float dist = toTarget.Length();

                // 穿身补伤兜底：这一帧比历史最近距离还远 → 上一帧左右已经飞过最近点。
                // 最近点在 passHitDist（默认 1 米）以内还没打出伤害，就是引擎没登记命中
                // （坐标系失配的幽灵弹、碰撞被先前命中关掉等），按伤害入口补一刀再把弹移除。
                // HiddenInGame 是引擎登记命中后立刻置位的标志，它 true 时绝不能补，否则双倍伤害。
                if (dist > e.MinDist)
                {
                    if (e.MinDist <= p.PassHitDist && !item.HiddenInGame)
                    {
                        HomingFallbackHit.Apply(item, e.Shooter, target);
                        entries[i] = entries[entries.Count - 1];
                        entries.RemoveAt(entries.Count - 1);
                        continue;
                    }
                }
                else
                {
                    e.MinDist = dist;
                }

                if (speedSqr < 0.0001f) continue;

                // 引导释放：进到 releaseDist 以内就交棒给弹道，之后不再施加任何冲量。
                // 一直转向会让弹贴着目标绕圈、引擎反而碰不上，永远没伤害。releaseDist=0 表示不释放（旧行为）。
                if (!e.Released && dist <= p.ReleaseDist) e.Released = true;
                if (e.Released) continue;

                float speed = (float)Math.Sqrt(speedSqr);
                Vector2 curDir = vel / speed;
                if (dist < 1f) continue;
                Vector2 targetDir = toTarget / dist;

                if (speed < p.MinimumSpeed)
                    body.ApplyLinearImpulse(curDir * (p.AccelerationMagnitude * (p.MinimumSpeed - speed)));
                body.ApplyLinearImpulse((targetDir - curDir) * p.SteeringMagnitude);
            }
        }

        static Character Acquire(Entry e, Vector2 vel)
        {
            HomingParams p = e.Params;
            Vector2 origin;
            Vector2 coneDir;
            if (p.Mode == HomingMode.Mouse)
            {
                Character shooter = e.Shooter;
                if (shooter == null || shooter.Removed) return null;
                // 锥心/原点/距离全部锚在射手（君王语义）：射弹飞远后仍可按当前瞄准方向重新捕获。
                // 必须 CursorWorldPosition：CursorPosition 在舱内是潜艇相对坐标，与世界坐标相减会混入潜艇偏移。
                origin = shooter.WorldPosition;
                coneDir = shooter.CursorWorldPosition - shooter.WorldPosition;
            }
            else
            {
                // 速度模式：锥心/原点/距离都锚在射弹自身。注意射弹飞离后锥角随速度转向，
                // 已飞离目标的射弹几何上不可能重新捕获——需要"鼠标引导再捕获"请用 mode="mouse"。
                origin = e.Item.WorldPosition;
                coneDir = vel;
            }
            float coneLen = coneDir.Length();
            if (coneLen < 0.001f) return null;
            coneDir /= coneLen;

            Character shooter0 = e.Shooter;
            Character best = null;
            float bestDot = p.AcquireConeCos;
            var list = Character.CharacterList;
            for (int i = 0; i < list.Count; i++)
            {
                Character c = list[i];
                if (c == null || c.Removed || c.IsDead || c == shooter0) continue;
                if (c.TeamID == e.Team) continue;
                if (!p.TrackInsideSub && IsInsideSub(c)) continue;
                Vector2 offset = c.WorldPosition - origin;
                float distSqr = offset.LengthSquared();
                if (distSqr > p.AcquireRangeSqr || distSqr < 1f) continue;
                float dot = Vector2.Dot(offset / (float)Math.Sqrt(distSqr), coneDir);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>
        /// 目标是否真的在潜艇舱室内部（登船敌人 / 跑进艇里的怪）。
        /// 用 CurrentHull（每帧对所有角色更新的所在舱室，见 Character.Update）+ 舱室所属潜艇判定：
        /// 在艇壳外扒着或在水中游的目标 CurrentHull 为 null，不算"艇内"，照常追踪。
        /// </summary>
        static bool IsInsideSub(Character c) => c.CurrentHull != null && c.CurrentHull.Submarine != null;
    }

    /// <summary>
    /// 穿身补伤的共用善后：射弹从目标身上飞过（最近距离在 passHitDist 以内）却没被引擎判成命中时，
    /// 按引擎的伤害入口补一次命中再把弹移除，表现得就像命中消失。
    /// 通用追踪和君王迁移版（MonarchHoming.cs）共用；"飞过最近点且引擎没登记命中"由调用方判断。
    /// </summary>
    public static class HomingFallbackHit
    {
        /// <summary>
        /// 引擎是否已经对受害者登记过命中（Hits 里出现过该角色的肢体）。
        /// removeonhit=true 的弹引擎命中后会置 HiddenInGame，调用方的判断已覆盖；
        /// removeonhit=false 的穿透弹 HiddenInGame 永远不置位，必须靠这个兜底防双倍伤害。
        /// </summary>
        public static bool EngineAlreadyHit(Item round, Entity target)
        {
            Projectile proj = round.GetComponent<Projectile>();
            if (proj == null) return false;
            Character victim = ResolveVictim(target);
            if (victim == null || victim.AnimController == null) return false;
            foreach (var hitBody in proj.Hits)
            {
                if (hitBody?.UserData is Limb hitLimb && hitLimb.character == victim) return true;
            }
            return false;
        }

        /// <summary>
        /// 挨打的是谁：角色目标直接用；鱼叉目标是反查它插到的四肢所属的角色
        /// （君王场景里弹追的是鱼叉，不是人，光看目标解析不出受害者）。
        /// </summary>
        public static Character ResolveVictim(Entity target)
        {
            if (target is Character character) return character;
            if (target is Item item && item.GetComponent<Projectile>()?.StickTarget?.UserData is Limb limb)
                return limb.character;
            return null;
        }

        /// <summary>补一次命中并把射弹移除；解析不出受害者就只移除不补伤。返回是否真的补上了伤害。</summary>
        public static bool Apply(Item round, Character shooter, Entity target)
        {
            // 引擎已经命中过（穿透弹场景）就什么都不做，防双倍伤害 / 双倍效果
            if (EngineAlreadyHit(round, target)) return false;

            if (shooter != null && shooter.Removed) shooter = null;
            Character victim = ResolveVictim(target);
            Limb limb = ResolveLimb(victim);
            // 引擎路径里弹停在命中接触点（目标表面），伤害位置、爆炸、溅射子弹的生成点全在那里；
            // 兜底路径弹已飞过目标，不挪回去的话溅射子弹会生成在目标体内/身后的几何里，
            // 出生即碰撞触发自己的 OnImpact → Condition-100 →"瞬间消失"。
            RepositionToImpactPoint(round, victim, limb);
            // 复刻引擎 RemoveOnHit 命中时的处置：冻结刚体。挪位后残余速度会再次朝目标飞，
            // 不冻结可能在移除生效前撞出第二次命中（双倍伤害/效果）。
            if (round.body?.FarseerBody != null) round.body.FarseerBody.Enabled = false;
            bool damaged = TryDamage(round, shooter, victim, limb);
            // 复刻引擎 HandleProjectileCollision 的命中善后：OnSuccess/OnFailure + OnImpact 状态效果
            // （散射生成、爆炸、音效、粒子都挂在这些效果上），并广播客户端事件。
            // 未复刻引擎对"被击中肢体自身 attack"的反击效果（怪物受击反应类），当前需求不涉及。
            ApplyHitEffects(round, shooter, victim, limb);
            if (!round.Removed) Entity.Spawner?.AddItemToRemoveQueue(round);
            return damaged;
        }

        /// <summary>躯干优先、其次第一个未断肢体；补伤、命中效果与挪位共用同一个肢体。</summary>
        static Limb ResolveLimb(Character victim)
        {
            if (victim?.AnimController == null) return null;
            Limb limb = victim.AnimController.GetLimb(LimbType.Torso);
            if (limb != null && !limb.IsSevered) return limb;
            var limbs = victim.AnimController.Limbs;
            for (int i = 0; i < limbs.Length; i++)
            {
                Limb candidate = limbs[i];
                if (candidate != null && !candidate.IsSevered) return candidate;
            }
            return null;
        }

        /// <summary>
        /// 把弹挪回"可见命中点"：以被击肢体自身为锚点，沿来向退到肢体表面外 0.15 米。
        /// 用 WorldPosition 差值做平移（自动处理潜艇/关卡坐标系差异），换算成 SimPosition 增量后
        /// SetTransform（增量法帧无关：WorldPosition = 帧偏移 + ToDisplayUnits(SimPosition)）。
        /// </summary>
        static void RepositionToImpactPoint(Item round, Character victim, Limb limb)
        {
            if (victim == null || limb == null || round.body == null) return;
            Vector2 vel = round.body.LinearVelocity;
            if (vel.LengthSquared() < 0.0001f) return;
            Vector2 dir = Vector2.Normalize(vel);
            // 锚在被击肢体自身（比角色整体中心准），沿来向退到肢体表面外一点点：
            // - 余量保留 0.15 米：贴太紧会生成在肢体内部"出生即碰撞"（旧 bug）；0.15 米在弹速下
            //   约 3 毫秒，朝怪物飞的溅射弹会在生成后一帧内命中，观感等同"在怪物身上命中"（引擎路径）；
            // - 不再设 1 米保底：大余量会让小目标出现明显的"飞一段才命中"空档（实测反馈）；
            // - GetMaxExtent() 是最大半程尺寸，恒 ≥ 任何方向的真实表面距离（圆形体精确），
            //   估算只会偏外不会偏内，偏外的部分就是上述那 0.15 米以内的余量。
            Vector2 anchor = limb.WorldPosition;
            float extentSim = Math.Max(limb.body?.GetMaxExtent() ?? 0f, 0.2f) + 0.15f;
            Vector2 desiredWorld = anchor - dir * ConvertUnits.ToDisplayUnits(extentSim);
            Vector2 simDelta = ConvertUnits.ToSimUnits(desiredWorld - round.WorldPosition);
            if (float.IsNaN(simDelta.X) || float.IsInfinity(simDelta.X) ||
                float.IsNaN(simDelta.Y) || float.IsInfinity(simDelta.Y)) return;
            round.SetTransform(round.SimPosition + simDelta, round.body.Rotation);
        }

        static bool TryDamage(Item round, Character shooter, Character victim, Limb limb)
        {
            if (victim == null || victim.AnimController == null || limb == null) return false;
            Projectile proj = round.GetComponent<Projectile>();
            if (proj == null || proj.Attack == null) return false;

            // 伤害走引擎入口 DoDamageToLimb（与引擎命中同一方法），生物按最大生命值的 affliction
            // 归一化（CharacterHealth.AddLimbAffliction：× 100/MaxVitality ×(1−抗性)）自动生效。
            victim.LastDamageSource = round; // 与引擎肢体分支一致：先记伤害来源（击杀归属等）
            proj.Attack.DoDamageToLimb(shooter, limb, round.WorldPosition, 1.0f, false);
            // 复刻引擎命中时给被击肢体的动量冲量（引擎在同一处做，命中击退手感一致）
            if (limb.body != null && round.body != null)
                limb.body.ApplyLinearImpulse(round.body.LinearVelocity * round.body.Mass);
            return true;
        }

        static void ApplyHitEffects(Item round, Character shooter, Character victim, Limb limb)
        {
            if (victim == null || limb == null || round.Removed) return;
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) return;
            Projectile proj = round.GetComponent<Projectile>();
            if (proj == null) return;

            ActionType conditional = ActionType.OnSuccess;
            // 与引擎同一掷骰：Rand.Range(0, 0.5, RandSync.Unsynced) > DegreeOfSuccess（IL 验证 1.12.7）
            if (shooter != null && Rand.Range(0.0f, 0.5f, Rand.RandSync.Unsynced) > proj.DegreeOfSuccess(shooter))
            {
                conditional = ActionType.OnFailure;
            }

            proj.ApplyStatusEffects(conditional, 1.0f, victim, limb, useTarget: victim, user: shooter);
            proj.ApplyStatusEffects(ActionType.OnImpact, 1.0f, victim, limb, useTarget: victim, user: shooter);

            // 与引擎一致：效果在服务端执行后广播给客户端做本地表现（音效/粒子/客户端效果）
            if (GameMain.NetworkMember is { IsServer: true } server)
            {
                server.CreateEntityEvent(round,
                    new Item.ApplyStatusEffectEventData(conditional, proj, victim, limb, victim, round.WorldPosition));
                server.CreateEntityEvent(round,
                    new Item.ApplyStatusEffectEventData(ActionType.OnImpact, proj, victim, limb, victim, round.WorldPosition));
            }
        }
    }

    /// <summary>
    /// 轻量性能自测：Enabled 打开时，每 5 秒汇总一条 tick 耗时 / 活跃追踪数 /
    /// 角色表扫描次数到控制台，在真实弹幕场景里核对性能，不靠估算。
    /// 关闭时 RunTrackers 只走两个 Tick 的空表早退路径，计时开销为零。
    /// </summary>
    public static class HomingProfiler
    {
        /// <summary>性能自测开关（默认关；排查性能时改 true，测完改回）。</summary>
        public static bool Enabled = false;

        /// <summary>周期内角色表全量扫描次数（通用追踪与君王共用计数），Frame 汇总后清零。</summary>
        public static int Scans;

        static long totalTicks;
        static long maxTicks;
        static int frames;
        static double nextLog = -1;

        public static void RunTrackers()
        {
            if (!Enabled)
            {
                HomingTracker.Tick();
                MonarchTracker.Tick();
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            HomingTracker.Tick();
            MonarchTracker.Tick();
            Frame(Stopwatch.GetTimestamp() - t0);
        }

        static void Frame(long elapsed)
        {
            frames++;
            totalTicks += elapsed;
            if (elapsed > maxTicks) maxTicks = elapsed;
            double now = Timing.TotalTime;
            if (nextLog < 0) { nextLog = now + 5.0; return; }
            if (now < nextLog) return;
            nextLog = now + 5.0;

            double toUs = 1_000_000.0 / Stopwatch.Frequency;
            double avgUs = frames > 0 ? totalTicks * toUs / frames : 0;
            LuaCsLogger.LogMessage($"[追踪][perf] 近 {frames} 帧：tick 均 {avgUs:F1} µs/帧，峰 {maxTicks * toUs:F0} µs，" +
                                   $"累计 {totalTicks * toUs / 1000.0:F2} ms；活跃追踪 {HomingTracker.ActiveCount}+{MonarchTracker.ActiveCount}，" +
                                   $"目标扫描 {Scans} 次", Color.LightGray);
            frames = 0;
            totalTicks = 0;
            maxTicks = 0;
            Scans = 0;
        }
    }

    public static class HomingLog
    {
        /// <summary>低频信息日志开关（插件加载、配置加载、外部注册等）。默认关，排查用。</summary>
        public static bool Verbose = false;

        public static void Log(string msg)
        {
            if (Verbose) LuaCsLogger.LogMessage($"[追踪] {msg}", Color.LightGreen);
        }
        public static void Warn(string msg) => LuaCsLogger.LogMessage($"[追踪] {msg}", Color.Orange);
    }
}
