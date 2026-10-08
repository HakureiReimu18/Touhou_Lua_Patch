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
//  · 两个按钮仍用 XML 的原生 CustomInterface 窗格（负责客户端→服务端事件 + LuaHook），只是被摆进面板右下角；
//  · 三条触发路径互为兜底：Item.DrawHUD 补丁（设备 HUD 显示时每帧走到）、CharacterHUD.Update 补丁（收起面板）、
//    LuaCs 的 think 钩子（Harmony 万一没生效也能工作）；
//  · 失败安全：出任何异常都吞掉、恢复原生窗口、停用面板；面板没接管时设备照 XML 排版工作。
//
// 调布局就改 PanelConfig；尺寸单位是 1080p 下的界面像素，其他分辨率/UI 缩放自动换算。

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
                bool drawHudPatched = false;
                bool hudUpdatePatched = false;

                // 主触发（可选）：设备 HUD 展示期间每帧都会走 Item.DrawHUD（CharacterHUD.Draw → SelectedItem.DrawHUD）
                var drawHud = AccessTools.Method(typeof(Item), "DrawHUD", new[] { typeof(SpriteBatch), typeof(Barotrauma.Camera), typeof(Character) });
                if (drawHud != null)
                {
                    harmony.Patch(drawHud, postfix: new HarmonyMethod(AccessTools.Method(typeof(EtherRecyclerPanelPlugin), nameof(AfterItemDrawHUD))));
                    drawHudPatched = true;
                }

                // 辅触发（可选）：本地玩家 HUD 每帧更新一次，用来收起面板
                var hudUpdate = AccessTools.Method(typeof(CharacterHUD), "Update", new[] { typeof(float), typeof(Character), typeof(Barotrauma.Camera) });
                if (hudUpdate != null)
                {
                    harmony.Patch(hudUpdate, postfix: new HarmonyMethod(AccessTools.Method(typeof(EtherRecyclerPanelPlugin), nameof(AfterCharacterHUDUpdate))));
                    hudUpdatePatched = true;
                }

                // 兜底：LuaCs 每帧 think 钩子（Harmony 两个补丁都没挂上时，靠它整条链路也能跑）
                LuaCsSetup.Instance.Hook.Add("think", "Touhou.EtherRecyclerPanel.Think", OnThink);
                // 主驱动：客户端 Lua 每帧把「选中物品」交过来（Lua 链路已验证可用，最可靠）
                LuaCsSetup.Instance.Hook.Add("Touhou.EtherRecyclerPanel.Sync", "Touhou.EtherRecyclerPanel", OnLuaSync);

                patched = true;
                EtherRecyclerPanel.Log($"面板插件已加载（DrawHUD 补丁 {(drawHudPatched ? "OK" : "未命中")}，CharacterHUD 补丁 {(hudUpdatePatched ? "OK" : "未命中")}，Lua 驱动 + think 兜底已注册）");
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
        harmony?.UnpatchSelf();
        patched = false;
        EtherRecyclerPanel.ResetAll();
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

    private static void AfterItemDrawHUD(Item __instance)
    {
        try { EtherRecyclerPanel.ShowFor(__instance); } catch (Exception ex) { EtherRecyclerPanel.Disable(ex); }
    }

    private static void AfterCharacterHUDUpdate(Character character)
    {
        try { EtherRecyclerPanel.HideIfNotApplicable(character); } catch (Exception ex) { EtherRecyclerPanel.Disable(ex); }
    }
}

