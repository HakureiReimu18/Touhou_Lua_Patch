using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace Touhou.Handbook
{
    public sealed partial class HandbookPlugin
    {
        partial void RemoveClientViews() => TouhouHandbook.ReleaseAllViews();
    }

    public sealed partial class TouhouHandbook
    {
        private static readonly HashSet<TouhouHandbook> views = new();
        private readonly List<GUIButton> chapterButtons = new();
        private GUIListBox chapters, body;
        private GUIButton back, previous, next, reopen;
        private GUIFrame launcher;
        private GUITextBlock position;
        private bool guiFailed, readerClosed;
        private int screenWidth, screenHeight, languageVersion;
        private float guiScale;
        private GUIFont currentFont;
        private uint fontSize;
        private LanguageIdentifier language;

        private static string Resolve(string key, string fallback)
            => string.IsNullOrEmpty(key) ? fallback : TextManager.Get(key).Fallback(fallback).Value;

        private static string L(string key, params (string Key, string Value)[] values)
        {
            string value = TextManager.GetWithVariables("touhou.handbook.ui." + key, values).Value;
            return string.IsNullOrEmpty(value) ? key : value;
        }

        private string BookTitle => Resolve(Document.TitleKey, Document.Title);
        private static string PageTitle(HandbookPage page) => Resolve(page.TitleKey, page.Title);

        private bool Available(Character character)
        {
            if (character == null || character != Character.Controlled || character.Removed || character.IsDead ||
                character.IsUnconscious || character.Stun > 0 || item.Removed || GUI.DisableHUD ||
                CharacterHUD.IsCampaignInterfaceOpen || character.ShouldLockHud() ||
                CharacterHealth.OpenHealthWindow != null || character.SelectedCharacter != null) return false;
            if (character.SelectedItem == item || character.SelectedSecondaryItem == item) return true;
            // 手持走原版的装备 HUD 入口，不能再要求 SelectedItem 也指着这本书。
            return character.SelectedItem == null && character.SelectedSecondaryItem == null &&
                character.HeldItems.FirstOrDefault(i => i.GetComponent<TouhouHandbook>() != null) == item;
        }

        private bool Reading(Character character) => !readerClosed && Available(character);

        public override bool Select(Character character)
        {
            if (character == Character.Controlled) readerClosed = false;
            return base.Select(character);
        }

        public override bool ShouldDrawHUDComponentSpecific(Character character)
        {
            bool reading = Reading(character);
            if (!reading && GuiFrame != null) GuiFrame.Visible = false;
            return reading;
        }

        public override void AddToGUIUpdateList(int order = 0)
        {
            // 原版装备路径会直接到这里，不会先调 Item.UpdateHUD。
            UpdateHUDComponentSpecific(Character.Controlled, 0f, null);
            if (GuiFrame?.Visible == true) base.AddToGUIUpdateList(order);
            if (launcher?.Visible == true) launcher.AddToGUIUpdateList(order: order);
        }

        public override void UpdateHUDComponentSpecific(Character character, float deltaTime, Camera cam)
        {
            bool available = Available(character);
            if (!available || guiFailed || GUI.Canvas == null || GUIStyle.Font == null || GUIStyle.SubHeadingFont == null)
            {
                if (GuiFrame != null) GuiFrame.Visible = false;
                if (launcher != null) launcher.Visible = false;
                return;
            }
            try
            {
                bool changed = screenWidth != GameMain.GraphicsWidth || screenHeight != GameMain.GraphicsHeight ||
                    guiScale != GUI.Scale || languageVersion != TextManager.LanguageVersion ||
                    currentFont != GUIStyle.Font || fontSize != GUIStyle.Font.Size ||
                    !language.Equals(GameSettings.CurrentConfig.Language);
                if (GuiFrame == null || changed)
                {
                    RememberPosition();
                    ReleaseView();
                    CreateReader();
                }
                GuiFrame.Visible = !readerClosed;
                launcher.Visible = readerClosed;
            }
            catch (Exception ex)
            {
                guiFailed = true;
                ReleaseView();
                DebugConsole.AddWarning("[Touhou.Handbook] " + L("initfailed", ("[reason]", ex.Message)));
            }
        }

        private static RectTransform Box(RectTransform parent, float width, float height, float x = 0, float y = 0)
            => new RectTransform(new Vector2(width, height), parent) { RelativeOffset = new Vector2(x, y) };

        private void CloseReader()
        {
            RememberPosition();
            readerClosed = true;
            var reader = Character.Controlled;
            if (reader?.SelectedItem == item) reader.SelectedItem = null;
            if (reader?.SelectedSecondaryItem == item) reader.SelectedSecondaryItem = null;
            if (GuiFrame != null) GuiFrame.Visible = false;
            if (launcher != null) launcher.Visible = Available(reader);
        }

        private void CreateReader()
        {
            screenWidth = GameMain.GraphicsWidth; screenHeight = GameMain.GraphicsHeight;
            guiScale = GUI.Scale; languageVersion = TextManager.LanguageVersion;
            currentFont = GUIStyle.Font; fontSize = currentFont.Size;
            language = GameSettings.CurrentConfig.Language;
            int width = Math.Max(1, Math.Min(GUI.IntScale(1140), (int)(GUI.UIWidth * 0.91f)));
            int height = Math.Max(1, Math.Min(GUI.IntScale(810), (int)(screenHeight * 0.86f)));
            int outer = GUI.IntScale(24), gap = GUI.IntScale(18);
            int innerWidth = width - outer * 2;
            int closeWidth = Math.Max(GUI.IntScale(92),
                (int)Math.Ceiling(GUIStyle.Font.MeasureString(L("close")).X) + GUI.IntScale(28));
            int titleWidth = Math.Max(1, innerWidth - GUI.IntScale(51) - closeWidth - gap);
            int smallLine = (int)Math.Ceiling(GUIStyle.SmallFont.MeasureString(
                GUIStyle.SmallFont.WrapText(L("masthead"), titleWidth)).Y);
            int titleLine = (int)Math.Ceiling(GUIStyle.SubHeadingFont.MeasureString(
                GUIStyle.SubHeadingFont.WrapText(BookTitle, titleWidth)).Y);
            int headerHeight = Math.Max(GUI.IntScale(78), smallLine + titleLine + GUI.IntScale(18));
            int footerHeight = Math.Max(GUI.IntScale(44), smallLine + GUI.IntScale(18));
            bool headerSigil = innerWidth > GUI.IntScale(850) && closeWidth <= GUI.IntScale(110) &&
                GUIStyle.SubHeadingFont.MeasureString(BookTitle).X + GUI.IntScale(51) < innerWidth - GUI.IntScale(180);
            int bodyY = headerHeight + gap;
            int bodyHeight = Math.Max(GUI.IntScale(80), height - outer * 2 - bodyY - footerHeight - gap);
            int railWidth = (int)(innerWidth * 0.235f), contentX = railWidth + gap;
            GuiFrame = new GUIFrame(new RectTransform(new Point(width, height), GUI.Canvas, Anchor.Center),
                style: null) { Color = Color.Transparent, UserData = "handbook.reader" };
            views.Add(this);
            new GUICustomComponent(new RectTransform(Vector2.One, GuiFrame.RectTransform), (sb, c) =>
            {
                var r = c.Rect;
                HandbookTheme.CutPanel(sb, r, HandbookTheme.Background, HandbookTheme.Border, GUI.IntScale(18));
                int inset = GUI.IntScale(25);
                GUI.DrawLine(sb, new Vector2(r.X + inset, r.Y + outer + headerHeight),
                    new Vector2(r.Right - inset, r.Y + outer + headerHeight), HandbookTheme.Border);
                HandbookTheme.BookSeal(sb, new Vector2(r.X + GUI.IntScale(43), r.Y + GUI.IntScale(43)),
                    GUI.IntScale(19), HandbookTheme.Violet);
                HandbookTheme.Folio(sb, r, r.X + outer + railWidth + gap / 2,
                    r.Y + outer + bodyY, bodyHeight, headerSigil);
                for (int i = 0; i < 7; i++)
                    GUI.DrawRectangle(sb, new Rectangle(r.X + GUI.IntScale(25 + i * 12), r.Bottom - GUI.IntScale(17),
                        GUI.IntScale(6), GUI.IntScale(3)), i < 5 ? HandbookTheme.Violet : HandbookTheme.Cyan, true);
            }) { CanBeFocused = false };
            var inner = new GUIFrame(HandbookTheme.At(GuiFrame.RectTransform, outer, outer,
                innerWidth, height - outer * 2), style: null);
            HandbookTheme.Label(inner.RectTransform, GUI.IntScale(51), 0, titleWidth,
                L("masthead"), GUIStyle.SmallFont, HandbookTheme.Muted);
            HandbookTheme.Label(inner.RectTransform, GUI.IntScale(51), smallLine + GUI.IntScale(5), titleWidth,
                BookTitle, GUIStyle.SubHeadingFont);
            HandbookTheme.Button(HandbookTheme.At(inner.RectTransform, innerWidth - closeWidth, 0,
                closeWidth, footerHeight), L("close"),
                () => { CloseReader(); return true; });

            var rail = HandbookTheme.Panel(HandbookTheme.At(inner.RectTransform, 0, bodyY, railWidth, bodyHeight),
                HandbookTheme.Rail, HandbookTheme.Violet, "handbook.rail");
            var indexLabel = HandbookTheme.Label(rail.RectTransform, GUI.IntScale(16), GUI.IntScale(14),
                rail.Rect.Width - GUI.IntScale(28), L("index"), GUIStyle.SmallFont, HandbookTheme.Muted);
            int railPad = GUI.IntScale(10), railHeader = indexLabel.Rect.Height + GUI.IntScale(32);
            chapters = new GUIListBox(HandbookTheme.At(rail.RectTransform, railPad, railHeader,
                railWidth - railPad * 2, Math.Max(1, bodyHeight - railHeader - railPad)), style: null)
            { Spacing = GUI.IntScale(9), KeepSpaceForScrollBar = true };
            body = new GUIListBox(HandbookTheme.At(inner.RectTransform, contentX, bodyY,
                innerWidth - contentX, bodyHeight), style: null)
            { Spacing = GUI.IntScale(14), KeepSpaceForScrollBar = true };
            HandbookTheme.StyleScroll(chapters);
            HandbookTheme.StyleScroll(body);
            foreach (var page in Document.Pages)
            {
                string pageId = page.Id;
                int index = Document.Pages.IndexOf(page) + 1;
                var chapterRect = new RectTransform(new Vector2(1, 0), chapters.Content.RectTransform,
                    minSize: new Point(0, GUI.IntScale(65)));
                string pageTitle = PageTitle(page);
                var button = HandbookTheme.Button(chapterRect, pageTitle,
                    () => Navigate(pageId), index.ToString("00"));
                button.TextBlock.Font = GUIStyle.SmallFont;
                string wrapped = GUIStyle.SmallFont.WrapText(pageTitle, button.TextBlock.Rect.Width);
                int chapterHeight = Math.Max(chapterRect.Rect.Height,
                    (int)Math.Ceiling(GUIStyle.SmallFont.MeasureString(wrapped).Y) + GUI.IntScale(20));
                chapterRect.MinSize = new Point(0, chapterHeight);
                chapterRect.Resize(new Point(chapterRect.Rect.Width, chapterHeight));
                button.TextBlock.RectTransform.Resize(new Point(button.TextBlock.Rect.Width, chapterHeight));
                button.ToolTip = pageTitle;
                button.UserData = pageId;
                chapterButtons.Add(button);
            }
            int footerY = bodyY + bodyHeight + gap;
            int footerButton = Math.Max(GUI.IntScale(92),
                (int)Math.Ceiling(new[] { L("back"), L("previous"), L("next") }
                    .Max(s => GUIStyle.Font.MeasureString(s).X)) + GUI.IntScale(28));
            back = HandbookTheme.Button(HandbookTheme.At(inner.RectTransform, 0, footerY, footerButton, footerHeight),
                L("back"), () =>
            {
                RememberPosition();
                if (Navigation.Back()) RenderPage();
                return true;
            });
            previous = HandbookTheme.Button(HandbookTheme.At(inner.RectTransform, footerButton + gap, footerY,
                footerButton, footerHeight), L("previous"),
                () => Navigate(Navigation.Adjacent(-1)));
            int positionX = footerButton * 2 + gap * 2;
            position = new GUITextBlock(HandbookTheme.At(inner.RectTransform, positionX, footerY,
                Math.Max(1, innerWidth - positionX - footerButton - gap), footerHeight), "",
                textColor: HandbookTheme.Muted, textAlignment: Alignment.Center,
                font: GUIStyle.SmallFont, style: null) { AutoScaleHorizontal = true };
            next = HandbookTheme.Button(HandbookTheme.At(inner.RectTransform, innerWidth - footerButton, footerY,
                footerButton, footerHeight), L("next"),
                () => Navigate(Navigation.Adjacent(1)));
            string readLabel = L("read");
            int launcherWidth = Math.Max(1, Math.Min((int)(GUI.UIWidth * 0.65f),
                Math.Max(GUI.IntScale(190), (int)Math.Ceiling(GUIStyle.Font.MeasureString(readLabel).X) + GUI.IntScale(28))));
            int launcherHeight = Math.Max(GUI.IntScale(46), (int)Math.Ceiling(GUIStyle.Font.MeasureString(
                GUIStyle.Font.WrapText(readLabel, Math.Max(1, launcherWidth - GUI.IntScale(20)))).Y) + GUI.IntScale(16));
            launcher = new GUIFrame(new RectTransform(new Point(launcherWidth, launcherHeight),
                GUI.Canvas, Anchor.Center),
                style: null) { Visible = false, UserData = "handbook.launcher" };
            reopen = HandbookTheme.Button(new RectTransform(Vector2.One, launcher.RectTransform), readLabel,
                () => { readerClosed = false; UpdateHUDComponentSpecific(Character.Controlled, 0, null); return true; });
            chapters.UpdateScrollBarSize();
            RenderPage();
        }

        private void RememberPosition() { if (body != null) Navigation.RememberScroll(body.BarScroll); }
        private bool Navigate(string page)
        {
            RememberPosition();
            if (!Navigation.Go(page)) return false;
            RenderPage();
            return true;
        }
        private string BlockText(HandbookBlock block) => Resolve(block.Key, block.Text);

        private void RenderPage()
        {
            body.ClearChildren();
            var page = Document.Find(Navigation.Current);
            foreach (var button in chapterButtons) button.Selected = (string)button.UserData == Navigation.Current;
            back.Enabled = Navigation.CanGoBack;
            previous.Enabled = Navigation.Adjacent(-1) != null;
            next.Enabled = Navigation.Adjacent(1) != null;
            if (page == null) { AddText(L("empty")); return; }
            string index = (Document.Pages.IndexOf(page) + 1).ToString("00"), total = Document.Pages.Count.ToString("00");
            string fullPosition = L("position", ("[index]", index), ("[total]", total), ("[title]", PageTitle(page)));
            position.Text = GUIStyle.SmallFont.MeasureString(fullPosition).X <= position.Rect.Width ? fullPosition :
                L("positioncompact", ("[index]", index), ("[total]", total));
            AddHeading(PageTitle(page), L("chapter", ("[index]", index)));
            foreach (var block in page.Blocks)
            {
                string text = BlockText(block);
                switch (block.Kind)
                {
                    case HandbookBlockKind.Heading: AddHeading(text); break;
                    case HandbookBlockKind.Text: AddText(text); break;
                    case HandbookBlockKind.Note: AddText(text, note: true); break;
                    case HandbookBlockKind.Steps: AddSteps(block.Steps); break;
                    case HandbookBlockKind.Items: AddItems(block.Items); break;
                    case HandbookBlockKind.Recipe: AddRecipe(block); break;
                    case HandbookBlockKind.Link:
                        var target = Document.Find(block.Page);
                        if (target != null) AddLink(block.Page, text.Length > 0 ? text : PageTitle(target));
                        else AddText(L("missingpage", ("[page]", block.Page)), note: true);
                        break;
                }
            }
            body.UpdateScrollBarSize();
            body.BarScroll = Navigation.Scroll;
        }

        private GUIFrame Block(Color fill, Color accent, string tag)
            => HandbookTheme.Panel(new RectTransform(new Vector2(1, 0), body.Content.RectTransform,
                minSize: new Point(0, 1)),
                fill, accent, tag);
        private static void Height(GUIFrame panel, int height)
        {
            panel.RectTransform.MinSize = new Point(0, Math.Max(1, height));
            panel.RectTransform.Resize(new Point(panel.Rect.Width, Math.Max(1, height)));
        }

        private void AddHeading(string text, string subtitle = "")
        {
            int pad = GUI.IntScale(16), y = GUI.IntScale(10);
            var panel = Block(HandbookTheme.Rail, HandbookTheme.Violet, "handbook.heading");
            if (subtitle.Length > 0)
            {
                var sub = HandbookTheme.Label(panel.RectTransform, pad, y, panel.Rect.Width - pad * 2,
                    subtitle, GUIStyle.SmallFont, HandbookTheme.Muted);
                y += sub.Rect.Height + GUI.IntScale(5);
            }
            var title = HandbookTheme.Label(panel.RectTransform, pad, y, panel.Rect.Width - pad * 2,
                text, GUIStyle.SubHeadingFont);
            Height(panel, y + title.Rect.Height + GUI.IntScale(12));
        }

        private void AddText(string text, bool note = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            int pad = GUI.IntScale(16), y = pad;
            var panel = Block(note ? HandbookTheme.Note : HandbookTheme.Card,
                note ? HandbookTheme.Violet : HandbookTheme.Border, note ? "handbook.note" : "handbook.text");
            if (note)
            {
                y += HandbookTheme.Label(panel.RectTransform, pad, y, panel.Rect.Width - pad * 2,
                    L("note"), GUIStyle.SmallFont, HandbookTheme.Violet).Rect.Height + GUI.IntScale(8);
            }
            var label = HandbookTheme.Label(panel.RectTransform, pad, y, panel.Rect.Width - pad * 2, text);
            Height(panel, y + label.Rect.Height + pad);
        }

        private void AddSteps(List<HandbookText> steps)
        {
            int pad = GUI.IntScale(16), y = pad, badge = GUI.IntScale(30);
            var panel = Block(HandbookTheme.Card, HandbookTheme.Cyan, "handbook.steps");
            y += HandbookTheme.Label(panel.RectTransform, pad, y, panel.Rect.Width - pad * 2,
                L("steps"), GUIStyle.SmallFont, HandbookTheme.Cyan).Rect.Height + GUI.IntScale(12);
            for (int i = 0; i < steps.Count; i++)
            {
                var number = HandbookTheme.Label(panel.RectTransform, pad, y, badge, (i + 1).ToString("00"),
                    GUIStyle.SmallFont, HandbookTheme.Cyan);
                var text = HandbookTheme.Label(panel.RectTransform, pad + badge + GUI.IntScale(8), y,
                    panel.Rect.Width - pad * 2 - badge - GUI.IntScale(8), Resolve(steps[i].Key, steps[i].Text));
                y += Math.Max(number.Rect.Height, text.Rect.Height) + GUI.IntScale(12);
            }
            Height(panel, y + GUI.IntScale(4));
        }

        private static ItemPrefab Prefab(string id)
            => ItemPrefab.Prefabs.Find(p => p.Identifier.Value.Equals(id, StringComparison.OrdinalIgnoreCase));
        private static string ItemText(LocalizedString value) => RichString.Rich(value).SanitizedValue;

        private GUIFrame ItemCard(RectTransform parent, HandbookItem entry, int x, int y, int width, bool compact)
        {
            var prefab = Prefab(entry.Identifier);
            int pad = GUI.IntScale(12), icon = GUI.IntScale(compact ? 38 : 44);
            var row = HandbookTheme.Panel(HandbookTheme.At(parent, x, y, width, 0),
                HandbookTheme.Rail, HandbookTheme.Border, "handbook.item");
            row.ToolTip = prefab == null ? entry.Identifier : ItemText(prefab.Name) + "\n" + ItemText(prefab.Description);
            int textX = compact ? pad : icon + pad * 2;
            int nameY = compact ? pad + icon + GUI.IntScale(8) : pad;
            var sprite = prefab?.InventoryIcon ?? prefab?.Sprite;
            if (sprite != null)
                new GUIImage(HandbookTheme.At(row.RectTransform, pad, pad, icon, icon), sprite, scaleToFit: true)
                { CanBeFocused = false, Color = prefab.InventoryIcon == null ? prefab.SpriteColor : prefab.InventoryIconColor };
            var name = HandbookTheme.Label(row.RectTransform, textX, nameY,
                width - textX - pad, (prefab == null ? L("missingitem", ("[id]", entry.Identifier)) : ItemText(prefab.Name)));
            int bottom = nameY + name.Rect.Height;
            if (compact)
                HandbookTheme.Label(row.RectTransform, width - GUI.IntScale(56), pad, GUI.IntScale(46),
                    "×" + entry.Amount, GUIStyle.SmallFont, HandbookTheme.Cyan);
            else
            {
                var amount = HandbookTheme.Label(row.RectTransform, textX, bottom + GUI.IntScale(4),
                    width - textX - pad, "×" + entry.Amount, GUIStyle.SmallFont, HandbookTheme.Cyan);
                bottom += amount.Rect.Height + GUI.IntScale(4);
            }
            string entryText = Resolve(entry.Key, entry.Text);
            if (entryText.Length > 0)
            {
                var description = HandbookTheme.Label(row.RectTransform, pad, Math.Max(bottom + GUI.IntScale(8), pad + icon),
                    width - pad * 2, entryText, GUIStyle.SmallFont, HandbookTheme.Muted);
                bottom = description.Rect.Y - row.Rect.Y + description.Rect.Height;
            }
            Height(row, Math.Max(pad + icon, bottom) + pad);
            if (entry.Page.Length > 0 && Document.Find(entry.Page) != null)
            {
                var click = new GUIButton(new RectTransform(Vector2.One, row.RectTransform), "", style: null)
                { ToolTip = row.ToolTip, OnClicked = (button, data) => Navigate(entry.Page) };
                click.Frame.Visible = false;
            }
            return row;
        }

        private int ItemGrid(RectTransform parent, IList<HandbookItem> items, int x, int y, int width, bool compact)
        {
            int gap = GUI.IntScale(10), minimum = GUI.IntScale(compact ? 150 : 230);
            int columns = Math.Max(1, Math.Min(compact ? 3 : 2, (width + gap) / (minimum + gap)));
            int cellWidth = Math.Max(1, (width - gap * (columns - 1)) / columns);
            int start = y;
            for (int index = 0; index < items.Count; index += columns)
            {
                var row = new List<GUIFrame>();
                int height = 0;
                for (int column = 0; column < columns && index + column < items.Count; column++)
                {
                    var cell = ItemCard(parent, items[index + column], x + column * (cellWidth + gap), y, cellWidth, compact);
                    row.Add(cell);
                    height = Math.Max(height, cell.Rect.Height);
                }
                foreach (var cell in row) Height(cell, height);
                y += height + gap;
            }
            return y - start - (items.Count > 0 ? gap : 0);
        }

        private void AddItems(IList<HandbookItem> items)
        {
            var group = new GUIFrame(new RectTransform(new Vector2(1, 0), body.Content.RectTransform,
                minSize: new Point(0, 1)),
                style: null) { UserData = "handbook.items" };
            Height(group, ItemGrid(group.RectTransform, items, 0, 0, group.Rect.Width, false));
        }

        private void AddLink(string page, string text)
        {
            var rect = new RectTransform(new Vector2(1, 0), body.Content.RectTransform,
                minSize: new Point(0, GUI.IntScale(46)));
            var button = HandbookTheme.Button(rect, text + " →", () => Navigate(page));
            int height = Math.Max(rect.Rect.Height, (int)Math.Ceiling(GUIStyle.Font.MeasureString(
                GUIStyle.Font.WrapText(text + " →", button.TextBlock.Rect.Width)).Y) + GUI.IntScale(16));
            rect.MinSize = new Point(0, height);
            rect.Resize(new Point(rect.Rect.Width, height));
            button.TextBlock.RectTransform.Resize(new Point(button.TextBlock.Rect.Width, height));
            button.ToolTip = text;
        }

        private void AddRecipe(HandbookBlock block)
        {
            var source = Prefab(block.Source);
            var other = block.Other.Length == 0 ? null : Prefab(block.Other);
            var station = Prefab(block.Station);
            if (source == null || station == null || (block.Other.Length > 0 && other == null))
            { AddText(L("missingrecipe"), note: true); return; }
            int pad = GUI.IntScale(16), y = pad;
            var panel = Block(HandbookTheme.Card, HandbookTheme.Cyan, "handbook.recipe");
            int width = panel.Rect.Width - pad * 2;
            y += HandbookTheme.Label(panel.RectTransform, pad, y, width, BlockText(block),
                GUIStyle.SubHeadingFont).Rect.Height + GUI.IntScale(6);
            y += HandbookTheme.Label(panel.RectTransform, pad, y, width, L("station", ("[station]", ItemText(station.Name))),
                GUIStyle.SmallFont, HandbookTheme.Muted).Rect.Height + GUI.IntScale(14);
            var inputs = new List<HandbookItem> { new() { Identifier = block.Source } };
            if (other != null) inputs.Add(new HandbookItem { Identifier = block.Other });
            y += HandbookTheme.Label(panel.RectTransform, pad, y, width, L("inputs"),
                GUIStyle.SmallFont, HandbookTheme.Cyan).Rect.Height + GUI.IntScale(8);
            y += ItemGrid(panel.RectTransform, inputs, pad, y, width, true) + GUI.IntScale(12);
            string action = TextManager.Get("Alchemy_Table.Research").Fallback("Transmute").Value;
            y += HandbookTheme.Label(panel.RectTransform, pad, y, width,
                L("consume", ("[action]", action)),
                GUIStyle.SmallFont, HandbookTheme.Violet).Rect.Height + GUI.IntScale(12);
            var matches = HandbookRecipes.Match(source.DeconstructItems.Select(r => new HandbookRecipeRule
            {
                Identifier = r.ItemIdentifier.Value, Amount = r.Amount, MinCondition = r.MinCondition,
                MaxCondition = r.MaxCondition, Weight = r.Commonness,
                Stations = r.RequiredDeconstructor.Select(s => s.Value).ToArray(),
                Others = r.RequiredOtherItem.Select(s => s.Value).ToArray()
            }), station.Identifier.Value, station.Tags.Select(t => t.Value),
                other?.Identifier.Value ?? "", other?.Tags.Select(t => t.Value) ?? Enumerable.Empty<string>());
            string resultLabel = matches.Count == 0 ? L("nomatch") :
                source.RandomDeconstructionOutput ? L("random", ("[count]",
                    Math.Min(source.RandomDeconstructionOutputAmount, matches.Count).ToString())) : L("outputs");
            y += HandbookTheme.Label(panel.RectTransform, pad, y, width, resultLabel,
                GUIStyle.SmallFont, HandbookTheme.Cyan).Rect.Height + GUI.IntScale(8);
            y += ItemGrid(panel.RectTransform, matches.Select(r => new HandbookItem
                { Identifier = r.Identifier, Amount = r.Amount }).ToList(), pad, y, width, true) + GUI.IntScale(12);
            y += HandbookTheme.Label(panel.RectTransform, pad, y, width, L("readonly"),
                GUIStyle.SmallFont, HandbookTheme.Muted).Rect.Height;
            Height(panel, y + pad);
        }

        private void ReleaseView()
        {
            if (GuiFrame != null)
            {
                GUI.RemoveFromUpdateList(GuiFrame);
                GuiFrame.Visible = false;
                GuiFrame.RectTransform.Parent = null;
                GuiFrame.ClearChildren();
                GuiFrame = null;
            }
            if (launcher != null)
            {
                GUI.RemoveFromUpdateList(launcher);
                launcher.Visible = false;
                launcher.RectTransform.Parent = null;
                launcher.ClearChildren();
                launcher = null;
            }
            chapters = body = null;
            back = previous = next = reopen = null;
            position = null;
            chapterButtons.Clear();
            views.Remove(this);
        }

        internal static void ReleaseAllViews()
        {
            foreach (var view in views.ToArray()) { view.RememberPosition(); view.ReleaseView(); }
        }
        public override void RemoveComponentSpecific()
        {
            ReleaseView();
            base.RemoveComponentSpecific();
        }
    }
}
