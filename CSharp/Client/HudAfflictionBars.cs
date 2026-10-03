using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

// 通用 HUD affliction 进度条系统：
// 任何 affliction（能量/正面/负面/中性）在 Config/hud_bars.xml 里登记一行 <Bar> 后，
// 玩家身上该 affliction 的强度就以竖条画在屏幕上（自下而上填充，位置可调）。
// 数据直接读本地角色 affliction 强度（联机自带同步），零网络消息。
// 设置界面在 Lua 侧（Touhou_Mod_Hotkey.lua 的 hudbars 页面）：Lua 写用户覆盖层
// TouhouHudBarsConfig.txt，本侧每帧查 mtime 即时重载；合并状态写 TouhouHudBarsState.txt 供 Lua 读取。

namespace Touhou.HudBars;

public sealed class HudBarsPlugin : IAssemblyPlugin
{
    private Harmony harmony;
    private bool patched;

    public void Initialize()
    {
        harmony = new Harmony("touhou.hudbars");
    }

    public void OnLoadCompleted()
    {
        try
        {
            HudBarsConfig.LoadAll();
            PatchIfNeeded();
            HudBarLog.Info($"已加载，进度条配置 {HudBarsConfig.Bars.Count} 条");
        }
        catch (Exception ex)
        {
            HudBarLog.Error("加载失败: " + ex);
        }
    }

    public void PreInitPatching()
    {
    }

    public void Dispose()
    {
        harmony?.UnpatchSelf();
        patched = false;
    }

    private void PatchIfNeeded()
    {
        if (patched)
        {
            return;
        }

        // reloadlua 后旧补丁会残留堆叠，先清掉自己名下的再打（仿 BondPlugin）
        harmony?.UnpatchSelf();
        // 别用无参 PatchAll()：所有插件编译在同一个程序集里，只按类型打自己的补丁
        harmony?.PatchAll(typeof(HudBarsDrawPatch));
        harmony?.PatchAll(typeof(HudBarsFramePatch));
        patched = true;
    }
}

// 诊断日志：打进游戏内控制台（F3）
internal static class HudBarLog
{
    internal static void Info(string msg)
    {
        try { DebugConsole.NewMessage("[进度条] " + msg, new Color(140, 230, 140)); } catch { }
    }

    internal static void Error(string msg)
    {
        try { DebugConsole.NewMessage("[进度条错误] " + msg, Color.OrangeRed); } catch { }
    }
}

// ==================== 配置 ====================

internal sealed class HudBarStateDef
{
    public string AfflictionId;
    public Identifier Affliction;
    public string Text;
    public Color Color;
}

internal sealed class HudBarExtraDef
{
    public string AfflictionId;
    public Identifier Affliction;
    public string Format; // {0} 替换为强度值
}

internal sealed class HudBarDef
{
    public string Id;
    public Identifier Affliction;
    public string Name;
    public Color Color = Color.White;
    public Color DefaultColor = Color.White;
    public float MaxStrength = 100f;
    public bool Enabled = true;
    public bool DefaultEnabled = true;
    public bool ShowWhenZero;
    // 显示条件：都不配置=总是按强度条件显示；配置了则需满足（同类多个为 OR，两类都要满足）
    public Identifier[] RequireAfflictions = System.Array.Empty<Identifier>();
    public Identifier[] RequireJobs = System.Array.Empty<Identifier>();
    public readonly List<HudBarStateDef> States = new List<HudBarStateDef>();
    public readonly List<HudBarExtraDef> Extras = new List<HudBarExtraDef>();
}