internal sealed class EtherRecyclerPanelState
{
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
    public GUIComponent HiddenButtonFrame;
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
    private static readonly Point OffScreenOffset = new Point(-20000, -20000);
    private const bool Verbose = false; // 面板提示日志（加载/接管/驱动接通）默认关闭
    private static bool disabled;
    private static bool firstTickLogged;
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
        foreach (var panelState in States.Values)
        {
            try { RestoreNativeFrames(panelState); } catch { /* 收尾失败无所谓 */ }
            try { GUI.RemoveFromUpdateList(panelState.Panel, true); } catch { /* 同上 */ }
            try { panelState.Panel?.Parent?.RemoveChild(panelState.Panel); } catch { /* 同上 */ }
        }
        States.Clear();
    }

    // 兜底路径：每帧从本地玩家当前选中的物品判断（LuaCs think 钩子调用）
    public static void Tick()
    {
        if (disabled) { return; }
        if (!firstTickLogged)
        {
            firstTickLogged = true;
            Log("每帧同步已启动");
        }

        var machine = GetRecycler(Character.Controlled?.SelectedItem);
        if (machine == null) { HideAll(); return; }
        ShowFor(machine);
    }

    // 主路径：客户端 Lua 每帧把选中物品交过来（可能为 nil）
    public static void SyncFromLua(Item item)
    {
        if (disabled) { return; }
        if (!firstTickLogged)
        {
            firstTickLogged = true;
            Log("Lua 驱动已接通（每帧同步）");
        }

        var machine = GetRecycler(item);
        if (machine == null) { HideAll(); return; }
        ShowFor(machine);
    }

    // 主路径：设备 HUD 正在显示时被调用（Item.DrawHUD 补丁）
    public static void ShowFor(Item machine)
    {
        if (disabled) { return; }
        if (GetRecycler(machine) == null) { return; }

        var state = GetOrCreate(machine);
        if (state == null || state.Failed) { return; }

        bool needsRebuild = state.Panel == null
            || state.LastScreenW != GameMain.GraphicsWidth
            || state.LastScreenH != GameMain.GraphicsHeight
            || Math.Abs(state.LastUIScale - Inventory.UIScale) > 0.0001f;

        if (needsRebuild)
        {
            Build(machine, state);
            if (!firstPanelLogged)
            {
                firstPanelLogged = true;
                Log("已接管以太回收机面板（格子 " + SlotSize + "px）");
            }
        }

        Layout(machine, state);
    }

    public static void HideIfNotApplicable(Character character)
    {
        if (disabled) { return; }
        if (GetRecycler(character?.SelectedItem) == null) { HideAll(); }
    }

    private static void HideAll()
    {
        foreach (var panelState in States.Values)
        {
            if (panelState.Panel != null)
            {
                panelState.Panel.Visible = false;
                try { GUI.RemoveFromUpdateList(panelState.Panel, true); } catch { /* 没在列表里也无所谓 */ }
            }
        }
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

        var state = new EtherRecyclerPanelState();
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

    // 面板建立后第一次布局：定尺寸、接管库存、隐藏原生窗口、把按钮窗格挪进来
    private static void Build(Item machine, EtherRecyclerPanelState state)
    {
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
        state.HiddenButtonFrame = buttonComponent?.GuiFrame;        HideNativeFrames(state);

        AttachInventory(containers.Value.Input, state.InputSlots);
        AttachInventory(containers.Value.Catalyst, state.CatalystSlots);
        AttachInventory(containers.Value.Output, state.OutputSlots);
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
        EnsureAttached(containers.Value.Input, state.InputSlots, ref state.LastInputSize, input);
        EnsureAttached(containers.Value.Catalyst, state.CatalystSlots, ref state.LastCatalystSize, catalyst);
        EnsureAttached(containers.Value.Output, state.OutputSlots, ref state.LastOutputSize, output);

        // 原版每帧会把被选中物品的容器/按钮窗口重新显示出来，这里持续压住
        HideNativeFrames(state);

        bool running = machine.GetComponent<LightComponent>()?.IsOn ?? false;
        state.Status.Text = TextManager.Get(running ? "touhou.recycler.isactive" : "touhou.recycler.panel.idle");
        state.StartButton.Text = TextManager.Get(running ? "fabricatorcancel" : "touhou.recycler.start");
        state.Prediction.Text = BuildPrediction(containers.Value.Input);

        // 暂停菜单等模态 UI 打开时把面板收起来（避免盖在暂停菜单上面）
        bool modalOpen = false;
        try { modalOpen = GUI.PauseMenu is GUIComponent pause && pause.Visible; } catch { /* 取不到就算了 */ }
        if (modalOpen)
        {
            state.Panel.Visible = false;
            try { GUI.RemoveFromUpdateList(state.Panel, true); } catch { /* 无所谓 */ }
            return;
        }

        state.Panel.Visible = true;

        // 关键：独立 GUIFrame 必须每帧注册进 GUI 更新列表才会被绘制/交互
        //（先例：本模组 CSharp/Shared/BondGui.cs 的 WindowTick；原版组件则是经 Item HUD 注册）
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
        // 不可见 + 不可聚焦 + 挪到屏幕外 + 从 GUI 更新列表里摘掉（原版每帧又会注册，所以我每帧摘）
        foreach (var container in state.HiddenContainers)
        {
            if (container?.GuiFrame is GUIComponent frame)
            {
                HideFrame(frame);
            }
        }
        if (state.Buttons?.GuiFrame != null)
        {
            // 每帧按组件重新取窗格：原版在分辨率变更时会重建它，存的引用会失效
            HideFrame(state.Buttons.GuiFrame);
        }
    }

    private static void HideFrame(GUIComponent frame)
    {
        frame.Visible = false;
        frame.CanBeFocused = false;
        frame.RectTransform.AbsoluteOffset = OffScreenOffset;
        try { GUI.RemoveFromUpdateList(frame, true); } catch { /* 不在列表里也无所谓 */ }
    }

    private static void RestoreNativeFrames(EtherRecyclerPanelState state)
    {
        foreach (var container in state.HiddenContainers)
        {
            if (container?.GuiFrame is GUIComponent frame)
            {
                RestoreFrame(frame);
            }
        }
        if (state.Buttons?.GuiFrame != null)
        {
            RestoreFrame(state.Buttons.GuiFrame);
        }
        state.HiddenContainers.Clear();
    }

    private static void RestoreFrame(GUIComponent frame)
    {
        frame.Visible = true;
        frame.CanBeFocused = true;
        frame.RectTransform.AbsoluteOffset = Point.Zero;
    }

    private static void SetArea(GUIComponent component, Vector2 offset, Vector2 size, Vector2 panelSize)
    {
        component.RectTransform.Anchor = Anchor.TopLeft;
        component.RectTransform.RelativeSize = new Vector2(size.X / panelSize.X, size.Y / panelSize.Y);
        component.RectTransform.RelativeOffset = new Vector2(offset.X / panelSize.X, offset.Y / panelSize.Y);
    }

    private static void AttachInventory(ItemInventory inventory, GUICustomComponent area)
    {
        area.UserData = inventory;
        inventory.RectTransform = area.RectTransform;
        inventory.CreateSlots();
    }

    private static void EnsureAttached(ItemInventory inventory, GUICustomComponent area, ref Point lastSize, Vector2 targetSize)
    {
        var size = targetSize.ToPoint();
        if (inventory.RectTransform != area.RectTransform || lastSize != size)
        {
            AttachInventory(inventory, area);
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
