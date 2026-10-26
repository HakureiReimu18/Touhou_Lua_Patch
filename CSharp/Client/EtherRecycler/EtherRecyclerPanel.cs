using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// 以太回收机：自定义设备面板（纯客户端显示层，服务端逻辑仍在 Lua）
//  · 面板由 C# 自绘（背景 + 标题 + 状态），把三个容器的「库存本体」直接画进面板里；
//  · 槽位仍是原生 Inventory（没有任何私有实现）：拖放/堆叠/右键/过滤/联机同步全部走原版
//    （拖放由 CharacterHUD.Update → Inventory.UpdateDragging 全局处理，依据是 Inventory.RectTransform 算出的槽位矩形）；
//  · 自绘按钮转发 XML 原生 CustomInterface 的点击（客户端→服务端事件 + LuaHook）；
//  · Lua 同步和 C# think 互为兜底，同一个固定更新周期只做一次布局，GUI 注册仍逐入口维护；
//  · 失败安全：出任何异常都吞掉、恢复原生窗口、停用面板；面板没接管时设备照 XML 排版工作。
//
// 调布局就改下方常量；尺寸单位是 1080p 下的界面像素，其他分辨率/UI 缩放自动换算。

namespace Touhou.EtherRecycler;

public sealed class EtherRecyclerPanelPlugin : IAssemblyPlugin
{
    private Harmony harmony;
    private bool patched;

    public void Initialize() => harmony = new Harmony("touhou.etherrecycler.panel");

    public void OnLoadCompleted()
    {
        try
        {
            if (!patched)
            {
                // 两路入口共用显示判定，避免联机换手时窗口闪烁。
                LuaCsSetup.Instance.Hook.Add("think", "Touhou.EtherRecyclerPanel.Think", OnThink);
                LuaCsSetup.Instance.Hook.Add("Touhou.EtherRecyclerPanel.Sync", "Touhou.EtherRecyclerPanel", OnLuaSync);
                LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.EtherRecyclerPanel.RoundEnd", OnRoundEnd);

                patched = true;
                EtherRecyclerPanel.Log("面板插件已加载（Lua 驱动 + think 兜底）");
            }
        }
        catch (Exception ex)
        {
            EtherRecyclerPanel.LogError("面板插件加载失败：" + ex.Message);
        }
    }

    public void PreInitPatching() { }

    public void Dispose()
    {
        try { LuaCsSetup.Instance.Hook.Remove("think", "Touhou.EtherRecyclerPanel.Think"); } catch { /* 卸载时无所谓 */ }
        try { LuaCsSetup.Instance.Hook.Remove("Touhou.EtherRecyclerPanel.Sync", "Touhou.EtherRecyclerPanel"); } catch { /* 同上 */ }
        try { LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.EtherRecyclerPanel.RoundEnd"); } catch { /* 同上 */ }
        harmony?.UnpatchSelf();
        patched = false;
        EtherRecyclerPanel.ResetAll();
    }

    private static object OnRoundEnd(params object[] args)
    {
        EtherRecyclerPanel.ResetAll();
        return null;
    }

    private static object OnThink(params object[] args)
    {
        try { EtherRecyclerPanel.Tick(); } catch (Exception ex) { EtherRecyclerPanel.Disable(ex); }
        return null;
    }

    // 客户端 Lua 每帧调用：参数是本地玩家当前选中的物品（可能为 nil）
    private static object OnLuaSync(params object[] args)
    {
        try
        {
            var item = (args != null && args.Length > 0) ? args[0] as Item : null;
            EtherRecyclerPanel.SyncFromLua(item);
        }
        catch (Exception ex)
        {
            EtherRecyclerPanel.Disable(ex);
        }
        return null;
    }


}

internal sealed class EtherRecyclerPanelState
{
    public Item Machine;
    public GUIFrame Panel;
    public GUITextBlock Title;
    public GUITextBlock Status;
    public GUITextBlock InputLabel;
    public GUITextBlock CatalystLabel;
    public GUITextBlock OutputLabel;
    public GUITextBlock Prediction;
    public GUIButton StartButton;
    public CustomInterface Buttons;
    public GUICustomComponent InputSlots;
    public GUICustomComponent CatalystSlots;
    public GUICustomComponent OutputSlots;
    public readonly List<ItemContainer> HiddenContainers = new();
    public readonly Dictionary<GUIComponent, (bool Visible, bool Focused, Anchor Anchor, Vector2 Size, Vector2 Offset)> NativeFrames = new();
    public readonly Dictionary<ItemInventory, (RectTransform Native, RectTransform Custom)> NativeInventories = new();
    public bool Attached;
    public double LastLayoutTime = double.NaN;
    public bool Failed;
    public int LastScreenW = -1, LastScreenH = -1;
    public float LastUIScale = -1f;
    public Point LastInputSize, LastCatalystSize, LastOutputSize;
}

internal static class EtherRecyclerPanel
{
    // ==================== 布局参数（想调整外观改这里） ====================
    private const string MachineTag = "Touhou_Ether_Recycler";
    private const float SlotSize = 92f;         // 库存格子边长（界面像素 @1080p）
    private const float Padding = 18f;          // 面板内边距
    private const float TitleHeight = 42f;      // 标题栏高度
    private const float LabelHeight = 24f;      // 每个格子区上方说明文字的高度
    private const float Gap = 14f;              // 元素间距
    private const float PanelCenterY = -0.05f;  // 面板中心相对屏幕中心的纵向偏移（0 = 屏幕正中）
    private const float ButtonWidth = 200f;     // 按钮宽度（右列至少这么宽）
    private const float ButtonHeight = 48f;     // 按钮高度
    private const float PredictionMinWidth = 220f; // 预计产出文字的宽度下限

