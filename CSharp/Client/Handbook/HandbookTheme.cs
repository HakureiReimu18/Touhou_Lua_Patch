using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.Handbook
{
    internal static class HandbookTheme
    {
        internal static readonly Color Background = new(14, 18, 29), Rail = new(19, 24, 38);
        internal static readonly Color Card = new(27, 33, 50), CardHover = new(38, 43, 64);
        internal static readonly Color Note = new(38, 30, 53), Border = new(61, 69, 93);
        internal static readonly Color Ink = new(233, 234, 244), Muted = new(158, 172, 195);
        internal static readonly Color Violet = new(186, 153, 235), Cyan = new(126, 207, 213);

        internal static RectTransform At(RectTransform parent, int x, int y, int width, int height)
            => new RectTransform(new Point(Math.Max(1, width), Math.Max(1, height)), parent, isFixedSize: true)
                { AbsoluteOffset = new Point(x, y) };

        internal static GUIFrame Panel(RectTransform rect, Color fill, Color accent, string tag = "")
        {
            var frame = new GUIFrame(rect, style: null) { Color = Color.Transparent, UserData = tag };
            new GUICustomComponent(new RectTransform(Vector2.One, rect), (sb, c) =>
            {
                CutPanel(sb, c.Rect, fill, Border, GUI.IntScale(8));
                GUI.DrawRectangle(sb, new Rectangle(c.Rect.X, c.Rect.Y + GUI.IntScale(10),
                    Math.Max(2, GUI.IntScale(3)), Math.Max(1, c.Rect.Height - GUI.IntScale(20))), accent, true);
            }) { CanBeFocused = false };
            return frame;
        }

        internal static GUITextBlock Label(RectTransform parent, int x, int y, int width,
            string text, GUIFont font = null, Color? ink = null)
        {
            var label = new GUITextBlock(At(parent, x, y, width, 0), text,
                font: font ?? GUIStyle.Font, textColor: ink ?? Ink,
                textAlignment: Alignment.TopLeft, wrap: true, style: null)
            { CanBeFocused = false };
            label.CalculateHeightFromText();
            return label;
        }

        internal static GUIButton Button(RectTransform rect, string label, Func<bool> click, string number = "")
        {
            var shell = new GUIFrame(rect, style: null) { Color = Color.Transparent };
            GUIButton button = null;
            new GUICustomComponent(new RectTransform(Vector2.One, rect), (sb, c) =>
            {
                if (button == null) return;
                bool hot = button.Enabled && GUI.IsMouseOn(button);
                bool active = button.Selected;
                Color ink = button.Enabled ? (active ? Cyan : Violet) : Border;
                CutPanel(sb, c.Rect, active ? new Color(35, 46, 65) : hot ? CardHover : Card,
                    active || hot ? ink : Border, GUI.IntScale(7));
                if (active)
                    GUI.DrawRectangle(sb, new Rectangle(c.Rect.X, c.Rect.Y + GUI.IntScale(8),
                        Math.Max(2, GUI.IntScale(3)), Math.Max(1, c.Rect.Height - GUI.IntScale(16))), Cyan, true);
                if (number.Length > 0)
                    GUI.DrawString(sb, new Vector2(c.Rect.X + GUI.IntScale(11),
                        c.Rect.Y + (c.Rect.Height - GUIStyle.SmallFont.MeasureString(number).Y) / 2f),
                        number, ink, font: GUIStyle.SmallFont);
            }) { CanBeFocused = false };
            button = new GUIButton(new RectTransform(Vector2.One, shell.RectTransform), label, style: null)
            { OnClicked = (b, data) => click() };
            button.Frame.Visible = false;
            button.TextBlock.OverrideTextColor(Ink);
            button.TextBlock.DisabledTextColor = Muted * 0.55f;
            button.TextBlock.Wrap = true;
            button.TextBlock.TextAlignment = number.Length > 0 ? Alignment.CenterLeft : Alignment.Center;
            int inset = number.Length > 0 ? GUI.IntScale(42) : GUI.IntScale(10);
            button.TextBlock.RectTransform.RelativeSize = new Vector2(1, 1);
            button.TextBlock.RectTransform.Resize(new Point(Math.Max(1, rect.Rect.Width - inset - GUI.IntScale(10)),
                rect.Rect.Height));
            button.TextBlock.RectTransform.AbsoluteOffset = new Point(number.Length > 0 ? GUI.IntScale(16) : 0, 0);
            return button;
        }

        internal static void StyleScroll(GUIListBox list)
        {
            var bar = list.ScrollBar;
            bar.Color = bar.HoverColor = bar.SelectedColor = bar.DisabledColor = bar.PressedColor = Color.Transparent;
            bar.Frame.Visible = false;
            bar.Bar.Frame.Visible = false;
            bar.Bar.TextBlock.Visible = false;
            new GUICustomComponent(new RectTransform(Vector2.One, bar.RectTransform), (sb, c) =>
            {
                int center = c.Rect.X + c.Rect.Width / 2;
                GUI.DrawRectangle(sb, new Rectangle(center, c.Rect.Y, Math.Max(1, GUI.IntScale(2)),
                    c.Rect.Height), Border, true);
                var handle = bar.Bar.Rect;
                GUI.DrawRectangle(sb, new Rectangle(center - GUI.IntScale(1), handle.Y,
                    Math.Max(3, GUI.IntScale(4)), handle.Height), GUI.IsMouseOn(bar) ? Violet : Cyan * 0.7f, true);
            }) { CanBeFocused = false };
        }

        internal static void CutPanel(SpriteBatch sb, Rectangle r, Color fill, Color line, int cut)
        {
            int c = Math.Max(0, Math.Min(cut, Math.Min(r.Width, r.Height) / 3));
            GUI.DrawRectangle(sb, new Rectangle(r.X + c, r.Y, Math.Max(1, r.Width - c * 2), r.Height), fill, true);
            GUI.DrawRectangle(sb, new Rectangle(r.X, r.Y + c, c, Math.Max(0, r.Height - c)), fill, true);
            GUI.DrawRectangle(sb, new Rectangle(r.Right - c, r.Y, c, Math.Max(0, r.Height - c)), fill, true);
            for (int i = 0; i < c; i++)
            {
                GUI.DrawRectangle(sb, new Rectangle(r.X + c - i, r.Y + i, i, 1), fill, true);
                GUI.DrawRectangle(sb, new Rectangle(r.Right - c, r.Bottom - c + i, c - i, 1), fill, true);
            }
            GUI.DrawLine(sb, new Vector2(r.X + c, r.Y), new Vector2(r.Right, r.Y), line);
            GUI.DrawLine(sb, new Vector2(r.Right, r.Y), new Vector2(r.Right, r.Bottom - c), line);
            GUI.DrawLine(sb, new Vector2(r.Right, r.Bottom - c), new Vector2(r.Right - c, r.Bottom), line);
            GUI.DrawLine(sb, new Vector2(r.Right - c, r.Bottom), new Vector2(r.X, r.Bottom), line);
            GUI.DrawLine(sb, new Vector2(r.X, r.Bottom), new Vector2(r.X, r.Y + c), line);
            GUI.DrawLine(sb, new Vector2(r.X, r.Y + c), new Vector2(r.X + c, r.Y), line);
        }

        internal static void Seal(SpriteBatch sb, Vector2 center, float size, Color ink)
        {
            Vector2 top = center + new Vector2(0, -size), right = center + new Vector2(size, 0);
            Vector2 bottom = center + new Vector2(0, size), left = center + new Vector2(-size, 0);
            GUI.DrawLine(sb, top, right, ink); GUI.DrawLine(sb, right, bottom, ink);
            GUI.DrawLine(sb, bottom, left, ink); GUI.DrawLine(sb, left, top, ink);
            GUI.DrawLine(sb, top, bottom, ink * 0.45f); GUI.DrawLine(sb, left, right, ink * 0.45f);
            GUI.DrawRectangle(sb, new Rectangle((int)(center.X - size * 0.45f), (int)(center.Y - size * 0.45f),
                (int)(size * 0.9f), (int)(size * 0.9f)), ink * 0.7f);
        }

        internal static void BookSeal(SpriteBatch sb, Vector2 center, float size, Color ink)
        {
            Vector2 top = center + new Vector2(0, -size), right = center + new Vector2(size, 0);
            Vector2 bottom = center + new Vector2(0, size), left = center + new Vector2(-size, 0);
            GUI.DrawLine(sb, top, right, ink * 0.65f); GUI.DrawLine(sb, right, bottom, ink * 0.65f);
            GUI.DrawLine(sb, bottom, left, ink * 0.65f); GUI.DrawLine(sb, left, top, ink * 0.65f);
            Vector2 spineTop = center + new Vector2(0, -size * 0.1f);
            Vector2 spineBottom = center + new Vector2(0, size * 0.48f);
            GUI.DrawLine(sb, spineTop, spineBottom, ink);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector2 edgeTop = center + new Vector2(side * size * 0.62f, -size * 0.34f);
                Vector2 edgeBottom = center + new Vector2(side * size * 0.62f, size * 0.28f);
                GUI.DrawLine(sb, spineTop, edgeTop, ink);
                GUI.DrawLine(sb, edgeTop, edgeBottom, ink);
                GUI.DrawLine(sb, edgeBottom, spineBottom, ink);
                for (int line = 0; line < 2; line++)
                {
                    float y = size * (0.05f + line * 0.2f);
                    GUI.DrawLine(sb, center + new Vector2(side * size * 0.16f, y),
                        center + new Vector2(side * size * 0.46f, y - size * 0.12f), ink * 0.6f);
                }
            }
        }

        internal static void Folio(SpriteBatch sb, Rectangle r, int gutterX, int bodyTop, int bodyHeight, bool headerSigil)
        {
            // 纸纹留在外边与书沟，不往正文上叠噪点。
            Color pageEdge = new(72, 68, 87), fiber = new(35, 39, 52), thread = new(104, 90, 132);
            int inset = GUI.IntScale(7), step = Math.Max(1, GUI.IntScale(3));
            for (int layer = 0; layer < 3; layer++)
            {
                int x = r.Right - inset - layer * step, y = r.Bottom - inset - layer * step;
                GUI.DrawLine(sb, new Vector2(x, r.Y + GUI.IntScale(28)),
                    new Vector2(x, y - GUI.IntScale(18)), pageEdge);
                GUI.DrawLine(sb, new Vector2(r.X + GUI.IntScale(124), y),
                    new Vector2(x - GUI.IntScale(18), y), pageEdge);
            }
            int grainTop = r.Y + GUI.IntScale(116), grainHeight = Math.Max(1, r.Height - GUI.IntScale(154));
            for (int line = 0; line < 18; line++)
            {
                int y = grainTop + grainHeight * line / 18;
                int end = r.Right - GUI.IntScale(16 + line % 2);
                GUI.DrawLine(sb, new Vector2(end - GUI.IntScale(3 + line % 4), y),
                    new Vector2(end, y), fiber);
            }
            int stitch = Math.Max(2, GUI.IntScale(4));
            GUI.DrawLine(sb, new Vector2(gutterX - stitch / 2, bodyTop),
                new Vector2(gutterX - stitch / 2, bodyTop + bodyHeight), fiber);
            GUI.DrawLine(sb, new Vector2(gutterX + stitch / 2, bodyTop),
                new Vector2(gutterX + stitch / 2, bodyTop + bodyHeight), fiber);
            for (int i = 0; i < 4; i++)
            {
                int y = bodyTop + bodyHeight * (i * 2 + 1) / 8;
                GUI.DrawLine(sb, new Vector2(gutterX - stitch, y - stitch),
                    new Vector2(gutterX + stitch, y + stitch), thread);
                GUI.DrawLine(sb, new Vector2(gutterX - stitch, y + stitch),
                    new Vector2(gutterX + stitch, y - stitch), thread * 0.6f);
            }
            if (headerSigil)
                AlchemySigil(sb, new Vector2(r.Right - GUI.IntScale(155), r.Y + GUI.IntScale(55)),
                    GUI.IntScale(22), thread * 0.8f);
        }

        private static void AlchemySigil(SpriteBatch sb, Vector2 center, float size, Color ink)
        {
            for (int i = 0; i < 12; i++)
            {
                float a = i * MathF.PI / 6f, b = (i + 1) * MathF.PI / 6f;
                GUI.DrawLine(sb, center + new Vector2(MathF.Cos(a) * size, MathF.Sin(a) * size),
                    center + new Vector2(MathF.Cos(b) * size, MathF.Sin(b) * size), ink * 0.65f);
            }
            for (int orientation = -1; orientation <= 1; orientation += 2)
            {
                Vector2 tip = center + new Vector2(0, orientation * size * 0.82f);
                Vector2 left = center + new Vector2(-size * 0.71f, -orientation * size * 0.41f);
                Vector2 right = center + new Vector2(size * 0.71f, -orientation * size * 0.41f);
                GUI.DrawLine(sb, tip, left, ink); GUI.DrawLine(sb, left, right, ink);
                GUI.DrawLine(sb, right, tip, ink);
            }
            Seal(sb, center, size * 0.25f, ink);
        }
    }
}