internal static class HudBarsConfig
{
    internal static readonly List<HudBarDef> Bars = new List<HudBarDef>();
    internal static float UserScale = 1f;
    internal const float DefaultScale = 1f;
    // 位置与尺寸（用户覆盖层可调）
    internal static bool LeftSide;                 // false=屏幕右侧  true=屏幕左侧
    internal static float OffsetX = 70f;           // 距所选边缘的距离（锚线）
    internal static float OffsetY;                 // 垂直偏移（正=向下）
    internal static float BarHeight = 180f;        // 条长度
    internal const bool DefaultLeftSide = false;
    internal const float DefaultOffsetX = 70f;
    internal const float DefaultOffsetY = 0f;
    internal const float DefaultBarHeight = 180f;

    // 跨模组约定：任何内容包（不只本模组，纯 XML 模组也行）在自己的包里放
    // Config/hud_bars.xml 就会被自动加载；重复 affliction id 先加载者生效（本模组优先）
    const string ConfigRelativePath = "Config/hud_bars.xml";
    static string userPath;     // 存档目录 TouhouHudBarsConfig.txt
    static DateTime userMtime;
    static readonly Dictionary<string, DateTime> fileMtimes = new Dictionary<string, DateTime>();
    static double nextReloadCheck;

    internal static string UserConfigPath
    {
        get
        {
            ResolveUserPath();
            return userPath;
        }
    }

    static void ResolveUserPath()
    {
        if (userPath != null) return;
        string baseDir = null;
        try { baseDir = SaveUtil.DefaultSaveFolder; } catch { }
        userPath = !string.IsNullOrEmpty(baseDir)
            ? Path.Combine(baseDir, "TouhouHudBarsConfig.txt")
            : Path.Combine("Data", "Saves", "TouhouHudBarsConfig.txt");
    }

    // Lua 设置页读取的合并状态快照（与 userPath 同目录）
    static string StatePath
    {
        get
        {
            ResolveUserPath();
            return Path.Combine(Path.GetDirectoryName(userPath) ?? ".", "TouhouHudBarsState.txt");
        }
    }

    internal static void LoadAll()
    {
        ResolveUserPath();
        LoadDefaults();
        LoadUserOverrides();
        WriteStateFile();
    }

    // 合并后的状态写给 Lua 设置页（含本地化后的名称与默认值）；内容没变不写，避免无意义的磁盘写入
    static void WriteStateFile()
    {
        try
        {
            var lines = new List<string>
            {
                "scale=" + UserScale.ToString("0.00"),
                "side=" + (LeftSide ? "left" : "right"),
                "offsetx=" + OffsetX.ToString("0"),
                "offsety=" + OffsetY.ToString("0"),
                "barheight=" + BarHeight.ToString("0"),
            };
            for (int i = 0; i < Bars.Count; i++)
            {
                var def = Bars[i];
                string prefix = $"bar.{i}.";
                lines.Add(prefix + "id=" + def.Id);
                lines.Add(prefix + "name=" + ResolveText(def.Name, def.Id));
                lines.Add(prefix + $"color={def.Color.R},{def.Color.G},{def.Color.B}");
                lines.Add(prefix + "enabled=" + (def.Enabled ? "1" : "0"));
                lines.Add(prefix + $"defaultcolor={def.DefaultColor.R},{def.DefaultColor.G},{def.DefaultColor.B}");
                lines.Add(prefix + "defaultenabled=" + (def.DefaultEnabled ? "1" : "0"));
            }
            string content = string.Join("\n", lines);
            string path = StatePath;
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            File.WriteAllText(path, content);
        }
        catch { }
    }

    // 用户覆盖层每帧查 mtime（Lua 设置页写入后需立即生效）；内容包配置 2 秒扫描一次
    internal static void PollReload()
    {
        try
        {
            ResolveUserPath();
            if (File.Exists(userPath) && File.GetLastWriteTime(userPath) != userMtime)
            {
                LoadAll();
                return;
            }

            double now = Timing.TotalTime;
            if (now < nextReloadCheck) return;
            nextReloadCheck = now + 2.0;
            var paths = EnumerateConfigPaths();
            bool dirty = paths.Count != fileMtimes.Count;
            if (!dirty)
            {
                foreach (var p in paths)
                {
                    if (!fileMtimes.TryGetValue(p, out var mtime) || File.GetLastWriteTime(p) != mtime)
                    {
                        dirty = true;
                        break;
                    }
                }
            }
            if (dirty) LoadAll();
        }
        catch { }
    }

