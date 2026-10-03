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
                HomingLog.Debug("OnLoadCompleted: 内容重载，补丁/命令已注册，跳过");
                return;
            }
            oneTimeInitDone = true;

            // 先清掉本插件名下的历史补丁再重打，防止 reloadlua 残留堆叠
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(HomingShootPatch));
            harmony.PatchAll(typeof(HomingTurretPatch));
            harmony.PatchAll(typeof(HomingTickPatch));

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
            HomingLog.Log("射弹追踪插件已卸载");
        }

        object OnRoundStart(object[] args)
        {
            HomingTracker.Clear();
            HomingConfig.Load();
            return null;
        }

        object OnRoundEnd(object[] args)
        {
            HomingTracker.Clear();
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
                    "           armDist=\"200\" armTime=\"2\"/>\n" +
                    "  <!-- armDist/armTime：追踪解锁条件（任一满足即解锁，距离是主条件、时间是保底，armDist=0 表示立即追踪） -->\n" +
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
            HomingLaunchCommon.OnProjectileLaunched(__instance.Item, __instance, user, "shoot");
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
            HomingLaunchCommon.OnProjectileLaunched(__0, __0.GetComponent<Projectile>(), __1, "turret", teamHint);
        }
    }

    /// <summary>两个发射入口的公共逻辑：探针日志 + 追踪登记。</summary>
    public static class HomingLaunchCommon
    {
        static readonly Dictionary<string, int> probeCounts = new Dictionary<string, int>();
        static double nextSummary;

        public static void ResetProbe()
        {
            probeCounts.Clear();
            nextSummary = 0;
        }

        public static void OnProjectileLaunched(Item item, Projectile proj, Character user, string source,
                                                CharacterTeamType? teamHint = null)
        {
            if (item == null) return;
            Probe(item, proj, user, source, teamHint);

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
            else
            {
                HomingLog.Debug($"user=null 且无队伍信息，跳过登记：{item.Prefab.Identifier.Value}");
                return;
            }
            HomingTracker.Register(item, p, user, team);
        }

        static void Probe(Item item, Projectile proj, Character user, string source, CharacterTeamType? teamHint)
        {
            if (!HomingLog.DebugMode) return;
            string id = item.Prefab.Identifier.Value;
            if (!probeCounts.TryGetValue(id, out int n))
            {
                string context = GameMain.NetworkMember == null ? "SP"
                    : GameMain.NetworkMember.IsServer ? "server" : "client";
                string userDesc = user != null ? $"{user.Name}(bot={user.IsBot}, team={user.TeamID})"
                    : teamHint.HasValue ? $"null(team={teamHint.Value})" : "null";
                string launcher = proj != null && proj.Launcher != null
                    ? proj.Launcher.Prefab.Identifier.Value : "null";
                string speed = item.body == null ? "no-body"
                    : item.body.LinearVelocity.Length().ToString("F1");
                HomingLog.Log($"[{context}/{source}] 首发 {id}: hitscan={(proj != null && proj.Hitscan)}, " +
                              $"user={userDesc}, launcher={launcher}, |v|={speed}");
                probeCounts[id] = 1;
            }
            else
            {
                probeCounts[id] = n + 1;
            }

            if (Timing.TotalTime >= nextSummary && probeCounts.Count > 0)
            {
                nextSummary = Timing.TotalTime + 10.0;
                var parts = new List<string>();
                foreach (var kv in probeCounts) parts.Add($"{kv.Key}×{kv.Value}");
                HomingLog.Debug($"发射统计: {string.Join(", ", parts)}");
            }
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
            HomingTracker.Tick();
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
            HomingTracker.Tick();
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
        }

        static readonly List<Entry> entries = new List<Entry>(128);
        public static int ActiveCount => entries.Count;

        public static void Clear() => entries.Clear();

        public static void Register(Item item, HomingParams p, Character shooter, CharacterTeamType team)
        {
            if (shooter == null && p.Mode == HomingMode.Mouse)
            {
                // 自动炮塔没有鼠标可引导，mouse 模式永远捕获不到目标
                HomingLog.Debug($"{item.Prefab.Identifier.Value}：无射手（自动炮塔）配 mouse 模式无效，请改用 velocity");
            }
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
                    if (HomingLog.DebugMode)
                    {
                        string reason = item.Removed ? "已移除" : now >= e.ExpireAt ? "超时" : "失去物理体";
                        HomingLog.Debug($"{item.Prefab.Identifier.Value} 退出追踪：{reason}" +
                                        $"（目标={(e.Target != null ? e.Target.Name : "无")}）");
                    }
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
                if (target != null && (target.Removed || target.IsDead))
                {
                    target = null;
                    e.Target = null;
                }

                bool due = now >= e.NextAcquire;
                if (due && scansLeft > 0 && (target == null || !p.LockOn))
                {
                    scansLeft--;
                    Character newTarget = Acquire(e, vel);
                    if (HomingLog.DebugMode && newTarget != e.Target)
                    {
                        HomingLog.Debug(newTarget != null
                            ? $"{item.Prefab.Identifier.Value} 锁定 {newTarget.Name}"
                            : $"{item.Prefab.Identifier.Value} 未捕获到目标");
                    }
                    e.Target = newTarget;
                    target = newTarget;
                    e.NextAcquire = now + p.AcquireInterval;
                }
                // 扫描预算用完则顺延到下帧（NextAcquire 保持过期状态，下帧自然优先处理）

                if (target == null || speedSqr < 0.0001f) continue;

                float speed = (float)Math.Sqrt(speedSqr);
                Vector2 curDir = vel / speed;
                Vector2 toTarget = target.WorldPosition - item.WorldPosition;
                float dist = toTarget.Length();
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
    }

    public static class HomingLog
    {
        public static bool DebugMode = false; // 调试时改为 true

        public static void Log(string msg)
        {
            if (DebugMode) LuaCsLogger.LogMessage($"[追踪] {msg}", Color.LightGreen);
        }
        public static void Warn(string msg) => LuaCsLogger.LogMessage($"[追踪] {msg}", Color.Orange);
        public static void Debug(string msg)
        {
            if (DebugMode) LuaCsLogger.LogMessage($"[追踪][dbg] {msg}", Color.LightGray);
        }
    }
}
