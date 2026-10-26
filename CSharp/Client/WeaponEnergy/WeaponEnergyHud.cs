// 武器能量条（客户端）：绘制、每把武器的文字/配色，以及本机外观设置
// （位置/方向/尺寸/颜色/强制覆盖，设置页在 K 菜单「能量条设置」）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponEnergy
{
    public sealed class WeaponEnergyHudPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize() => harmony = new Harmony("touhou.weaponenergy.hud");
        public void PreInitPatching() { }
        public void OnLoadCompleted()
        {
            try { WeaponEnergyHudConfig.LoadAll(); } catch { }
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(WeaponEnergyHudPatch));
            harmony.PatchAll(typeof(WeaponEnergyConfigPatch));
        }
        public void Dispose() => harmony?.UnpatchSelf();
    }

    // 能量条外观（客户端本地设置）：设置页在 Lua 侧（Touhou_Mod_Hotkey.lua 的 energybar 页）。
    // Lua 写 TouhouWeaponEnergyConfig.txt，本侧每帧查 mtime 即时重载；合并后的值写
    // TouhouWeaponEnergyState.txt 供设置页显示（跟进度条一个桥法，两边内容一致时互不触发）。
    internal static class WeaponEnergyHudConfig
    {
        internal const bool DefaultShow = true;
        internal const bool DefaultFollowWeapon = true;
        internal const float DefaultAnchorX = 0.5f;
        internal const float DefaultAnchorY = 0.86f;
        internal const bool DefaultVertical = false;
        internal const float DefaultLength = 220f;
        internal const float DefaultThickness = 14f;
        internal static readonly Color DefaultActiveColor = new Color(100, 220, 255);
        internal static readonly Color DefaultIdleColor = new Color(150, 170, 190);

        internal static bool Show = DefaultShow;
        internal static bool FollowWeapon = DefaultFollowWeapon;   // true=跟在武器按钮上方（原行为） false=按屏幕百分比定位
        internal static float AnchorX = DefaultAnchorX;            // 自定义位置：条中心占屏幕宽/高的比例
        internal static float AnchorY = DefaultAnchorY;
        internal static bool Vertical = DefaultVertical;
        internal static float Length = DefaultLength;              // 条长度（未按 UI 缩放）
        internal static float Thickness = DefaultThickness;
        internal static Color ActiveColor = DefaultActiveColor;
        internal static Color IdleColor = DefaultIdleColor;
        // 设置页“强制覆盖武器配色”：默认关。关时 XML 里写了颜色就用武器自己的，否则用上面这组默认。
        internal static bool ForceColors;
        // 数值是否显示小数：默认关（整数显示，如 45/100）
        internal static bool ShowDecimals;

        static string userPath;
        static DateTime userMtime = DateTime.MinValue;

        static void ResolvePaths()
        {
            if (userPath != null) return;
            string baseDir = null;
            try { baseDir = SaveUtil.DefaultSaveFolder; } catch { }
            userPath = !string.IsNullOrEmpty(baseDir)
                ? Path.Combine(baseDir, "TouhouWeaponEnergyConfig.txt")
                : Path.Combine("Data", "Saves", "TouhouWeaponEnergyConfig.txt");
        }

        static string StatePath
        {
            get
            {
                ResolvePaths();
                return Path.Combine(Path.GetDirectoryName(userPath) ?? ".", "TouhouWeaponEnergyState.txt");
            }
        }

        internal static void LoadAll()
        {
            ResolvePaths();
            LoadUserOverrides();
            userMtime = File.Exists(userPath) ? File.GetLastWriteTime(userPath) : DateTime.MinValue;
            WriteStateFile();
        }

        internal static void PollReload()
        {
            try
            {
                ResolvePaths();
                if (!File.Exists(userPath))
                {
                    if (userMtime == DateTime.MinValue) return;  // 从没写过配置，保持默认
                    LoadAll();
                    return;
                }
                if (File.GetLastWriteTime(userPath) != userMtime) LoadAll();
            }
            catch { }
        }

        static void LoadUserOverrides()
        {
            Show = DefaultShow;
            FollowWeapon = DefaultFollowWeapon;
            AnchorX = DefaultAnchorX;
            AnchorY = DefaultAnchorY;
            Vertical = DefaultVertical;
            Length = DefaultLength;
            Thickness = DefaultThickness;
            ActiveColor = DefaultActiveColor;
            IdleColor = DefaultIdleColor;
            ForceColors = false;
            ShowDecimals = false;
            if (!File.Exists(userPath)) return;
            try
            {
                foreach (var line in File.ReadAllLines(userPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "show": Show = value == "1"; break;
                        case "follow": FollowWeapon = value == "1"; break;
                        case "vertical": Vertical = value == "1"; break;
                        case "forcecolors": ForceColors = value == "1"; break;
                        case "decimals": ShowDecimals = value == "1"; break;
                        case "anchorx":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ax))
                                AnchorX = MathHelper.Clamp(ax, 0f, 1f);
                            break;
                        case "anchory":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ay))
                                AnchorY = MathHelper.Clamp(ay, 0f, 1f);
                            break;
                        case "length":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float len))
                                Length = MathHelper.Clamp(len, 60f, 600f);
                            break;
                        case "thickness":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float th))
                                Thickness = MathHelper.Clamp(th, 4f, 40f);
                            break;
                        case "activecolor": ActiveColor = ParseColor(value, ActiveColor); break;
                        case "idlecolor": IdleColor = ParseColor(value, IdleColor); break;
                    }
                }
            }
            catch { }
        }

        static void WriteStateFile()
        {
            try
            {
                var lines = new List<string>
                {
                    "show=" + (Show ? "1" : "0"),
                    "follow=" + (FollowWeapon ? "1" : "0"),
                    "anchorx=" + AnchorX.ToString("0.00", CultureInfo.InvariantCulture),
                    "anchory=" + AnchorY.ToString("0.00", CultureInfo.InvariantCulture),
                    "vertical=" + (Vertical ? "1" : "0"),
                    "length=" + Length.ToString("0", CultureInfo.InvariantCulture),
                    "thickness=" + Thickness.ToString("0", CultureInfo.InvariantCulture),
                    "activecolor=" + $"{ActiveColor.R},{ActiveColor.G},{ActiveColor.B}",
                    "idlecolor=" + $"{IdleColor.R},{IdleColor.G},{IdleColor.B}",
                    "forcecolors=" + (ForceColors ? "1" : "0"),
                    "decimals=" + (ShowDecimals ? "1" : "0"),
                };
                string content = string.Join("\n", lines);
                string path = StatePath;
                if (File.Exists(path) && File.ReadAllText(path) == content) return;
                File.WriteAllText(path, content);
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
    }

    [HarmonyPatch(typeof(CharacterHUD), nameof(CharacterHUD.Draw))]
    internal static class WeaponEnergyHudPatch
    {
        private static void Postfix(SpriteBatch spriteBatch, Character character)
        {
            if (spriteBatch == null || GUI.DisableHUD || character == null || character != Character.Controlled ||
                character.IsDead || character.IsUnconscious || character.ShouldLockHud() ||
                CharacterHealth.OpenHealthWindow != null || character.SelectedCharacter != null) return;
            var control = character.HeldItems.Select(item => item.GetComponent<TouhouWeaponEnergy>())
                .FirstOrDefault(component => component != null);
            if (control == null || !control.Settings.ShowEnergyBar || !WeaponEnergyHudConfig.Show) return;
            DrawBar(spriteBatch, control);
        }

        private static void DrawBar(SpriteBatch spriteBatch, TouhouWeaponEnergy control)
        {
            float scale = GUI.Scale;
            bool vertical = WeaponEnergyHudConfig.Vertical;
            int length = Math.Max(24, (int)(WeaponEnergyHudConfig.Length * scale));
            int thickness = Math.Max(4, (int)(WeaponEnergyHudConfig.Thickness * scale));
            int width = vertical ? thickness : length;
            int height = vertical ? length : thickness;

            int x, y;
            if (WeaponEnergyHudConfig.FollowWeapon)
            {
                // 原行为：横条贴在武器按钮正上方；竖条底边留在按钮上方 12px
                var frame = control.Item.GetComponent<CustomInterface>()?.GuiFrame;
                int frameX = frame?.Rect.X ?? (int)(20f * scale);
                int frameY = frame?.Rect.Y ?? (GameMain.GraphicsHeight - (int)(180f * scale));
                x = frameX;
                y = vertical ? frameY - (int)(12f * scale) - height : frameY - (int)(48f * scale);
            }
            else
            {
                x = (int)(WeaponEnergyHudConfig.AnchorX * GameMain.GraphicsWidth) - width / 2;
                y = (int)(WeaponEnergyHudConfig.AnchorY * GameMain.GraphicsHeight) - height / 2;
            }

            int margin = (int)(30f * scale);
            x = Math.Clamp(x, 0, Math.Max(0, GameMain.GraphicsWidth - width));
            y = Math.Clamp(y, margin, Math.Max(margin, GameMain.GraphicsHeight - height - margin));

            var settings = control.Settings;
            Color idleColor = WeaponEnergyHudConfig.IdleColor;
            Color activeColor = WeaponEnergyHudConfig.ActiveColor;
            // 设置页“强制覆盖”关着时，武器 XML 里写了颜色就用武器自己的。
            if (!WeaponEnergyHudConfig.ForceColors)
            {
                if (!string.IsNullOrEmpty(settings.BarColorIdle))
                    idleColor = WeaponEnergyHudConfig.ParseColor(settings.BarColorIdle, idleColor);
                if (!string.IsNullOrEmpty(settings.BarColorActive))
                    activeColor = WeaponEnergyHudConfig.ParseColor(settings.BarColorActive, activeColor);
            }
            // “就绪”只对“满能量才能开”的武器有意义（改装类）；缄默这种低门槛武器仍显示待机。
            bool fullBarGate = settings.MinimumActivationEnergy >= settings.MaxEnergy - 0.01f;
            bool ready = fullBarGate && control.Energy >= settings.MaxEnergy - 0.01f;
            Color color = control.ControlActive ? activeColor : idleColor;
            string key = !control.SnapshotReady ? settings.BarSyncing :
                control.ControlActive ? settings.BarActive :
                ready ? settings.BarReady : settings.BarStandby;
            string title = ResolveText(key, key);
            if (!string.IsNullOrEmpty(settings.BarTitle))
                title = ResolveText(settings.BarTitle, settings.BarTitle) + "：" + title;
            if (control.SnapshotReady)
                title += $"  {FormatValue(control.Energy)}/{FormatValue(control.MaxEnergy)}";

            GUI.DrawRectangle(spriteBatch, new Rectangle(x, y, width, height), Color.Black * 0.7f, true);
            if (control.SnapshotReady)
            {
                float ratio = MathHelper.Clamp(control.Energy / Math.Max(0.01f, control.MaxEnergy), 0f, 1f);
                if (vertical)
                {
                    int fill = (int)(height * ratio);
                    if (fill > 0)
                        GUI.DrawRectangle(spriteBatch, new Rectangle(x, y + height - fill, width, fill), color, true);
                }
                else
                {
                    int fill = (int)(width * ratio);
                    if (fill > 0)
                        GUI.DrawRectangle(spriteBatch, new Rectangle(x, y, fill, height), color, true);
                }
            }
            // 满能量（可启动）时描边用激活色，一眼能看出“可以按了”。
            GUI.DrawRectangle(spriteBatch, new Rectangle(x, y, width, height), ready ? activeColor : color, false);
            Vector2 titleSize = GUIStyle.Font.MeasureString(title);
            GUI.DrawString(spriteBatch, new Vector2(x + width / 2f - titleSize.X / 2f, y - titleSize.Y - 3f * scale),
                title, color, Color.Black * 0.8f, (int)(2f * scale));
        }

        // 数值格式：默认整数；设置页打开“显示小数”后才保留两位。
        private static string FormatValue(float value)
            => value.ToString(WeaponEnergyHudConfig.ShowDecimals ? "0.##" : "0",
                CultureInfo.InvariantCulture);

        // 文本 id 走游戏本地化；解析不到就当字面文本用（方便现场调试）。
        private static string ResolveText(string id, string fallback)
        {
            if (string.IsNullOrEmpty(id)) return fallback;
            if (id.Contains('.') && TextManager.ContainsTag(id))
            {
                string localized = TextManager.Get(id).Value;
                if (!string.IsNullOrEmpty(localized)) return localized;
            }
            return fallback;
        }
    }

    [HarmonyPatch]
    internal static class WeaponEnergyConfigPatch
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
                WeaponEnergyHudConfig.PollReload();
            }
            catch { }
        }
    }
}