    // 本模组优先，其余已启用内容包随后
    static List<string> EnumerateConfigPaths()
    {
        var paths = new List<string>();
        string ownDir = null;
        try
        {
            if (LuaCsSetup.Instance.PluginPackageManager.TryGetPackageForPlugin<HudBarsPlugin>(out var ownPkg))
            {
                ownDir = ownPkg.Dir;
            }
        }
        catch { }
        if (ownDir != null)
        {
            string ownPath = Path.Combine(ownDir, ConfigRelativePath);
            if (File.Exists(ownPath)) paths.Add(ownPath);
        }
        try
        {
            foreach (var pkg in ContentPackageManager.EnabledPackages.Regular)
            {
                if (pkg == null || pkg.Dir == null || pkg.Dir == ownDir) continue;
                string path = Path.Combine(pkg.Dir, ConfigRelativePath);
                if (File.Exists(path)) paths.Add(path);
            }
        }
        catch { }
        return paths;
    }

    static void LoadDefaults()
    {
        Bars.Clear();
        fileMtimes.Clear();
        foreach (var path in EnumerateConfigPaths())
        {
            LoadConfigFile(path);
        }
    }

    static void LoadConfigFile(string path)
    {
        try
        {
            fileMtimes[path] = File.GetLastWriteTime(path);
            var doc = XElement.Load(path);
            foreach (var barEl in doc.Elements("Bar"))
            {
                string id = barEl.GetAttributeString("affliction", "");
                if (string.IsNullOrEmpty(id)) continue;
                if (Bars.Exists(b => b.Id == id)) continue; // 重复 id：先加载者生效

                // name 可省略：默认用 affliction 自身的本地化名称（afflictionname.<id>）；
                // 也可以填任意文本标识符，绘制时经 ResolveText 走 TextManager 本地化
                string name = barEl.GetAttributeString("name", "");
                if (string.IsNullOrEmpty(name)) name = "afflictionname." + id;

                var def = new HudBarDef
                {
                    Id = id,
                    Affliction = id.ToIdentifier(),
                    Name = name,
                    MaxStrength = barEl.GetAttributeFloat("maxstrength", 100f),
                    Enabled = barEl.GetAttributeBool("enabled", true),
                    ShowWhenZero = barEl.GetAttributeBool("showwhenzero", false),
                };
                def.DefaultEnabled = def.Enabled;
                def.Color = def.DefaultColor = ParseColor(barEl.GetAttributeString("color", "255,255,255"), Color.White);
                def.RequireAfflictions = ParseIdList(barEl.GetAttributeString("requireaffliction", ""));
                def.RequireJobs = ParseIdList(barEl.GetAttributeString("requirejob", ""));

                foreach (var stateEl in barEl.Elements("State"))
                {
                    string sid = stateEl.GetAttributeString("affliction", "");
                    if (string.IsNullOrEmpty(sid)) continue;
                    // text 可省略：默认用该 affliction 的本地化名称
                    string stateText = stateEl.GetAttributeString("text", "");
                    if (string.IsNullOrEmpty(stateText)) stateText = "afflictionname." + sid;
                    def.States.Add(new HudBarStateDef
                    {
                        AfflictionId = sid,
                        Affliction = sid.ToIdentifier(),
                        Text = stateText,
                        Color = ParseColor(stateEl.GetAttributeString("color", ""), def.Color),
                    });
                }
                foreach (var extraEl in barEl.Elements("Extra"))
                {
                    string eid = extraEl.GetAttributeString("affliction", "");
                    if (string.IsNullOrEmpty(eid)) continue;
                    def.Extras.Add(new HudBarExtraDef
                    {
                        AfflictionId = eid,
                        Affliction = eid.ToIdentifier(),
                        Format = extraEl.GetAttributeString("format", "{0}"),
                    });
                }
                Bars.Add(def);
            }
        }
        catch { }
    }