    private static readonly Dictionary<Item, EtherRecyclerPanelState> States = new();
    private static readonly List<Item> removedMachines = new();
    private static EtherRecyclerPanelState visibleState;
    private static double lastPruneTime = double.NaN;
    private const bool Verbose = false; // 面板提示日志（加载/接管/驱动接通）默认关闭
    private static bool disabled;
    private static bool firstPanelLogged;

    public static void Log(string message)
    {
        if (!Verbose) { return; } // 提示日志默认关闭（调试时把 Verbose 改 true）
        try { DebugConsole.NewMessage("[以太回收机面板] " + message, Color.Cyan); } catch { /* 控制台不可用就算了 */ }
    }

    public static void LogError(string message)
    {
        // 错误类日志始终打，方便排查
        try { DebugConsole.NewMessage("[以太回收机面板] " + message, Color.OrangeRed); } catch { /* 同上 */ }
    }

    public static void Disable(Exception ex)
    {
        disabled = true;
        LogError("面板已停用，退回原生窗口排版：" + ex.Message);
        ResetAll();
    }

    public static void ResetAll()
    {
        visibleState = null;
        foreach (var state in States.Values) { DestroyPanel(state); }
        States.Clear();
        removedMachines.Clear();
        lastPruneTime = double.NaN;
    }

    private static void DestroyPanel(EtherRecyclerPanelState state)
    {
        try { RestoreNativeFrames(state); } catch (Exception ex) { LogError("恢复原生界面失败：" + ex.Message); }
        if (state.Panel != null) { state.Panel.Visible = false; }
        try { GUI.RemoveFromUpdateList(state.Panel, true); } catch { /* 卸载时可能已被引擎摘掉 */ }
        try { if (state.Panel != null) { state.Panel.RectTransform.Parent = null; } } catch { /* 同上 */ }
    }

    private static void PruneRemoved(Item selected)
    {
        // 1.13.4 的 TotalTime 每次固定更新递增，think 在 CoroutineManager.Update 后发布；不是显示帧号。
        if (lastPruneTime != Timing.TotalTime)
        {
            lastPruneTime = Timing.TotalTime;
            foreach (var pair in States)
            {
                if (pair.Key.Removed) { removedMachines.Add(pair.Key); }
            }
        }
        // 同一周期第二个入口也不能让刚移除的当前设备留下来。
        if (visibleState?.Machine is Item visible && visible.Removed && !removedMachines.Contains(visible))
        {
            removedMachines.Add(visible);
        }
        if (selected != null && selected.Removed && States.ContainsKey(selected) && !removedMachines.Contains(selected))
        {
            removedMachines.Add(selected);
        }
        foreach (var machine in removedMachines)
        {
            var state = States[machine];
            if (visibleState == state) { visibleState = null; }
            DestroyPanel(state);
            States.Remove(machine);
        }
        removedMachines.Clear();
    }

