using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace Touhou.VirtualPantry
{
    public sealed partial class TouhouVirtualPantry : ItemComponent
    {
        private GUITextBlock pantrySummary;
        private GUITextBlock pantryStatus;
        private GUIListBox pantryRows;
        private GUIButton pantryRestore;
        private GUIButton pantryResume;
        private bool pantryHasRevision;
        private uint pantryShownRevision;
        private uint pantryShownRemoteToken;
        private long pantryShownCount;
        private readonly List<GUIButton> pantryTakeButtons = new List<GUIButton>();
        private bool pantryTakeEnabled;
        private bool pantryGuiFailed, pantryWasOpen;
        private float pantrySyncTimer;
        private bool PantryReady => PantryPlugin.Authority || RemoteToken != 0;

        private bool PantrySelected(Character character)
        {
            return character != null && character == Character.Controlled &&
                !character.Removed && !character.IsDead &&
                (character.SelectedItem == item || character.SelectedSecondaryItem == item);
        }

        partial void InitGui()
        {
            CanBeSelected = true;
            AllowUIOverlap = false;
            // 只在选中时创建，避开 Item 构造和原生网络事件读取期间
            GuiFrame = new GUIFrame(new RectTransform(new Vector2(0.28f, 0.48f), GUI.Canvas,
                Anchor.CenterRight, minSize: new Point(GUI.IntScale(340), GUI.IntScale(340)),
                maxSize: new Point(GUI.IntScale(460), GUI.IntScale(520))))
            {
                Visible = false
            };
            var content = new GUIFrame(new RectTransform(new Vector2(0.94f, 0.94f),
                GuiFrame.RectTransform, Anchor.Center), style: null);

            pantrySummary = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.075f),
                content.RectTransform), "虚拟储物柜", font: GUIStyle.SubHeadingFont)
            {
                AutoScaleHorizontal = true
            };
            pantryStatus = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.065f),
                content.RectTransform) { RelativeOffset = new Vector2(0, 0.08f) }, "状态：等待同步",
                font: GUIStyle.SmallFont) { AutoScaleHorizontal = true };

            var actions = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.09f),
                content.RectTransform) { RelativeOffset = new Vector2(0, 0.15f) }, isHorizontal: true)
            {
                RelativeSpacing = 0.02f
            };
            pantryRestore = new GUIButton(new RectTransform(new Vector2(0.60f, 1.0f),
                actions.RectTransform), "停止并还原柜内", style: "GUIButtonSmall")
            {
                ToolTip = "停止虚拟化，逐步将所有虚拟物品还原到柜子原格子；空间不足时请取走实物，腾出空位。",
                OnClicked = (button, data) =>
                {
                    if (PantrySelected(Character.Controlled) && !Draining) { Request(3); }
                    return true;
                }
            };
            pantryResume = new GUIButton(new RectTransform(new Vector2(0.38f, 1.0f),
                actions.RectTransform), "恢复虚拟化", style: "GUIButtonSmall")
            {
                OnClicked = (button, data) =>
                {
                    if (PantrySelected(Character.Controlled) && Suspended && !Draining && !Faulted)
                    {
                        Request(4);
                    }
                    return true;
                }
            };
            pantryRestore.TextBlock.AutoScaleHorizontal = true;
            pantryResume.TextBlock.AutoScaleHorizontal = true;

            pantryRows = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.50f),
                content.RectTransform) { RelativeOffset = new Vector2(0, 0.26f) })
            {
                KeepSpaceForScrollBar = true
            };
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.22f), content.RectTransform)
                { RelativeOffset = new Vector2(0, 0.78f) },
                "实物仍使用柜子原格子。\n关闭扩展前，请先还原全部虚拟物品。\n虚拟物不参与商店直接出售，需先取出。",
                font: GUIStyle.SmallFont, textAlignment: Alignment.TopLeft, wrap: true);
        }

        partial void RefreshGui()
        {
            if (!PantrySelected(Character.Controlled))
            {
                pantryWasOpen = false;
                if (GuiFrame != null) { GuiFrame.Visible = false; }
                return;
            }
            if (GuiFrame == null) { return; }

            if (!pantryHasRevision || pantryShownRevision != Revision || pantryShownRemoteToken != RemoteToken)
            {
                // View() 可能分配快照：没被选中或没变化时一律别调
                uint revision = Revision;
                var entries = View();
                float scroll = pantryRows.BarScroll;
                pantryRows.ClearChildren();
                pantryTakeButtons.Clear();
                pantryShownCount = 0;
                var names = new Dictionary<uint, string>();
                foreach (var entry in entries)
                {
                    if (entry.Count <= 0) { continue; }
                    pantryShownCount += entry.Count;
                    if (!names.TryGetValue(entry.Prefab, out string name))
                    {
                        var prefab = ItemPrefab.Prefabs.Find(p => p.UintIdentifier == entry.Prefab);
                        name = prefab == null ? "未知物品 #" + entry.Prefab : prefab.Name.Value;
                        names.Add(entry.Prefab, name);
                    }
                    AddPantryRow(entry, name);
                }
                if (pantryShownCount == 0)
                {
                    new GUITextBlock(new RectTransform(new Vector2(1, 0.25f),
                        pantryRows.Content.RectTransform), "暂无虚拟物品", font: GUIStyle.SmallFont,
                        textAlignment: Alignment.Center);
                }
                pantryRows.UpdateScrollBarSize();
                pantryRows.BarScroll = scroll;
                pantrySummary.Text = "虚拟储物柜 · 虚拟数量 " + pantryShownCount;
                pantryShownRevision = revision;
                pantryShownRemoteToken = RemoteToken;
                pantryHasRevision = true;
            }

            // 状态会脱离库存版本单独变化，所以每次刷新都重算
            string state = !PantryReady ? "等待服务器库存" :
                ForeignManager ? "检测到 IO 组件：已暂停，可取到人物栏" :
                item.ParentInventory?.Owner is Item ? "嵌套容器中：保持实物存储" :
                Draining ? "还原中（原格子满时，请腾出空位）" :
                Suspended ? "已暂停虚拟化" : "虚拟化运行中";
            pantryStatus.Text = "状态：" + (Faulted ? "错误 / " : "") + state;
            pantryStatus.TextColor = Faulted ? Color.OrangeRed :
                Draining || Suspended ? Color.Yellow : Color.LightGreen;
            pantryRestore.Enabled = PantryReady && !ForeignManager && !Draining && (!Suspended || pantryShownCount > 0 || Faulted);
            pantryResume.Enabled = PantryReady && Suspended && !Draining && !Faulted;
            bool canTake = PantryReady && !Draining && (!Faulted || ForeignManager);
            if (pantryTakeEnabled != canTake)
            {
                foreach (var button in pantryTakeButtons) { button.Enabled = canTake; }
                pantryTakeEnabled = canTake;
            }
        }

        private void AddPantryRow(PantryView entry, string name)
        {
            string condition = entry.Condition.ToString("0.###");
            var row = new GUIFrame(new RectTransform(new Vector2(1, 0),
                pantryRows.Content.RectTransform, minSize: new Point(0, GUI.IntScale(62))), style: null);
            var label = new GUITextBlock(new RectTransform(new Vector2(1, 0.47f), row.RectTransform),
                name + " ×" + entry.Count, font: GUIStyle.SmallFont)
            {
                AutoScaleHorizontal = true,
                ToolTip = name + "\n虚拟数量：" + entry.Count + "\n耐久：" + condition +
                    "%　品质：" + entry.Quality
            };
            new GUITextBlock(new RectTransform(new Vector2(0.53f, 0.49f),
                row.RectTransform) { RelativeOffset = new Vector2(0, 0.49f) },
                "耐久 " + condition + "%  品质 " + entry.Quality, font: GUIStyle.SmallFont)
            {
                AutoScaleHorizontal = true,
                ToolTip = label.ToolTip
            };
            uint key = entry.Key;
            AddPantryTakeButton(row.RectTransform, 0.54f, "取1", 1, key);
            AddPantryTakeButton(row.RectTransform, 0.77f, "取32", 2, key);
        }

        private void AddPantryTakeButton(RectTransform row, float x, string text, byte action, uint key)
        {
            var button = new GUIButton(new RectTransform(new Vector2(0.22f, 0.47f), row)
                { RelativeOffset = new Vector2(x, 0.50f) }, text, style: "GUIButtonSmall")
            {
                Enabled = PantryReady && !Draining && (!Faulted || ForeignManager),
                ToolTip = action == 1 ? "取出1个到人物物品栏" : "取出最多32个到人物物品栏",
                OnClicked = (clicked, data) =>
                {
                    if (PantryReady && PantrySelected(Character.Controlled) && !Draining && (!Faulted || ForeignManager))
                    {
                        Request(action, key);
                    }
                    return true;
                }
            };
            button.TextBlock.AutoScaleHorizontal = true;
            pantryTakeButtons.Add(button);
        }

        public override bool ShouldDrawHUDComponentSpecific(Character character)
        {
            return PantrySelected(character);
        }

        public override void AddToGUIUpdateList(int order = 0)
        {
            if (GuiFrame == null) { return; }
            GuiFrame.Visible = PantrySelected(Character.Controlled);
            if (GuiFrame.Visible) { base.AddToGUIUpdateList(order); }
        }

        public override void UpdateHUDComponentSpecific(Character character, float deltaTime, Camera cam)
        {
            if (Retired || !PantrySelected(character)) { return; }
            if (GuiFrame == null && !pantryGuiFailed && GUI.Canvas != null && GUIStyle.SmallFont != null)
            {
                try { InitGui(); }
                catch (Exception e)
                {
                    pantryGuiFailed = true;
                    RemoveGui();
                    DebugConsole.AddWarning("[东方食材虚拟柜] 面板初始化失败：" + e.Message);
                }
            }
            if (GuiFrame == null) { return; }
            GuiFrame.Visible = PantrySelected(character);
            if (!GuiFrame.Visible) { return; }
            // 打开时每秒握手一次，刷新订阅并补上中间漏掉的摘要
            pantrySyncTimer -= deltaTime;
            if (!pantryWasOpen || pantrySyncTimer <= 0)
            {
                pantryWasOpen = true;
                pantrySyncTimer = 1;
                Request(0);
            }
            RefreshGui();
        }

        internal void PlaceGui()
        {
            if (GuiFrame == null) { return; }
            Rectangle bounds = item.GetComponent<ItemContainer>()?.GuiFrame?.Rect ?? Rectangle.Empty;
            if (bounds == Rectangle.Empty) { return; }
            int margin = GUI.IntScale(8);
            int x = bounds.Right + margin;
            if (x + GuiFrame.Rect.Width > GameMain.GraphicsWidth - margin) { x = bounds.Left - GuiFrame.Rect.Width - margin; }
            x = Math.Clamp(x, margin, Math.Max(margin, GameMain.GraphicsWidth - GuiFrame.Rect.Width - margin));
            int y = Math.Clamp(bounds.Top, margin, Math.Max(margin, GameMain.GraphicsHeight - GuiFrame.Rect.Height - margin));
            GuiFrame.RectTransform.ScreenSpaceOffset += new Point(x, y) - GuiFrame.Rect.Location;
        }

        partial void RemoveGui()
        {
            if (GuiFrame != null)
            {
                GuiFrame.Visible = false;
                GuiFrame.RectTransform.Parent = null;
                GuiFrame.ClearChildren();
                GuiFrame = null;
            }
            pantrySummary = null;
            pantryStatus = null;
            pantryRows = null;
            pantryRestore = null;
            pantryResume = null;
            pantryTakeButtons.Clear();
            pantryHasRevision = false;
            pantryShownCount = 0;
        }
    }
}