    static void LoadUserOverrides()
    {
        try
        {
            UserScale = DefaultScale;
            LeftSide = DefaultLeftSide;
            OffsetX = DefaultOffsetX;
            OffsetY = DefaultOffsetY;
            BarHeight = DefaultBarHeight;
            foreach (var def in Bars)
            {
                def.Enabled = def.DefaultEnabled;
                def.Color = def.DefaultColor;
            }
            if (!File.Exists(userPath)) return;
            userMtime = File.GetLastWriteTime(userPath);
            foreach (var line in File.ReadAllLines(userPath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key == "scale")
                {
                    if (float.TryParse(value, out float s)) UserScale = MathHelper.Clamp(s, 0.5f, 2f);
                    continue;
                }
                if (key == "side")
                {
                    LeftSide = value == "left";
                    continue;
                }
                if (key == "offsetx")
                {
                    if (float.TryParse(value, out float ox)) OffsetX = MathHelper.Clamp(ox, 0f, 400f);
                    continue;
                }
                if (key == "offsety")
                {
                    if (float.TryParse(value, out float oy)) OffsetY = MathHelper.Clamp(oy, -400f, 400f);
                    continue;
                }
                if (key == "barheight")
                {
                    if (float.TryParse(value, out float bh)) BarHeight = MathHelper.Clamp(bh, 90f, 270f);
                    continue;
                }
                // bar.<id>.enabled / bar.<id>.color
                if (!key.StartsWith("bar.", StringComparison.Ordinal)) continue;
                int lastDot = key.LastIndexOf('.');
                if (lastDot <= 4) continue;
                string id = key.Substring(4, lastDot - 4);
                string prop = key.Substring(lastDot + 1);
                var target = Bars.Find(b => b.Id == id);
                if (target == null) continue;
                if (prop == "enabled")
                {
                    target.Enabled = value == "1";
                }
                else if (prop == "color")
                {
                    target.Color = ParseColor(value, target.Color);
                }
            }
        }
        catch { }
    }

    internal static void SaveUserOverrides()
    {
        try
        {
            ResolveUserPath();
            var lines = new List<string>
            {
                "scale=" + UserScale.ToString("0.00"),
                "side=" + (LeftSide ? "left" : "right"),
                "offsetx=" + OffsetX.ToString("0"),
                "offsety=" + OffsetY.ToString("0"),
                "barheight=" + BarHeight.ToString("0"),
            };
            foreach (var def in Bars)
            {
                lines.Add($"bar.{def.Id}.enabled={(def.Enabled ? "1" : "0")}");
                lines.Add($"bar.{def.Id}.color={def.Color.R},{def.Color.G},{def.Color.B}");
            }
            File.WriteAllLines(userPath, lines);
            userMtime = File.GetLastWriteTime(userPath);
        }
        catch { }
    }

    internal static Color ParseColor(string text, Color fallback)
    {
        if (string.IsNullOrEmpty(text)) return fallback;
        var parts = text.Split(',');
        if (parts.Length < 3) return fallback;
        if (!byte.TryParse(parts[0].Trim(), out byte r) ||
            !byte.TryParse(parts[1].Trim(), out byte g) ||
            !byte.TryParse(parts[2].Trim(), out byte b))
        {
            return fallback;
        }
        return new Color(r, g, b);
    }

