using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Touhou.Handbook;

namespace Touhou.MissionJournal
{
    public sealed partial class TouhouMissionJournal
    {
        private void CreateConfirmation(int index)
        {
            // 只借原生的模态输入和关闭队列，不把绿色弹窗皮肤带进日志。
            var box = confirmation = new GUIMessageBox("", "", Array.Empty<LocalizedString>())
            {
                DrawOnTop = true, Draggable = false, CanBeFocused = true,
                UserData = "journal.confirmation"
            };
            box.sprites = null;
            box.Color = box.HoverColor = box.SelectedColor = box.DisabledColor = box.PressedColor = Color.Transparent;
            box.OutlineColor = Color.Transparent;
            box.InnerFrame.Visible = false;
            box.ClearChildren();
            new GUICustomComponent(new RectTransform(Vector2.One, box.RectTransform), (sb, c) =>
                GUI.DrawRectangle(sb, c.Rect, new Color(5, 8, 15, 200), true)) { CanBeFocused = false };

            int width = Math.Max(1, Math.Min(GUI.IntScale(620), (int)(GUI.UIWidth * .88f)));
            int pad = GUI.IntScale(24), gap = GUI.IntScale(14), inner = Math.Max(1, width - pad * 2);
            var panel = new GUIFrame(new RectTransform(new Point(width, 1), box.RectTransform, Anchor.Center),
                style: null) { Color = Color.Transparent, UserData = "journal.confirm.panel" };
            int divider = 0;
            new GUICustomComponent(new RectTransform(Vector2.One, panel.RectTransform), (sb, c) =>
            {
                HandbookTheme.CutPanel(sb, c.Rect, HandbookTheme.Background, HandbookTheme.Cyan, GUI.IntScale(16));
                HandbookTheme.BookSeal(sb, new Vector2(c.Rect.X + pad + GUI.IntScale(17),
                    c.Rect.Y + pad + GUI.IntScale(20)), GUI.IntScale(18), HandbookTheme.Cyan);
                GUI.DrawLine(sb, new Vector2(c.Rect.X + pad, c.Rect.Y + divider),
                    new Vector2(c.Rect.Right - pad, c.Rect.Y + divider), HandbookTheme.Border);
            }) { CanBeFocused = false };
            int textX = pad + GUI.IntScale(48), titleWidth = Math.Max(1, width - pad - textX);
            var kicker = HandbookTheme.Label(panel.RectTransform, textX, pad, titleWidth,
                "COMMISSION / CONFIRMATION", GUIStyle.SmallFont, HandbookTheme.Cyan);
            var heading = HandbookTheme.Label(panel.RectTransform, textX,
                pad + kicker.Rect.Height + GUI.IntScale(5), titleWidth, "确认接取委托", GUIStyle.SubHeadingFont);
            divider = heading.Rect.Bottom - panel.Rect.Y + gap;
            int bodyY = divider + gap;
            int buttonHeight = Line(GUIStyle.SmallFont) + GUI.IntScale(24);
            int maxBody = Math.Max(1, (int)(GameMain.GraphicsHeight * .90f) - bodyY - gap - buttonHeight - pad);
            var body = new GUIListBox(HandbookTheme.At(panel.RectTransform, pad, bodyY, inner, maxBody), style: null)
            { Spacing = gap, KeepSpaceForScrollBar = true, UserData = "journal.confirm.body" };
            HandbookTheme.StyleScroll(body);
            body.UpdateDimensions();

            var task = ConfirmationCard(body, HandbookTheme.Card, HandbookTheme.Cyan, "journal.confirm.task");
            int cardPad = GUI.IntScale(16), y = cardPad, textWidth = Math.Max(1, task.Rect.Width - cardPad * 2);
            y += HandbookTheme.Label(task.RectTransform, cardPad, y, textWidth, "即将登记的任务",
                GUIStyle.SmallFont, HandbookTheme.Muted).Rect.Height + GUI.IntScale(8);
            y += HandbookTheme.Label(task.RectTransform, cardPad, y, textWidth,
                Resolve(Document.Missions[index].Text)).Rect.Height + GUI.IntScale(8);
            y += HandbookTheme.Label(task.RectTransform, cardPad, y, textWidth, Document.SeriesName,
                GUIStyle.SmallFont, HandbookTheme.Cyan).Rect.Height + cardPad;
            SetConfirmationHeight(task, y);

            var warning = ConfirmationCard(body, HandbookTheme.Note, HandbookTheme.Violet, "journal.confirm.warning");
            y = cardPad;
            textWidth = Math.Max(1, warning.Rect.Width - cardPad * 2);
            y += HandbookTheme.Label(warning.RectTransform, cardPad, y, textWidth, "全队共享 · 本巡回仅此一次",
                GUIStyle.SmallFont, HandbookTheme.Violet).Rect.Height + GUI.IntScale(8);
            y += HandbookTheme.Label(warning.RectTransform, cardPad, y, textWidth,
                "确认后不能更改，同系列其他日志也将锁定。\n下一巡回恢复；取消不会消耗名额。",
                GUIStyle.SmallFont, HandbookTheme.Ink).Rect.Height + cardPad;
            SetConfirmationHeight(warning, y);

            int bodyHeight = Math.Max(1, Math.Min(maxBody, task.Rect.Height + gap + warning.Rect.Height));
            body.RectTransform.Resize(new Point(inner, bodyHeight));
            body.UpdateDimensions();
            body.UpdateScrollBarSize();
            int buttonY = bodyY + bodyHeight + gap, buttonWidth = Math.Max(1, (inner - gap) / 2);
            var cancel = HandbookTheme.Button(HandbookTheme.At(panel.RectTransform, pad, buttonY, buttonWidth, buttonHeight),
                "取消", () => FinishConfirmation(box, index, false));
            var accept = HandbookTheme.Button(HandbookTheme.At(panel.RectTransform, pad + buttonWidth + gap,
                buttonY, buttonWidth, buttonHeight), "确认接取", () => FinishConfirmation(box, index, true));
            cancel.TextBlock.Font = accept.TextBlock.Font = GUIStyle.SmallFont;
            cancel.UserData = "journal.confirm.cancel";
            accept.UserData = "journal.confirm.accept";
            accept.Selected = true;
            box.Buttons.Clear();
            box.Buttons.Add(accept);
            box.Buttons.Add(cancel);
            // 不绑定回车，避免刚打开时误确认不可撤销的选择。
            box.OnEnterPressed = null;
            SetConfirmationHeight(panel, buttonY + buttonHeight + pad);
        }

        private static GUIFrame ConfirmationCard(GUIListBox body, Color fill, Color accent, string tag) =>
            HandbookTheme.Panel(new RectTransform(new Vector2(1, 0), body.Content.RectTransform,
                minSize: new Point(0, 1)), fill, accent, tag);

        private static void SetConfirmationHeight(GUIFrame frame, int height)
        {
            frame.RectTransform.MinSize = new Point(0, height);
            frame.RectTransform.Resize(new Point(frame.Rect.Width, height));
        }

        private bool FinishConfirmation(GUIMessageBox box, int index, bool accept)
        {
            if (confirmation != box) return false;
            CloseConfirmation();
            if (accept && Available(Character.Controlled)) Choose(index);
            UpdateChoice();
            return true;
        }

        private void CloseConfirmation()
        {
            var box = confirmation;
            confirmation = null;
            if (box == null) return;
            box.Close();
            GUI.RemoveFromUpdateList(box);
            box.Visible = false;
            box.RectTransform.Parent = null;
            box.ClearChildren();
        }
    }
}