    public static void Tick() => SyncFromLua(Character.Controlled?.SelectedItem);

    public static void SyncFromLua(Item item)
    {
        if (disabled) { return; }
        PruneRemoved(item);
        var machine = GetRecycler(item);
        if (machine != null)
        {
            ShowFor(machine);
            return;
        }

        // 换手时第一次解析可能瞬时失败；确认已不是回收机才收起。
        bool looksLikeOurs = false;
        try { looksLikeOurs = item != null && !item.Removed && item.HasTag(MachineTag.ToIdentifier()); } catch { /* 取不到就当作不是 */ }
        if (!looksLikeOurs) { HideAll(); }
    }

    public static void ShowFor(Item machine)
    {
        if (disabled || GetRecycler(machine) == null) { return; }
        var state = GetOrCreate(machine);
        if (state == null || state.Failed) { return; }
        if (visibleState != state)
        {
            HideAll();
            visibleState = state;
        }

        bool needsRebuild = !state.Attached
            || state.LastScreenW != GameMain.GraphicsWidth
            || state.LastScreenH != GameMain.GraphicsHeight
            || Math.Abs(state.LastUIScale - Inventory.UIScale) > 0.0001f;
        if (needsRebuild)
        {
            Build(machine, state);
            if (!state.Attached) { return; }
            if (!firstPanelLogged)
            {
                firstPanelLogged = true;
                Log("已接管以太回收机面板（格子 " + SlotSize + "px）");
            }
        }

        if (needsRebuild || state.LastLayoutTime != Timing.TotalTime)
        {
            Layout(machine, state);
            state.LastLayoutTime = Timing.TotalTime;
        }
        MaintainGUI(state);
    }

    private static void HideAll()
    {
        var state = visibleState;
        if (state == null) { return; }
        visibleState = null;
        if (state.Panel != null)
        {
            state.Panel.Visible = false;
            try { GUI.RemoveFromUpdateList(state.Panel, true); } catch { /* 没在列表里也无所谓 */ }
        }
        RestoreNativeFrames(state);
        state.LastLayoutTime = double.NaN;
    }

    private static Item GetRecycler(Item item)
    {
        if (item == null || item.Removed) { return null; }
        try { return item.HasTag(MachineTag.ToIdentifier()) ? item : null; }
        catch { return null; }
    }

