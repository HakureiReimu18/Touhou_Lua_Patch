#if CLIENT
using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Touhou.Bond
{
    // 好爽好爽好爽好爽好爽好爽好爽好爽好爽好爽好爽好爽好爽
    // 绑定面板：选人 → 确认缔结，配过对的置灰；快捷键读配置文件，默认 L
    public static class BondGui
    {
        static GUIFrame window;
        static GUIListBox candList, pairList;
        static GUITextBlock statusText, denyText;
        static int seenVersion = -1;
        static string selectedId;
        static bool pendingRefresh;        // 鼠标按住期间的刷新请求延迟到松开（防重建吃掉点击）
        static bool hotkeyWasDown, escWasDown; // 原始按键边沿检测（KeyHit 在固定步长下会一帧双触发）
        // 候选人按钮引用，选中态靠原地改文字，不重建列表（重建会吃掉点击）
        static readonly List<(GUIButton btn, string id, string name)> candButtons = new();

        // 快捷键读东方快捷键系统的配置文件，默认 L，2 秒轮询 mtime 热更新；鼠标键也认
        static Keys hotkey = Keys.L;
        static string hotkeyMouse;
        static string hotkeyPath;
        static DateTime hotkeyMtime;
        static double nextHotkeyReload;

        public static bool IsOpen => window != null;

        public static void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        static void Open()
        {
            Close();
            window = new GUIFrame(new RectTransform(new Vector2(0.36f, 0.68f), GUI.Canvas, Anchor.Center), "ItemUI");

            var layout = new GUILayoutGroup(new RectTransform(new Vector2(0.94f, 0.95f), window.RectTransform, Anchor.Center))
            {
                RelativeSpacing = 0.015f,
                Stretch = true
            };

            // 行高预算（含间距须 < 1.0）：0.065+0.05+0.042+0.038+0.28+0.038+0.12+0.07+0.07+0.07+9×0.015 ≈ 0.98
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.065f), layout.RectTransform),
                RichString.Rich("绑定面板", null))
            { TextAlignment = Alignment.Center };

            statusText = new GUITextBlock(new RectTransform(new Vector2(1f, 0.05f), layout.RectTransform),
                RichString.Rich("", null))
            { TextAlignment = Alignment.Center, TextColor = new Color(160, 255, 170) };

            denyText = new GUITextBlock(new RectTransform(new Vector2(1f, 0.042f), layout.RectTransform),
                RichString.Rich("", null))
            { TextAlignment = Alignment.Center, TextColor = new Color(255, 160, 80) };

            new GUITextBlock(new RectTransform(new Vector2(1f, 0.038f), layout.RectTransform),
                RichString.Rich("— 选择绑定对象（点击选中，再按确认） —", null))
            { TextAlignment = Alignment.Center, TextColor = new Color(180, 200, 255) };

            candList = new GUIListBox(new RectTransform(new Vector2(1f, 0.28f), layout.RectTransform));

            new GUITextBlock(new RectTransform(new Vector2(1f, 0.038f), layout.RectTransform),
                RichString.Rich("— 现有绑定 —", null))
            { TextAlignment = Alignment.Center, TextColor = new Color(180, 200, 255) };

            pairList = new GUIListBox(new RectTransform(new Vector2(1f, 0.12f), layout.RectTransform));

            // 三个按钮各占一行居中（横排曾因间距计算溢出右框）
            var btnConfirm = new GUIButton(new RectTransform(new Vector2(0.5f, 0.07f), layout.RectTransform, Anchor.Center), "确认绑定");
            btnConfirm.OnClicked = (btn, obj) =>
            {
                BondLog.Debug($"确认绑定点击：selectedId={(selectedId ?? "null")}");
                if (selectedId == null)
                {
                    denyText.Text = RichString.Rich("⚠ 请先在上方列表中选择一个对象", null);
                    BondClientState.LastDeniedAt = Timing.TotalTime;
                    return true;
                }
                BondNet.RequestPair(selectedId);
                selectedId = null;
                RefreshLists(); // 数据在 SP 同步就绪；MP 由随后的广播 Version 刷新兜底
                return true;
            };

            var btnBreak = new GUIButton(new RectTransform(new Vector2(0.5f, 0.07f), layout.RectTransform, Anchor.Center), "解除我的绑定");
            btnBreak.OnClicked = (btn, obj) =>
            {
                selectedId = null;
                BondNet.RequestBreak();
                RefreshLists();
                return true;
            };

            var btnClose = new GUIButton(new RectTransform(new Vector2(0.5f, 0.07f), layout.RectTransform, Anchor.Center), "关闭");
            btnClose.OnClicked = (btn, obj) => { Close(); return true; };

            RefreshLists();
            BondNet.RequestCandidates();
        }

        public static void Close()
        {
            if (window == null) return;
            try
            {
                GUI.RemoveFromUpdateList(window, true);
                window.RectTransform.Parent = null;
                GUI.PreventPauseMenuToggle = false;
            }
            catch { }
            window = null;
            candList = null;
            pairList = null;
            statusText = null;
            denyText = null;
            candButtons.Clear();
        }

        // 镜像变了就重建两个列表，窗口本体不动
        static void RefreshLists()
        {
            if (window == null) return;
            try
            {
                string myName = Character.Controlled?.Name;

                // 状态行附带佩戴提示（每次刷新算一次，不在每帧路径上）
                bool wearing = Character.Controlled != null && BondShare.FindCharm(Character.Controlled) != null;
                statusText.Text = RichString.Rich(
                    (string.IsNullOrEmpty(BondClientState.MyPartnerName)
                        ? "当前没有绑定对象"
                        : $"当前绑定对象：{BondClientState.MyPartnerName}")
                    + (wearing ? "" : "（未佩戴绑定护符）"), null);

                bool denyFresh = !string.IsNullOrEmpty(BondClientState.LastDenied) &&
                                 Timing.TotalTime - BondClientState.LastDeniedAt < 10.0;
                denyText.Text = RichString.Rich(denyFresh ? "⚠ " + BondClientState.LastDenied : "", null);

                candList.Content.ClearChildren();
                candButtons.Clear();
                bool any = false;
                foreach (var c in BondClientState.Candidates)
                {
                    if (c.Name == myName) continue;
                    any = true;
                    string pairedWith = null;
                    foreach (var p in BondClientState.Pairs)
                    {
                        if (p.NameA == c.Name) { pairedWith = p.NameB; break; }
                        if (p.NameB == c.Name) { pairedWith = p.NameA; break; }
                    }
                    if (pairedWith != null)
                    {
                        new GUITextBlock(
                            new RectTransform(new Vector2(1f, 0.14f), candList.Content.RectTransform),
                            RichString.Rich($"{c.Name}  — 已与 {pairedWith} 绑定", null))
                        { TextAlignment = Alignment.Center, TextColor = Color.Gray };
                    }
                    else
                    {
                        // 点击只选中（» 标记），确认按钮才发请求，防误触
                        string id = c.Id;
                        string name = c.Name;
                        var btn = new GUIButton(
                            new RectTransform(new Vector2(1f, 0.14f), candList.Content.RectTransform),
                            selectedId == id ? $"» {name} «" : name);
                        btn.OnClicked = (b, obj) =>
                        {
                            selectedId = selectedId == id ? null : id;
                            BondLog.Debug($"面板选中：{(selectedId == null ? "（已取消）" : name)}");
                            foreach (var cb in candButtons)
                                cb.btn.Text = cb.id == selectedId ? $"» {cb.name} «" : cb.name;
                            return true;
                        };
                        candButtons.Add((btn, id, name));
                    }
                }
                if (!any)
                {
                    new GUITextBlock(new RectTransform(new Vector2(1f, 0.14f), candList.Content.RectTransform),
                        RichString.Rich("（暂无候选人——单人模式需有 AI 船员）", null))
                    { TextAlignment = Alignment.Center, TextColor = Color.Gray };
                }

                pairList.Content.ClearChildren();
                if (BondClientState.Pairs.Count == 0)
                {
                    new GUITextBlock(new RectTransform(new Vector2(1f, 0.3f), pairList.Content.RectTransform),
                        RichString.Rich("（无）", null))
                    { TextAlignment = Alignment.Center, TextColor = Color.Gray };
                }
                foreach (var p in BondClientState.Pairs)
                {
                    new GUITextBlock(new RectTransform(new Vector2(1f, 0.3f), pairList.Content.RectTransform),
                        RichString.Rich($"{p.NameA}  ↔  {p.NameB}", null))
                    { TextAlignment = Alignment.Center, TextColor = new Color(160, 255, 170) };
                }
            }
            catch (Exception ex) { BondLog.Warn($"面板刷新抑制了异常：{ex.Message}"); }
        }

        static void EnsureHotkey()
        {
            double now = Timing.TotalTime;
            if (now < nextHotkeyReload) return;
            nextHotkeyReload = now + 2.0;
            try
            {
                if (hotkeyPath == null)
                {
                    string baseDir = null;
                    try { baseDir = SaveUtil.DefaultSaveFolder; } catch { }
                    hotkeyPath = !string.IsNullOrEmpty(baseDir)
                        ? Path.Combine(baseDir, "TouhouModHotkeyConfig.txt")
                        : Path.Combine("Data", "Saves", "TouhouModHotkeyConfig.txt");
                }
                if (!File.Exists(hotkeyPath)) return;
                var mtime = File.GetLastWriteTime(hotkeyPath);
                if (mtime == hotkeyMtime) return;
                hotkeyMtime = mtime;
                foreach (var line in File.ReadAllLines(hotkeyPath))
                {
                    if (!line.StartsWith("bondpanel.key=", StringComparison.Ordinal)) continue;
                    string keyName = line.Substring("bondpanel.key=".Length).Trim();
                    hotkeyMouse = null;
                    if (keyName.Length > 0 && Enum.TryParse(keyName, out Keys k))
                    {
                        hotkey = k;
                    }
                    else if (IsMouseButtonName(keyName))
                    {
                        hotkeyMouse = keyName;
                    }
                    break;
                }
            }
            catch { }
        }

        static bool IsMouseButtonName(string name)
        {
            switch (name)
            {
                case "MouseMiddle":
                case "MouseSide1": case "MouseSide2":
                    return true;
                default: return false;
            }
        }

        static bool MouseButtonHeld(string name)
        {
            var ms = Mouse.GetState();
            switch (name)
            {
                case "MouseMiddle": return ms.MiddleButton == ButtonState.Pressed;
                case "MouseSide1":  return ms.XButton1 == ButtonState.Pressed;
                case "MouseSide2":  return ms.XButton2 == ButtonState.Pressed;
                default: return false;
            }
        }

        /// <summary>鼠标是否停在打开的绑定面板上（防止鼠标键热键把点击面板当成关窗）</summary>
        static bool IsMouseOverOpenPanel()
        {
            var over = GUI.MouseOn;
            if (over == null || window == null) return false;
            for (var rt = over.RectTransform; rt != null; rt = rt.Parent)
                if (rt.GUIComponent == window) return true;
            return false;
        }

        [HarmonyPatch]
        public static class BondGuiFramePatch
        {
            static System.Reflection.MethodBase TargetMethod()
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                var m = typeof(GameMain).GetMethod("Update", flags);
                if (m == null) BondLog.Warn("BondGuiFramePatch: GameMain.Update 未找到");
                return m;
            }

            static void Postfix()
            {
                try
                {
                    // 窗口开着必须每帧重新注册，否则不绘制
                    if (BondGui.window != null)
                    {
                        GUI.PreventPauseMenuToggle = true;   // 窗口开着时 Esc 归我们管，不弹暂停菜单
                        BondGui.window.AddToGUIUpdateList(false, 1);
                        if (BondClientState.Version != BondGui.seenVersion)
                        {
                            BondGui.seenVersion = BondClientState.Version;
                            if (PlayerInput.PrimaryMouseButtonHeld()) BondGui.pendingRefresh = true;
                            else BondGui.RefreshLists();
                        }
                        else if (BondGui.pendingRefresh && !PlayerInput.PrimaryMouseButtonHeld())
                        {
                            BondGui.pendingRefresh = false;
                            BondGui.RefreshLists();
                        }
                        bool escDown = PlayerInput.GetKeyboardState.IsKeyDown(Keys.Escape);
                        if (escDown && !BondGui.escWasDown) { BondGui.escWasDown = true; BondGui.Close(); return; }
                        BondGui.escWasDown = escDown;
                    }

                    EnsureHotkey();
                    if (GUI.KeyboardDispatcher.Subscriber != null) return; // 聊天框/输入框激活时不触发
                    if (GameMain.Instance != null && GameMain.Instance.Paused) return; // 1.12.7 里 Paused 是实例属性

                    // 不用 PlayerInput.KeyHit：固定步长帧卡顿时一帧会跑两次，双触发 = 开+关秒关
                    bool keyDown = hotkeyMouse != null
                        ? MouseButtonHeld(hotkeyMouse)
                        : PlayerInput.GetKeyboardState.IsKeyDown(hotkey);
                    if (keyDown && !BondGui.hotkeyWasDown)
                    {
                        if (BondGui.IsOpen)
                        {
                            // 鼠标在面板内时不响应（热键为鼠标键时，点击面板不会把窗口关掉）
                            if (!IsMouseOverOpenPanel()) BondGui.Close();
                        }
                        else
                        {
                            BondGui.Toggle(); // 不检测穿戴：面板随时可开，缔结由服务端校验护符
                        }
                    }
                    BondGui.hotkeyWasDown = keyDown;
                }
                catch { }
            }
        }
    }
    // 绑定/耐久设置 ↔ Lua 设置页的文件桥接：
    // 状态快照（当前值 + 拒绝反馈）写 TouhouBondState.txt 供 Lua 读取；
    // Lua 页面「保存」写 TouhouBondRequest.txt，本侧每帧查 mtime，经 BondNet.SendCfgSet
    // 走原有网络/权限路径（单人直改，多人服务器验证权限后全船同步）。
    public static class BondSettingsBridge
    {
        static string statePath, requestPath, flagPath;
        static DateTime requestMtime;
        static int seenVersion = -1;
        static string lastDenied;
        static double lastDeniedAt = -1;
        static bool lastDeniedFresh;
        static double nextCfgRequest;

        static void ResolvePaths()
        {
            if (statePath != null) return;
            string baseDir = null;
            try { baseDir = SaveUtil.DefaultSaveFolder; } catch { }
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.Combine("Data", "Saves");
            statePath = Path.Combine(baseDir, "TouhouBondState.txt");
            requestPath = Path.Combine(baseDir, "TouhouBondRequest.txt");
            flagPath = Path.Combine(baseDir, "TouhouModHotkeyConfig.txt");
        }

        public static void Tick()
        {
            try
            {
                ResolvePaths();

                // 定期拉取配置镜像（单人本地直接同步；多人 C→S 请求，回包驱动状态重写）
                double now = Timing.TotalTime;
                if (now >= nextCfgRequest)
                {
                    nextCfgRequest = now + 5.0;
                    BondNet.RequestCfgState();
                }

                // Lua 页面「保存」写入的请求文件
                if (File.Exists(requestPath))
                {
                    var mtime = File.GetLastWriteTime(requestPath);
                    if (mtime != requestMtime)
                    {
                        requestMtime = mtime;
                        var values = new Dictionary<string, string>();
                        foreach (var line in File.ReadAllLines(requestPath))
                        {
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            string key = line.Substring(0, eq).Trim();
                            string value = line.Substring(eq + 1).Trim();
                            if (key.Length > 0) values[key] = value;
                        }
                        if (values.Count > 0) BondNet.SendCfgSet(values);
                    }
                }

                // 状态快照：镜像版本、拒绝信息变化、或拒绝信息新鲜度过期（10s）时尝试重写
                bool denyFresh = !string.IsNullOrEmpty(BondClientState.LastDenied) &&
                                 Timing.TotalTime - BondClientState.LastDeniedAt < 10.0;
                if (BondClientState.Version != seenVersion ||
                    BondClientState.LastDenied != lastDenied ||
                    BondClientState.LastDeniedAt != lastDeniedAt ||
                    denyFresh != lastDeniedFresh)
                {
                    seenVersion = BondClientState.Version;
                    lastDenied = BondClientState.LastDenied;
                    lastDeniedAt = BondClientState.LastDeniedAt;
                    lastDeniedFresh = denyFresh;
                    WriteState();
                }            }
            catch { }
        }

        static void WriteState()
        {
            try
            {
                string Get(string k, string def) =>
                    BondClientState.CfgValues.TryGetValue(k, out var v) ? v : def;
                // denied 只写 10 秒内的（新鲜度判断放在 C# 侧：Lua 的多人客户端上下文里没有 Timing）
                bool denyFresh = !string.IsNullOrEmpty(BondClientState.LastDenied) &&
                                 Timing.TotalTime - BondClientState.LastDeniedAt < 10.0;
                var lines = new List<string>
                {
                    "settleinterval=" + Get("settleinterval", BondConfig.SettleInterval.ToString("0.##")),
                    "resistancescale=" + Get("resistancescale", BondConfig.ResistanceScale.ToString("0.##")),
                    "maxlinkdistance=" + Get("maxlinkdistance", BondConfig.MaxLinkDistance.ToString("0.##")),
                    "countedtypes=" + Get("countedtypes", BondConfig.CountedTypes),
                    "condlossmult=" + Get("condlossmult", BondConfig.CondLossMult.ToString("0.##")),
                    "denied=" + (denyFresh ? BondClientState.LastDenied : ""),
                    "deniedat=" + BondClientState.LastDeniedAt.ToString("0.###"),
                };
                // 内容没变不写盘：mtime 变化会让 Lua 页轮询重建，打断玩家输入
                string content = string.Join("\n", lines);
                if (File.Exists(statePath) && File.ReadAllText(statePath) == content) return;
                File.WriteAllText(statePath, content);
            }
            catch { }
        }

        // 加载时清掉上个会话残留的开窗标记（旧版 C# 窗口时代的 open_*_settings 行）
        public static void CleanStaleFlags()
        {
            try
            {
                ResolvePaths();
                if (!File.Exists(flagPath)) return;
                var kept = new List<string>();
                bool changed = false;
                foreach (var line in File.ReadAllLines(flagPath))
                {
                    string t = line.Trim();
                    if (t == "open_bond_settings=1" || t == "open_condloss_settings=1" || t == "open_hudbar_settings=1") { changed = true; continue; }
                    kept.Add(line);
                }
                if (changed) File.WriteAllLines(flagPath, kept);
            }
            catch { }
        }

        [HarmonyPatch]
        public static class BondSettingsPatch
        {
            static System.Reflection.MethodBase TargetMethod()
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                var m = typeof(GameMain).GetMethod("Update", flags);
                if (m == null) BondLog.Warn("BondSettingsPatch: GameMain.Update 未找到");
                return m;
            }

            static void Postfix()
            {
                try
                {
                    BondSettingsBridge.Tick();
                }
                catch { }
            }
        }
    }
}
#endif
