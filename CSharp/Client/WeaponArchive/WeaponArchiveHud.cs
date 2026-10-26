using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponArchive
{
    public sealed class WeaponArchiveHudPlugin : IAssemblyPlugin
    {
        private readonly Harmony harmony = new Harmony("touhou.weaponarchive.hud");
        public void Initialize() { }
        public void PreInitPatching() { }
        public void OnLoadCompleted()
        {
            ArchiveStationHudPatch.ClearCache();
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(ArchiveStationHudPatch));
        }
        public void Dispose()
        {
            harmony.UnpatchSelf();
            ArchiveStationHudPatch.ClearCache();
        }
    }

    [HarmonyPatch(typeof(CharacterHUD), nameof(CharacterHUD.Draw))]
    internal static class ArchiveStationHudPatch
    {
        private readonly struct DrawCommand
        {
            private readonly Rectangle rect;
            private readonly Vector2 position;
            private readonly string text;
            private readonly Color color;
            private readonly bool filled;
            internal DrawCommand(Rectangle rect, Color color, bool filled)
            { this.rect = rect; this.color = color; this.filled = filled; position = default; text = null; }
            internal DrawCommand(string text, Vector2 position, Color color)
            { this.text = text; this.position = position; this.color = color; rect = default; filled = false; }
            internal void Draw(SpriteBatch spriteBatch)
            {
                if (text != null) GUI.DrawString(spriteBatch, position, text, color, Color.Transparent, 0);
                else GUI.DrawRectangle(spriteBatch, rect, color, filled);
            }
        }

        private sealed class CachedLayout
        {
            private readonly TouhouArchiveStation station;
            private readonly List<ArchiveRow> rows;
            private readonly ArchiveStationStatus status;
            private readonly int width = GameMain.GraphicsWidth, height = GameMain.GraphicsHeight;
            private readonly float scale = GUI.Scale;
            private readonly int languageVersion = TextManager.LanguageVersion;
            private readonly LanguageIdentifier language = GameSettings.CurrentConfig.Language;
            private readonly GUIFont font = GUIStyle.Font;
            private readonly uint fontSize = GUIStyle.Font.Size;
            internal readonly List<DrawCommand> Commands = new List<DrawCommand>();

            internal CachedLayout(TouhouArchiveStation station)
            { this.station = station; rows = station.Rows; status = station.Status; }
            internal bool Matches(TouhouArchiveStation current)
                => station == current && rows == current.Rows && status == current.Status &&
                    width == GameMain.GraphicsWidth && height == GameMain.GraphicsHeight && scale == GUI.Scale &&
                    languageVersion == TextManager.LanguageVersion && language.Equals(GameSettings.CurrentConfig.Language) &&
                    font == GUIStyle.Font && fontSize == GUIStyle.Font.Size;
        }

        private static CachedLayout cachedLayout;
        internal static void ClearCache() => cachedLayout = null;

        private static string Fit(string value, float width)
        {
            if (GUIStyle.Font.MeasureString(value).X <= width) return value;
            while (value.Length > 0 && GUIStyle.Font.MeasureString(value + "…").X > width)
                value = value.Substring(0, value.Length - 1);
            return value + "…";
        }

        private static string SpeciesLabel(string species, float width)
        {
            string translated = TextManager.Get("Character." + species).Value;
            if (string.IsNullOrWhiteSpace(translated)) return Fit(species, width);
            string suffix = "（" + species + "）";
            float nameWidth = width - GUIStyle.Font.MeasureString(suffix).X;
            // 名字太长时先缩名字，尽量保住用来核对物种的 id。
            return nameWidth >= GUIStyle.Font.MeasureString("…").X
                ? Fit(translated, nameWidth) + suffix : Fit(translated + suffix, width);
        }

        private static List<string> Wrap(string value, float width)
        {
            var lines = new List<string>();
            string rest = value;
            while (rest.Length > 0 && lines.Count < 2)
            {
                if (GUIStyle.Font.MeasureString(rest).X <= width) { lines.Add(rest); break; }
                if (lines.Count == 1) { lines.Add(Fit(rest, width)); break; }
                int length = 1;
                while (length < rest.Length && GUIStyle.Font.MeasureString(rest.Substring(0, length + 1)).X <= width)
                    length++;
                int space = rest.LastIndexOf(' ', length - 1, length);
                if (space > length / 2) length = space;
                lines.Add(rest.Substring(0, length).TrimEnd());
                rest = rest.Substring(length).TrimStart();
            }
            return lines;
        }

        private static void Postfix(SpriteBatch spriteBatch, Character character)
        {
            if (spriteBatch == null || GUI.DisableHUD || character == null || character != Character.Controlled ||
                character.IsDead || character.IsUnconscious || character.ShouldLockHud() ||
                CharacterHealth.OpenHealthWindow != null || character.SelectedCharacter != null)
            { ClearCache(); return; }
            var station = character.SelectedItem?.GetComponent<TouhouArchiveStation>();
            if (station == null) { ClearCache(); return; }
            // 服务端每次刷新都会换一份 Rows，状态、语言和尺寸也各自参与失效判断。
            if (cachedLayout == null || !cachedLayout.Matches(station)) cachedLayout = BuildLayout(station);
            foreach (var command in cachedLayout.Commands) command.Draw(spriteBatch);
        }

        private static CachedLayout BuildLayout(TouhouArchiveStation station)
        {
            var layout = new CachedLayout(station);
            float scale = GUI.Scale;
            int width = Math.Min((int)(540 * scale), (int)(GameMain.GraphicsWidth * 0.43f));
            int padding = Math.Max(10, (int)(18 * scale));
            int innerWidth = width - padding * 2;
            int fontHeight = (int)Math.Ceiling(GUIStyle.Font.MeasureString("ABC国").Y);
            int rowHeight = fontHeight + (int)(12 * scale);
            int titleHeight = fontHeight + padding * 2;
            var statusLines = Wrap(TextManager.Get("touhou.archive.status." +
                station.Status.ToString().ToLowerInvariant()).Value, innerWidth);
            int statusHeight = statusLines.Count * (fontHeight + (int)(4 * scale)) + padding * 2;
            int fixedHeight = titleHeight + statusHeight + rowHeight + padding;
            int capacity = Math.Max(1, Math.Min(12,
                ((int)(GameMain.GraphicsHeight * 0.7f) - fixedHeight) / rowHeight));
            bool overflow = station.Rows.Count > capacity;
            int shown = Math.Min(station.Rows.Count, overflow ? Math.Max(0, capacity - 1) : capacity);
            int bodyRows = Math.Max(1, shown + (overflow ? 1 : 0));
            int height = fixedHeight + bodyRows * rowHeight;
            int x = GameMain.GraphicsWidth / 2 + (int)(16 * scale);
            int y = Math.Max(padding, (GameMain.GraphicsHeight - height) / 2);
            int left = x + padding, right = x + width - padding;
            bool single = station.Status == ArchiveStationStatus.SingleA || station.Status == ArchiveStationStatus.SingleB;
            bool merge = station.Status == ArchiveStationStatus.Preview || station.Status == ArchiveStationStatus.Synced;
            Color ink = new Color(223, 231, 233), muted = new Color(153, 170, 176);
            Color accent = new Color(128, 190, 191), border = new Color(61, 79, 86);
            Color stateColor = station.Status == ArchiveStationStatus.InvalidData ||
                station.Status == ArchiveStationStatus.DifferentModel ? new Color(220, 180, 134) : muted;
            if (station.Status == ArchiveStationStatus.Synced) stateColor = accent;
            void Fill(Rectangle rect, Color color) => layout.Commands.Add(new DrawCommand(rect, color, true));
            void Text(string value, float textX, float textY, Color color)
                => layout.Commands.Add(new DrawCommand(value, new Vector2(textX, textY), color));
            void Number(string value, float edge, float textY, Color color)
                => Text(value, edge - GUIStyle.Font.MeasureString(value).X, textY, color);

            Fill(new Rectangle(x, y, width, height), new Color(18, 26, 32) * 0.96f);
            layout.Commands.Add(new DrawCommand(new Rectangle(x, y, width, height), border, false));
            Fill(new Rectangle(x, y, width, titleHeight), new Color(28, 39, 45));
            Fill(new Rectangle(left, y + padding, Math.Max(3, (int)(3 * scale)), fontHeight), accent);
            Text(Fit(TextManager.Get("touhou.archive.title").Value, innerWidth - padding),
                left + padding, y + padding, ink);
            int lineY = y + titleHeight + padding;
            foreach (string text in statusLines)
            {
                Text(text, left, lineY, stateColor);
                lineY += fontHeight + (int)(4 * scale);
            }
            int tableY = y + titleHeight + statusHeight;
            int firstEdge = left + (int)(innerWidth * 0.61f);
            int secondEdge = left + (int)(innerWidth * 0.78f);
            int combinedStart = left + (int)(innerWidth * 0.82f);
            if (merge)
                Fill(new Rectangle(combinedStart, tableY, right - combinedStart, rowHeight * (shown + 1)),
                    new Color(33, 51, 56));
            Fill(new Rectangle(left, tableY, innerWidth, 1), border);
            int textInset = (rowHeight - fontHeight) / 2;
            Text(TextManager.Get("touhou.archive.species").Value, left, tableY + textInset, muted);
            if (single)
                Number(TextManager.Get("touhou.archive.defeats").Value, right, tableY + textInset, muted);
            else
            {
                Number("A", firstEdge, tableY + textInset, muted);
                Number("B", secondEdge, tableY + textInset, muted);
                Number(TextManager.Get("touhou.archive.combined").Value, right, tableY + textInset, accent);
            }
            lineY = tableY + rowHeight;
            foreach (var row in station.Rows.Take(shown))
            {
                Fill(new Rectangle(left, lineY, innerWidth, 1), border * 0.5f);
                Text(SpeciesLabel(row.Species, innerWidth * (single ? 0.72f : 0.43f)), left, lineY + textInset, ink);
                if (!single)
                {
                    Number(row.First.ToString(), firstEdge, lineY + textInset, ink);
                    Number(row.Second.ToString(), secondEdge, lineY + textInset, ink);
                }
                Number(row.Combined.ToString(), right, lineY + textInset, merge ? accent : ink);
                lineY += rowHeight;
            }
            if (overflow)
                Text(Fit(TextManager.Get("touhou.archive.more").Value.Replace("[count]",
                    (station.Rows.Count - shown).ToString()), innerWidth), left, lineY + textInset, muted);
            else if (station.Rows.Count == 0)
            {
                string empty = TextManager.Get("touhou.archive.empty").Value;
                Text(empty, left + (innerWidth - GUIStyle.Font.MeasureString(empty).X) / 2,
                    lineY + textInset, muted);
            }
            return layout;
        }
    }
}