    private static EtherRecyclerPanelState GetOrCreate(Item machine)
    {
        if (States.TryGetValue(machine, out var existing)) { return existing; }

        var state = new EtherRecyclerPanelState { Machine = machine };
        try
        {
            state.Panel = new GUIFrame(new RectTransform(new Vector2(0.1f, 0.1f), GUI.Canvas, Anchor.Center), style: "ItemUI")
            {
                CanBeFocused = false,
                Visible = false
            };

            state.Title = new GUITextBlock(new RectTransform(new Vector2(0.7f, 1f), state.Panel.RectTransform, Anchor.TopLeft),
                TextManager.Get("entityname.touhou_ether_recycler"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.CenterLeft)
            {
                CanBeFocused = false
            };

            state.Status = new GUITextBlock(new RectTransform(new Vector2(0.3f, 1f), state.Panel.RectTransform, Anchor.TopRight),
                "", textAlignment: Alignment.CenterRight)
            {
                CanBeFocused = false
            };

            // 各区域的标题/说明（放在格子区上方，说明"放什么"）
            state.InputLabel = MakeLabel(state.Panel.RectTransform, TextManager.Get("touhou.recycler.input.hint"));
            state.CatalystLabel = MakeLabel(state.Panel.RectTransform, TextManager.Get("touhou.recycler.catalyst.hint"));
            state.OutputLabel = MakeLabel(state.Panel.RectTransform, TextManager.Get("touhou.recycler.output.hint"));

            // 按钮画在面板里（原生 CustomInterface 窗格会被藏起来，点击时复刻它的点击处理，
            // 这样客户端→服务端事件与 LuaHook 链路仍是原版的）
            state.StartButton = new GUIButton(new RectTransform(new Vector2(0f, 0f), state.Panel.RectTransform), TextManager.Get("touhou.recycler.start"), style: "DeviceButton");
            state.StartButton.OnClicked = (btn, userdata) => InvokeNativeButton(state, 0);

            // 预计产出（客户端按入料里的等级 tag 直接算，纯显示；不再要"预览"按钮）
            state.Prediction = MakeLabel(state.Panel.RectTransform, "", Alignment.TopLeft);



            state.InputSlots = MakeSlotArea(state.Panel.RectTransform);
            state.OutputSlots = MakeSlotArea(state.Panel.RectTransform);
            state.CatalystSlots = MakeSlotArea(state.Panel.RectTransform);

            States[machine] = state;
        }
        catch (Exception ex)
        {
            state.Failed = true;
            DestroyPanel(state);
            Disable(ex);
            return null;
        }

        return state;
    }

    private static GUICustomComponent MakeSlotArea(RectTransform parent)
    {
        return new GUICustomComponent(new RectTransform(new Vector2(0f, 0f), parent), (spriteBatch, component) =>
        {
            if (component.UserData is Inventory inventory) { inventory.Draw(spriteBatch); }
        })
        {
            CanBeFocused = true
        };
    }

    private static GUITextBlock MakeLabel(RectTransform parent, LocalizedString text, Alignment alignment = Alignment.CenterLeft)
    {
        return new GUITextBlock(new RectTransform(new Vector2(0f, 0f), parent), text, textAlignment: alignment)
        {
            CanBeFocused = false,
            Wrap = true
        };
    }

    // 复刻原生 CustomInterface 第 index 个按钮的点击（保留客户端→服务端事件与 LuaHook）
    private static bool InvokeNativeButton(EtherRecyclerPanelState state, int index)
    {
        try
        {
            if (state.Buttons?.GuiFrame == null) { return false; }

            var buttons = new List<GUIButton>();
            CollectButtons(state.Buttons.GuiFrame, buttons, 0);
            if (index >= 0 && index < buttons.Count)
            {
                var button = buttons[index];
                button.OnClicked?.Invoke(button, button.UserData);
                return true;
            }
        }
        catch (Exception ex)
        {
            LogError("按钮转发失败：" + ex.Message);
        }
        return false;
    }

    private static List<GUIButton> CollectButtons(GUIComponent parent)
    {
        var result = new List<GUIButton>();
        CollectButtons(parent, result, 0);
        return result;
    }

    private static void CollectButtons(GUIComponent parent, List<GUIButton> result, int depth)
    {
        if (parent == null || depth > 4) { return; }
        foreach (var child in parent.Children)
        {
            if (child is GUIButton button)
            {
                result.Add(button);
            }
            CollectButtons(child, result, depth + 1);
        }
    }

    // 重建前先交还原生挂接，按钮未就绪时才能安全退回原版。
    private static void Build(Item machine, EtherRecyclerPanelState state)
    {
        RestoreNativeFrames(state);
        state.Panel.Visible = false;
        GUI.RemoveFromUpdateList(state.Panel, true);
        var containers = GetContainers(machine);
        if (containers == null) { return; }

        state.LastScreenW = GameMain.GraphicsWidth;
        state.LastScreenH = GameMain.GraphicsHeight;
        state.LastUIScale = Inventory.UIScale;
        state.LastInputSize = Point.Zero;
        state.LastCatalystSize = Point.Zero;
        state.LastOutputSize = Point.Zero;

        float u = Inventory.UIScale;
        Vector2 input = CalcArea(containers.Value.InputContainer, u);
        Vector2 catalyst = CalcArea(containers.Value.CatalystContainer, u);
        Vector2 output = CalcArea(containers.Value.OutputContainer, u);

        var buttonComponent = machine.GetComponent<CustomInterface>();
        state.Buttons = buttonComponent;

        // 布局：左列＝入料 + 输出（宽度取两者较大者），右列＝催化 + 预计产出 + 按钮
        float leftWidth = Math.Max(input.X, output.X);
        float rightWidth = Math.Max(Math.Max(catalyst.X, ButtonWidth), PredictionMinWidth);
        float contentWidth = leftWidth + Gap + rightWidth;
        float rightHeight = LabelHeight + catalyst.Y + Gap + LabelHeight * 2f + Gap + ButtonHeight;
        float contentHeight = Math.Max((LabelHeight + input.Y) + Gap + (LabelHeight + output.Y), rightHeight);

        Vector2 screen = new Vector2(GameMain.GraphicsWidth, GameMain.GraphicsHeight);
        Vector2 panelSize = new Vector2(contentWidth + Padding * 2f, contentHeight + TitleHeight + Padding * 2f);
        state.Panel.RectTransform.RelativeSize = panelSize / screen;
        // 屏幕居中（⚙ 里的「重置位置」就是回到这里）
        state.Panel.RectTransform.Anchor = Anchor.Center;
        state.Panel.RectTransform.RelativeOffset = new Vector2(0f, PanelCenterY);

        state.HiddenContainers.Clear();
        state.HiddenContainers.Add(containers.Value.InputContainer);
        state.HiddenContainers.Add(containers.Value.CatalystContainer);
        state.HiddenContainers.Add(containers.Value.OutputContainer);
        // 按钮链路不可用（例如中途加入、原生 UI 还没建立）→ 本轮不接管，让原版窗口继续可用
        if (CollectButtons(buttonComponent?.GuiFrame).Count == 0)
        {
            LogError("按钮链路未就绪，本轮退回原生窗口（下一帧重试）");
            return;
        }

        HideNativeFrames(state);

        AttachInventory(state, containers.Value.Input, state.InputSlots);
        AttachInventory(state, containers.Value.Catalyst, state.CatalystSlots);
        AttachInventory(state, containers.Value.Output, state.OutputSlots);
        state.Attached = true;
    }

    private static void Layout(Item machine, EtherRecyclerPanelState state)
    {
        var containers = GetContainers(machine);
        if (containers == null) { return; }

        float u = Inventory.UIScale;
        Vector2 input = CalcArea(containers.Value.InputContainer, u);
        Vector2 catalyst = CalcArea(containers.Value.CatalystContainer, u);
        Vector2 output = CalcArea(containers.Value.OutputContainer, u);

        Vector2 panelSize = state.Panel.Rect.Size.ToVector2();
        if (panelSize.X < 1f || panelSize.Y < 1f) { return; }

        SetArea(state.Title, new Vector2(Padding + 34f, 0f), new Vector2(panelSize.X * 0.55f, TitleHeight), panelSize);
        SetArea(state.Status, new Vector2(panelSize.X - Padding - panelSize.X * 0.35f, 0f), new Vector2(panelSize.X * 0.35f, TitleHeight), panelSize);

        float leftWidth = Math.Max(input.X, output.X);
        float rightWidth = Math.Max(Math.Max(catalyst.X, ButtonWidth), PredictionMinWidth);
        float rightX = Padding + leftWidth + Gap;

        float y = TitleHeight + Padding;
        // 左列：入料 / 输出
        SetArea(state.InputLabel, new Vector2(Padding, y), new Vector2(leftWidth, LabelHeight), panelSize);
        SetArea(state.InputSlots, new Vector2(Padding, y + LabelHeight), input, panelSize);

        float secondRowY = y + LabelHeight + input.Y + Gap;
        SetArea(state.OutputLabel, new Vector2(Padding, secondRowY), new Vector2(leftWidth, LabelHeight), panelSize);
        SetArea(state.OutputSlots, new Vector2(Padding, secondRowY + LabelHeight), output, panelSize);

        // 右列：催化 / 预计产出 / 按钮
        SetArea(state.CatalystLabel, new Vector2(rightX, y), new Vector2(rightWidth, LabelHeight), panelSize);
        SetArea(state.CatalystSlots, new Vector2(rightX, y + LabelHeight), catalyst, panelSize);

        float predictionY = y + LabelHeight + catalyst.Y + Gap;
        SetArea(state.Prediction, new Vector2(rightX, predictionY), new Vector2(rightWidth, LabelHeight * 2f), panelSize);
        SetArea(state.StartButton, new Vector2(rightX, predictionY + LabelHeight * 2f + Gap), new Vector2(rightWidth, ButtonHeight), panelSize);

        // 库存的 RectTransform 可能被原版重建（分辨率变更）顶掉，发现不一致就抢回来；
        // 绘制区尺寸变了也要重算槽位（CreateSlots 只在调用时按矩形算一次）
        EnsureAttached(state, containers.Value.Input, state.InputSlots, ref state.LastInputSize, input);
        EnsureAttached(state, containers.Value.Catalyst, state.CatalystSlots, ref state.LastCatalystSize, catalyst);
        EnsureAttached(state, containers.Value.Output, state.OutputSlots, ref state.LastOutputSize, output);

        bool running = machine.GetComponent<LightComponent>()?.IsOn ?? false;
        state.Status.Text = TextManager.Get(running ? "touhou.recycler.isactive" : "touhou.recycler.panel.idle");
        state.StartButton.Text = TextManager.Get(running ? "fabricatorcancel" : "touhou.recycler.start");
        state.Prediction.Text = BuildPrediction(containers.Value.Input);

        // 专属服务器下服务端的 Inventory.Locked 不会同步到客户端（单人同一进程才会），
        // 所以这里按"运行中"在客户端自己锁住入料/催化：表现与单人一致（变灰、拖不出来）。
        // 服务端那份锁仍然生效，客户端锁只是让本地 UI 与拒绝拖放保持一致。
        if (containers.Value.Input.Locked != running) { containers.Value.Input.Locked = running; }
        if (containers.Value.Catalyst.Locked != running) { containers.Value.Catalyst.Locked = running; }

    }

    private static void MaintainGUI(EtherRecyclerPanelState state)
    {
        HideNativeFrames(state);
        RepairAttachment(state, state.InputSlots);
        RepairAttachment(state, state.CatalystSlots);
        RepairAttachment(state, state.OutputSlots);
        // GUI 更新列表每个固定更新都会清空，不能和昂贵布局一起跳过。
        // 暂停菜单打开时把面板收起来（避免盖在暂停菜单上面）。
        // 注意：要用引擎自己的标志 GUI.PauseMenuOpen——用帧对象 PauseMenu.Visible 判断会踩脏状态：
        // 中途加入时该帧要么还没建、要么留着上次的可见性，会把面板一直误判成"暂停中"藏着，
        // 直到玩家真正按一次 ESC 翻转标志位（实机症状：刚加入看不到窗口，按 ESC 后就能看到了）。
        bool modalOpen = false;
        try { modalOpen = GUI.PauseMenuOpen; } catch { /* 取不到就算了 */ }
        if (modalOpen)
        {
            state.Panel.Visible = false;
            try { GUI.RemoveFromUpdateList(state.Panel, true); } catch { /* 无所谓 */ }
            return;
        }

        state.Panel.Visible = true;

        // 关键：独立 GUIFrame 必须每帧注册进 GUI 更新列表才会被绘制/交互
        //（先例：本模组 CSharp/Shared/Bond/BondGui.cs 的 WindowTick；原版组件则是经 Item HUD 注册）
        state.Panel.AddToGUIUpdateList(false, 1);
    }

    // 客户端自己按入料里的等级 tag 算预计产出（纯显示用；数值与 Lua 的兜底表一致）
    private static LocalizedString BuildPrediction(ItemInventory inputInventory)
    {
        int metal = 0, byproduct = 0, count = 0;

        foreach (var item in inputInventory.AllItemsMod)
        {
            if (item == null || item.Removed) { continue; }

            int level = 0;
            if (item.HasTag("Touhou_Weapon_Level05".ToIdentifier())) { level = 5; }
            else if (item.HasTag("Touhou_Weapon_Level04".ToIdentifier())) { level = 4; }
            else if (item.HasTag("Touhou_Weapon_Level03".ToIdentifier())) { level = 3; }
            else if (item.HasTag("Touhou_Weapon_Level02".ToIdentifier())) { level = 2; }
            else if (item.HasTag("Touhou_Weapon_Level01".ToIdentifier())) { level = 1; }
            if (level == 0) { continue; }

            count++;
            if (level == 1) { byproduct += 2; }
            else if (level == 2) { metal += 1; }
            else if (level == 3) { metal += 2; }
            else if (level == 4) { metal += 4; }
            else { metal += 16; }
        }

        if (count == 0)
        {
            return TextManager.Get("touhou.recycler.noinput");
        }

        var parts = new List<string>();
        if (metal > 0) { parts.Add(GetItemName("Touhou_Base_Metal") + "×" + metal); }
        if (byproduct > 0) { parts.Add(GetItemName("Byproduct_Alchemical") + "×" + byproduct); }
        return TextManager.Get("touhou.recycler.panel.prediction").Value + string.Join("、", parts);
    }

    private static string GetItemName(string identifier)
    {
        var name = TextManager.Get("entityname." + identifier.ToLowerInvariant()).Value;
        return string.IsNullOrEmpty(name) ? identifier : name;
    }

    private static void HideNativeFrames(EtherRecyclerPanelState state)
    {
        // 只设 Visible=false 挡不住原版的鼠标命中与"重置位置/锁定位置"右键菜单，所以四件事一起做：
        // 不可见 + 不可聚焦 + 贴住面板 + 从 GUI 更新列表里摘掉（原版每帧又会注册，所以我每帧摘）
        foreach (var container in state.HiddenContainers)
        {
            if (container?.GuiFrame is GUIComponent frame)
            {
                HideFrame(state, frame);
            }
        }
        if (state.Buttons?.GuiFrame != null)
        {
            // 每帧按组件重新取窗格：原版在分辨率变更时会重建它，存的引用会失效
            HideFrame(state, state.Buttons.GuiFrame);
        }
    }

    private static void HideFrame(EtherRecyclerPanelState state, GUIComponent frame)
    {
        if (!state.NativeFrames.ContainsKey(frame))
        {
            var rect = frame.RectTransform;
            state.NativeFrames.Add(frame, (frame.Visible, frame.CanBeFocused, rect.Anchor, rect.RelativeSize, rect.RelativeOffset));
        }
        frame.Visible = false;
        frame.CanBeFocused = false;
        try { GUI.RemoveFromUpdateList(frame, true); } catch { /* 不在列表里也无所谓 */ }
        AlignToPanel(state, frame);
    }

    // 让被隐藏的原生窗格「贴住面板矩形」而不是挪到屏幕外：
    //  1) 它们不可见、不可聚焦、已从更新列表摘掉，不会吃点击；
    //  2) 原版「正被某某使用」提示的位置取的是这些窗格矩形的并集（Item.UpdateHUD），
    //     贴住面板后提示会落在面板下方，和其它机器一致。
    private static void AlignToPanel(EtherRecyclerPanelState state, GUIComponent frame)
    {
        if (state.Panel == null) { return; }

        var panelRect = state.Panel.RectTransform;
        Vector2 size = panelRect.RelativeSize;
        float left = 0.5f + panelRect.RelativeOffset.X - size.X * 0.5f;
        float top = 0.5f + panelRect.RelativeOffset.Y - size.Y * 0.5f;

        var rect = frame.RectTransform;
        rect.Anchor = Anchor.TopLeft;
        rect.RelativeSize = size;
        rect.RelativeOffset = new Vector2(left, top);
    }

    private static void RestoreNativeFrames(EtherRecyclerPanelState state)
    {
        foreach (var pair in state.NativeFrames)
        {
            try
            {
                var frame = pair.Key;
                var saved = pair.Value;
                frame.RectTransform.Anchor = saved.Anchor;
                frame.RectTransform.RelativeSize = saved.Size;
                frame.RectTransform.RelativeOffset = saved.Offset;
                frame.Visible = saved.Visible;
                frame.CanBeFocused = saved.Focused;
            }
            catch (Exception ex) { LogError("恢复原生窗格失败：" + ex.Message); }
        }
        foreach (var pair in state.NativeInventories)
        {
            // 原版已经重建挂接时别把旧窗格塞回去。
            if (pair.Key.RectTransform == pair.Value.Custom)
            {
                pair.Key.RectTransform = pair.Value.Native;
                try { pair.Key.CreateSlots(); }
                catch (Exception ex) { LogError("恢复原生槽位失败：" + ex.Message); }
            }
        }
        if (state.InputSlots != null) { state.InputSlots.UserData = null; }
        if (state.CatalystSlots != null) { state.CatalystSlots.UserData = null; }
        if (state.OutputSlots != null) { state.OutputSlots.UserData = null; }
        state.NativeFrames.Clear();
        state.NativeInventories.Clear();
        state.HiddenContainers.Clear();
        state.Buttons = null;
        state.Attached = false;
    }

    private static void SetArea(GUIComponent component, Vector2 offset, Vector2 size, Vector2 panelSize)
    {
        component.RectTransform.Anchor = Anchor.TopLeft;
        component.RectTransform.RelativeSize = new Vector2(size.X / panelSize.X, size.Y / panelSize.Y);
        component.RectTransform.RelativeOffset = new Vector2(offset.X / panelSize.X, offset.Y / panelSize.Y);
    }

    private static void AttachInventory(EtherRecyclerPanelState state, ItemInventory inventory, GUICustomComponent area)
    {
        if (inventory.RectTransform != area.RectTransform)
        {
            state.NativeInventories[inventory] = (inventory.RectTransform, area.RectTransform);
        }
        area.UserData = inventory;
        inventory.RectTransform = area.RectTransform;
        inventory.CreateSlots();
    }

    private static void RepairAttachment(EtherRecyclerPanelState state, GUICustomComponent area)
    {
        if (area.UserData is ItemInventory inventory && inventory.RectTransform != area.RectTransform)
        {
            AttachInventory(state, inventory, area);
        }
    }

    private static void EnsureAttached(EtherRecyclerPanelState state, ItemInventory inventory, GUICustomComponent area, ref Point lastSize, Vector2 targetSize)
    {
        var size = targetSize.ToPoint();
        if (inventory.RectTransform != area.RectTransform || lastSize != size)
        {
            AttachInventory(state, inventory, area);
            lastSize = size;
        }
    }

    // 库存绘制区尺寸：按目标格子边长反推（公式取自 Inventory.CreateSlots 的缩放逻辑）
    private static Vector2 CalcArea(ItemContainer container, float u)
    {
        var inventory = container.Inventory;
        int capacity = Math.Max(inventory.Capacity, 1);
        int perRow = Math.Max(1, Math.Min(container.SlotsPerRow, capacity));
        int rows = (int)Math.Ceiling(capacity / (double)perRow);

        float naturalWidth = perRow * 60f * u + (perRow - 1) * 5f * u + 10f * u;
        float naturalHeight = rows * 60f * u + (rows - 1) * 20f * u + 25f * u;
        float k = SlotSize / (60f * u);
        return new Vector2(naturalWidth * k, naturalHeight * k);
    }

    private static (ItemInventory Input, ItemContainer InputContainer, ItemInventory Catalyst, ItemContainer CatalystContainer, ItemInventory Output, ItemContainer OutputContainer)? GetContainers(Item machine)
    {
        var found = new List<ItemContainer>();
        foreach (var component in machine.Components)
        {
            if (component is ItemContainer container && container.Inventory != null)
            {
                found.Add(container);
                if (found.Count == 3) { break; }
            }
        }
        if (found.Count < 3) { return null; }

        return (found[0].Inventory, found[0], found[1].Inventory, found[1], found[2].Inventory, found[2]);
    }
}