    // 逗号分隔的 identifier 列表
    static Identifier[] ParseIdList(string text)
    {
        if (string.IsNullOrEmpty(text)) return System.Array.Empty<Identifier>();
        var parts = text.Split(',');
        var list = new List<Identifier>();
        foreach (var part in parts)
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0) list.Add(trimmed.ToIdentifier());
        }
        return list.ToArray();
    }

    // 文本本地化：含 "." 且 TextManager 里存在该标识符时按当前语言解析；
    // 否则返回 fallbackWhenUnresolved（缺省=原样返回 text）
    internal static string ResolveText(string text, string fallbackWhenUnresolved = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Contains('.') && TextManager.ContainsTag(text))
        {
            string localized = TextManager.Get(text).Value;
            if (!string.IsNullOrEmpty(localized)) return localized;
        }
        return fallbackWhenUnresolved ?? text;
    }
}

// ==================== 渲染 ====================

[HarmonyPatch(typeof(CharacterHUD), nameof(CharacterHUD.Draw))]
internal static class HudBarsDrawPatch
{
    private struct ActiveBar
    {
        public HudBarDef Def;
        public float Strength;
        public HudBarStateDef State;
    }

    private static readonly List<ActiveBar> activeBars = new List<ActiveBar>();

    private static void Postfix(SpriteBatch spriteBatch, Character character, Camera cam)
    {
        try
        {
            DrawBars(spriteBatch, character);
        }
        catch
        {
            // 绘制异常绝不能影响 HUD 主流程
        }
    }

