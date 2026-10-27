using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;
using Touhou.Handbook;

namespace Touhou.MissionJournal
{
    public sealed partial class TouhouMissionJournal
    {
        private GUIFrame launcher;
        private GUIListBox missions;
        private GUITextBlock status;
        private GUIMessageBox confirmation;
        private GUIButton closeReader;
        private readonly List<GUIButton> missionButtons = new();
        private bool closed, guiFailed;
        private int screenWidth, screenHeight, language;
        private float scale;
        private GUIFont font;
        private uint fontSize;
        private bool Available(Character reader) =>
            reader == Character.Controlled && CanRead(reader) && !GUI.DisableHUD &&
            !CharacterHUD.IsCampaignInterfaceOpen && !reader.ShouldLockHud() &&
            CharacterHealth.OpenHealthWindow == null && reader.SelectedCharacter == null &&
            (reader.SelectedItem == item || reader.SelectedSecondaryItem == item ||
                reader.SelectedItem == null && reader.SelectedSecondaryItem == null &&
                !reader.HeldItems.Any(i => i.GetComponent<TouhouHandbook>() != null) &&
                reader.HeldItems.FirstOrDefault(i => i.GetComponent<TouhouMissionJournal>() != null) == item);

        public override bool Select(Character reader)
        {
            if (reader == Character.Controlled) closed = false;
            return base.Select(reader);
        }
        public override bool ShouldDrawHUDComponentSpecific(Character reader)
        {
            bool show = Available(reader);
            if (!show)
            {
                CloseConfirmation();
                if (GuiFrame != null) GuiFrame.Visible = false;
                if (launcher != null) launcher.Visible = false;
            }
            return show;
        }
        public override void AddToGUIUpdateList(int order = 0)
        {
            UpdateHUDComponentSpecific(Character.Controlled, 0, null);
            if (GuiFrame?.Visible == true) base.AddToGUIUpdateList(order);
            if (launcher?.Visible == true) launcher.AddToGUIUpdateList(order: order);
        }
        public override void UpdateHUDComponentSpecific(Character reader, float deltaTime, Camera cam)
        {
            if (!Available(reader) || guiFailed || GUI.Canvas == null || GUIStyle.Font == null)
            {
                CloseConfirmation();
                if (GuiFrame != null) GuiFrame.Visible = false;
                if (launcher != null) launcher.Visible = false;
                return;
            }
            try
            {
                if (GuiFrame == null || screenWidth != GameMain.GraphicsWidth || screenHeight != GameMain.GraphicsHeight ||
                    scale != GUI.Scale || language != TextManager.LanguageVersion || font != GUIStyle.Font ||
                    fontSize != GUIStyle.Font.Size)
                { ReleaseView(); CreateReader(); }
                RefreshSnapshot();
                GuiFrame.Visible = !closed;
                launcher.Visible = closed;
                UpdateChoice();
            }
            catch (Exception ex)
            {
                guiFailed = true;
                ReleaseView();
                DebugConsole.AddWarning("[任务日志] 界面初始化失败：" + ex.Message);
            }
        }
        private static string Resolve(string text)
        {
            string localized = TextManager.Get(text).Value;
            return string.IsNullOrWhiteSpace(localized) || localized == text ? text : localized;
        }
        private void CreateReader()
        {
            screenWidth = GameMain.GraphicsWidth; screenHeight = GameMain.GraphicsHeight;
            scale = GUI.Scale; language = TextManager.LanguageVersion; font = GUIStyle.Font; fontSize = font.Size;
            int width = Math.Min(GUI.IntScale(1080), (int)(GUI.UIWidth * .91f));
            int height = Math.Min(GUI.IntScale(780), (int)(screenHeight * .86f));
            int pad = GUI.IntScale(22), gap = GUI.IntScale(14), small = Line(GUIStyle.SmallFont);
            int inner = width - pad * 2;
            int titleHeight = (int)Math.Ceiling(GUIStyle.SubHeadingFont.MeasureString(
                GUIStyle.SubHeadingFont.WrapText(Document.Title, inner - GUI.IntScale(162))).Y);
            int header = small + titleHeight + GUI.IntScale(18);
            int footer = small * 3 + GUI.IntScale(16), bodyY = pad + header + gap;
            int bodyHeight = Math.Max(1, height - bodyY - pad - footer - gap);
            int railWidth = (int)(inner * .27f), contentX = pad + railWidth + gap;
            GuiFrame = new GUIFrame(new RectTransform(new Point(width, height), GUI.Canvas, Anchor.Center), style: null)
            { Color = Color.Transparent, UserData = "journal.reader" };
            new GUICustomComponent(new RectTransform(Vector2.One, GuiFrame.RectTransform), (sb, c) =>
            {
                HandbookTheme.CutPanel(sb, c.Rect, HandbookTheme.Background, HandbookTheme.Border, GUI.IntScale(18));
                HandbookTheme.Folio(sb, c.Rect, c.Rect.X + pad + railWidth + gap / 2,
                    c.Rect.Y + bodyY, bodyHeight, false);
                HandbookTheme.BookSeal(sb, new Vector2(c.Rect.X + pad + GUI.IntScale(18),
                    c.Rect.Y + pad + GUI.IntScale(20)), GUI.IntScale(18), HandbookTheme.Cyan);
            }) { CanBeFocused = false };
            HandbookTheme.Label(GuiFrame.RectTransform, pad + GUI.IntScale(50), pad, inner - GUI.IntScale(162),
                "TOUHOU / COMMISSION ARCHIVE", GUIStyle.SmallFont, HandbookTheme.Cyan);
            HandbookTheme.Label(GuiFrame.RectTransform, pad + GUI.IntScale(50), pad + small + GUI.IntScale(5),
                inner - GUI.IntScale(162), Document.Title, GUIStyle.SubHeadingFont);
            closeReader = HandbookTheme.Button(HandbookTheme.At(GuiFrame.RectTransform, width - pad - GUI.IntScale(90), pad,
                GUI.IntScale(90), small + GUI.IntScale(24)), "关闭", () =>
                { closed = true; GuiFrame.Visible = false; launcher.Visible = true; return true; });

            var rail = HandbookTheme.Panel(HandbookTheme.At(GuiFrame.RectTransform, pad, bodyY, railWidth, bodyHeight),
                HandbookTheme.Rail, HandbookTheme.Cyan, "journal.rules");
            // 窄屏把规则也放进滚动区，字体变大时不挤掉接取按钮。
            var rules = new GUIListBox(HandbookTheme.At(rail.RectTransform, gap, gap,
                railWidth - gap * 2, bodyHeight - gap * 2), style: null) { Spacing = gap, KeepSpaceForScrollBar = true };
            HandbookTheme.StyleScroll(rules);
            AddRule(rules, "委托登记", HandbookTheme.Cyan);
            AddRule(rules, Document.SeriesName, HandbookTheme.Ink);
            AddRule(rules, "全队共享 · 每巡回一次", HandbookTheme.Violet);
            AddRule(rules, "同系列所有日志共用一个名额。\n\n确认后本巡回不能更改；重开、换书、转交他人都不会重置。\n\n不同系列分别计数，下一巡回恢复。", HandbookTheme.Muted);
            rules.UpdateScrollBarSize();
            missions = new GUIListBox(HandbookTheme.At(GuiFrame.RectTransform, contentX, bodyY,
                width - pad - contentX, bodyHeight), style: null) { Spacing = gap, KeepSpaceForScrollBar = true };
            HandbookTheme.StyleScroll(missions);
            for (int i = 0; i < Document.Missions.Count; i++) AddMission(i);
            missions.UpdateScrollBarSize();
            status = HandbookTheme.Label(GuiFrame.RectTransform, pad, height - pad - footer, inner,
                "正在读取本巡回登记……", GUIStyle.SmallFont, HandbookTheme.Cyan);
            launcher = new GUIFrame(new RectTransform(new Point(GUI.IntScale(200), GUI.IntScale(46)),
                GUI.Canvas, Anchor.Center), style: null) { Visible = false, UserData = "journal.launcher" };
            HandbookTheme.Button(new RectTransform(Vector2.One, launcher.RectTransform), "查看任务日志",
                () => { closed = false; return true; });
        }
        private static int Line(GUIFont f) => (int)Math.Ceiling(f.MeasureString("委托").Y);
        private static void AddRule(GUIListBox list, string text, Color ink)
        {
            var block = new GUITextBlock(new RectTransform(new Vector2(1, 0), list.Content.RectTransform),
                text, font: GUIStyle.SmallFont, textColor: ink, wrap: true, style: null) { CanBeFocused = false };
            block.CalculateHeightFromText();
        }
        private void AddMission(int index)
        {
            var mission = Document.Missions[index];
            int pad = GUI.IntScale(15), width = missions.Content.Rect.Width;
            var panel = HandbookTheme.Panel(new RectTransform(new Vector2(1, 0), missions.Content.RectTransform,
                minSize: new Point(0, 1)), HandbookTheme.Card, HandbookTheme.Border, "journal.mission");
            var label = HandbookTheme.Label(panel.RectTransform, pad, pad, width - pad * 2, Resolve(mission.Text));
            int y = pad + label.Rect.Height + GUI.IntScale(8);
            y += HandbookTheme.Label(panel.RectTransform, pad, y, width - pad * 2, "委托 " + (index + 1).ToString("00") +
                "  /  " + Document.SeriesName, GUIStyle.SmallFont, HandbookTheme.Muted).Rect.Height + GUI.IntScale(12);
            int buttonHeight = Line(GUIStyle.SmallFont) + GUI.IntScale(20);
            var button = HandbookTheme.Button(HandbookTheme.At(panel.RectTransform, pad, y, width - pad * 2, buttonHeight),
                "接取此任务", () => Confirm(index));
            button.TextBlock.Font = GUIStyle.SmallFont;
            missionButtons.Add(button);
            int height = y + buttonHeight + pad;
            panel.RectTransform.MinSize = new Point(0, height);
            panel.RectTransform.Resize(new Point(width, height));
        }
        private bool Confirm(int index)
        {
            if (!Known || Choice != null || confirmation != null || closed || !Available(Character.Controlled) ||
                index < 0 || index >= Document.Missions.Count) return false;
            CreateConfirmation(index);
            UpdateChoice();
            return true;
        }
        private void UpdateChoice()
        {
            var choice = Choice;
            if (confirmation != null && (confirmation.Closed || !GUIMessageBox.MessageBoxes.Contains(confirmation) ||
                !Known || choice != null)) CloseConfirmation();
            if (closeReader != null) closeReader.Enabled = confirmation == null;
            for (int i = 0; i < missionButtons.Count; i++)
            {
                bool chosen = choice != null && choice.Event.Equals(Document.Missions[i].Event, StringComparison.OrdinalIgnoreCase);
                var button = missionButtons[i];
                button.Enabled = Known && choice == null && confirmation == null;
                button.Selected = chosen;
                button.TextBlock.Text = !Known ? "同步中" : chosen ? "已接取 · 本巡回已登记" : choice != null ?
                    "本系列已登记 · 不可接取" : "接取此任务";
            }
            status.Text = !Known ? "正在读取服务端登记……" : choice != null ?
                "本巡回已登记：" + Resolve(choice.Text) + "\n同系列所有日志已锁定，下一巡回恢复。" :
                Failure.Length > 0 ? Failure : "本系列可接取 1 项 · 确认后全队共享，下一巡回恢复。";
            status.CalculateHeightFromText();
        }
        partial void ReleaseView()
        {
            CloseConfirmation();
            foreach (var frame in new[] { GuiFrame, launcher })
            {
                if (frame == null) continue;
                GUI.RemoveFromUpdateList(frame); frame.Visible = false; frame.RectTransform.Parent = null; frame.ClearChildren();
            }
            GuiFrame = launcher = null; missions = null; status = null; closeReader = null; missionButtons.Clear();
        }
    }
}