    private static void DrawBars(SpriteBatch spriteBatch, Character character)
    {
        if (spriteBatch == null || character == null)
        {
            return;
        }

        if (GUI.DisableHUD)
        {
            return;
        }

        if (CharacterHealth.OpenHealthWindow != null || character.SelectedCharacter != null)
        {
            return;
        }

        if (Screen.Selected == GameMain.SubEditorScreen && GameMain.SubEditorScreen.WiringMode)
        {
            return;
        }

        if (character != Character.Controlled || character.IsDead || character.CharacterHealth == null || character.ShouldLockHud())
        {
            return;
        }

        CharacterHealth health = character.CharacterHealth;
        activeBars.Clear();
        foreach (var def in HudBarsConfig.Bars)
        {
            if (!def.Enabled) continue;

            // 显示条件：特定 affliction（强度>0 任一满足）/ 特定职业（任一满足）
            if (def.RequireAfflictions.Length > 0)
            {
                bool any = false;
                foreach (var req in def.RequireAfflictions)
                {
                    if (GetStrength(health, req) > 0f) { any = true; break; }
                }
                if (!any) continue;
            }
            if (def.RequireJobs.Length > 0)
            {
                Identifier jobId = character.Info?.Job?.Prefab.Identifier ?? Identifier.Empty;
                bool any = false;
                foreach (var req in def.RequireJobs)
                {
                    if (jobId == req) { any = true; break; }
                }
                if (!any) continue;
            }

            float strength = GetStrength(health, def.Affliction);
            HudBarStateDef state = null;
            foreach (var s in def.States)
            {
                if (GetStrength(health, s.Affliction) >= 1f)
                {
                    state = s;
                    break;
                }
            }
            if (strength <= 0.01f && state == null && !def.ShowWhenZero) continue;
            activeBars.Add(new ActiveBar { Def = def, Strength = strength, State = state });
        }
        if (activeBars.Count == 0) return;

        // 多条显示方案：竖条「编组居中」——所有条围绕一条锚线对称展开，每条占 96px 槽位，
        // 条在槽内居中，标题/附加行对条居中，配置顺序从左到右。
        // 锚线位置由玩家配置：左侧或右侧 + 距边缘偏移（offsetx），整体可垂直偏移（offsety），条长可调。
        float scale = GUI.Scale * HudBarsConfig.UserScale;
        int barWidth = Math.Max(8, (int)(14 * scale));
        int barHeight = (int)(HudBarsConfig.BarHeight * scale);
        int slot = (int)(96 * scale);
        int totalWidth = slot * activeBars.Count;
        int anchorX = HudBarsConfig.LeftSide
            ? (int)(HudBarsConfig.OffsetX * scale)
            : GameMain.GraphicsWidth - (int)(HudBarsConfig.OffsetX * scale);
        int startX = Math.Clamp(anchorX - totalWidth / 2, 0, Math.Max(0, GameMain.GraphicsWidth - totalWidth));
        int baseY = Math.Clamp(
            GameMain.GraphicsHeight / 2 - barHeight / 2 + (int)(HudBarsConfig.OffsetY * scale),
            (int)(30 * scale), Math.Max((int)(30 * scale), GameMain.GraphicsHeight - barHeight - (int)(30 * scale)));

        for (int i = 0; i < activeBars.Count; i++)
        {
            var bar = activeBars[i];
            Color color = bar.State != null ? bar.State.Color : bar.Def.Color;
            string title = bar.State != null
                ? HudBarsConfig.ResolveText(bar.State.Text, bar.State.AfflictionId) + " " + HudBarsConfig.ResolveText(bar.Def.Name, bar.Def.Id)
                : HudBarsConfig.ResolveText(bar.Def.Name, bar.Def.Id);

            int barX = startX + i * slot + (slot - barWidth) / 2;
            // 背景 + 自下而上填充 + 边框
            GUI.DrawRectangle(spriteBatch, new Rectangle(barX, baseY, barWidth, barHeight), Color.Black * 0.6f, isFilled: true);
            int fillHeight = (int)(barHeight * MathHelper.Clamp(bar.Strength / bar.Def.MaxStrength, 0f, 1f));
            if (fillHeight > 0)
            {
                GUI.DrawRectangle(spriteBatch, new Rectangle(barX, baseY + barHeight - fillHeight, barWidth, fillHeight), color, isFilled: true);
            }
            GUI.DrawRectangle(spriteBatch, new Rectangle(barX, baseY, barWidth, barHeight), color * 0.8f, isFilled: false, thickness: 1f);

            // 标题在条上方居中：「蓄势 气 80%」
            string text = $"{title} {bar.Strength:0}%";
            Vector2 textSize = GUIStyle.Font.MeasureString(text);
            GUI.DrawString(spriteBatch,
                new Vector2(barX + barWidth / 2f - textSize.X / 2f, baseY - textSize.Y - 4 * scale),
                text, color, Color.Black * 0.6f, (int)(2 * scale));

            // 附加行（如切换冷却）在条下方居中
            float extraY = baseY + barHeight + 2 * scale;
            foreach (var extra in bar.Def.Extras)
            {
                float value = GetStrength(health, extra.Affliction);
                if (value <= 0.05f) continue;
                string extraText = HudBarsConfig.ResolveText(extra.Format).Replace("{0}", value.ToString("0"));
                Vector2 extraSize = GUIStyle.Font.MeasureString(extraText);
                GUI.DrawString(spriteBatch,
                    new Vector2(barX + barWidth / 2f - extraSize.X / 2f, extraY),
                    extraText, color, Color.Black * 0.6f, (int)(2 * scale));
                extraY += extraSize.Y;
            }
        }
    }

    private static float GetStrength(CharacterHealth health, Identifier affliction)
    {
        Affliction aff = health.GetAffliction(affliction);
        return aff?.Strength ?? 0f;
    }
}


// ==================== 每帧驱动（配置热更新） ====================
// 设置界面在 Lua 侧（Touhou_Mod_Hotkey.lua 的 hudbars 页面，与快捷键/装束锁定同框架）；
// Lua 写入用户覆盖层 TouhouHudBarsConfig.txt，本侧每帧查 mtime 即时重载；
// 合并后的状态写 TouhouHudBarsState.txt 供 Lua 页面读取（只读快照，含本地化后的名称）。

[HarmonyPatch]
internal static class HudBarsFramePatch
{
    static System.Reflection.MethodBase TargetMethod()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance;
        return typeof(GameMain).GetMethod("Update", flags);
    }

    static void Postfix()
    {
        try
        {
            HudBarsConfig.PollReload();
        }
        catch { }
    }
}
