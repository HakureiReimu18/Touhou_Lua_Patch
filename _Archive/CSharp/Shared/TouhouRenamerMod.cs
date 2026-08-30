// 改名台（Touhou_Renamer）原生实现
//
// 从 Lua 迁移的原因：旧 Lua 版本需要每 60 think 帧全量遍历 Item.ItemList（C# 列表的
// 跨语言迭代开销极大），主机开服时还要在服务端/客户端两个 Lua 环境各扫一遍，
// 表现为每隔固定时间卡顿一次（性能图 Max 尖峰）。
//
// 本实现完全事件驱动，没有任何定时巡检：
//   - Item.get_Name 前缀补丁：缓存命中即纳秒级查表；
//   - Item.AddTag / RemoveTag 补丁：改名即时生效、外部擦除自动补回、落盘随事件触发；
//   - ConditionalWeakTable 做缓存：物品销毁后条目由 GC 自动回收，无需人工清理。
//
// 标签协议（与 Lua 侧 Lua/Scripts/Sever/Touhou_Renamer.lua 约定，存档兼容旧版）：
//   threname:<转义后的名字>  —— 重命名标签（%、逗号、换行需转义）
//   threname_cleared         —— 重置标记（见到后清缓存、删文件记录并自动移除自身）
//
// 文件记录（Data/Saves 或存档目录下 Touhou_Renamer_Data.txt）：
//   "潜艇名|物品ID=转义后的名字"，格式与旧 Lua 版一致；游戏重启读档后按 ID 恢复并补回标签。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace Touhou.Renamer;

public sealed class RenamerPlugin : IAssemblyPlugin
{
    private Harmony harmony;
    private bool patched;

    public void Initialize()
    {
        PatchIfNeeded();
    }

    public void OnLoadCompleted()
    {
        PatchIfNeeded();
    }

    public void PreInitPatching()
    {
    }

    public void Dispose()
    {
        if (patched)
        {
            harmony?.UnpatchSelf();
            patched = false;
        }
        RenamerState.Shutdown();
    }

    private void PatchIfNeeded()
    {
        if (patched) return;

        // 主机开服时服务端/客户端两个程序集会在同一进程加载。
        // 只让先到的程序集真正补丁，后到的发现已有本插件 ID 的补丁就直接跳过。
        var existing = Harmony.GetPatchInfo(typeof(Item).GetMethod("get_Name"));
        if (existing != null && existing.Owners.Contains(RenamerState.HarmonyId))
        {
            patched = true;
            return;
        }

        RenamerState.Initialize();
        harmony = new Harmony(RenamerState.HarmonyId);
        harmony.PatchAll();
        patched = true;
    }
}

internal sealed class RenamerNameState
{
    public string StoredName;
    public bool HasRename;
}

internal static class RenamerState
{
    internal const string HarmonyId = "touhou.renamer";

    private const string TagPrefix = "threname:";
    private const string ClearedTag = "threname_cleared";
    private const string DataFileName = "Touhou_Renamer_Data.txt";

    // 物品实例 -> 名字状态；弱引用表，物品销毁后由 GC 自动回收
    private static readonly ConditionalWeakTable<Item, RenamerNameState> nameCache = new();

    // 文件记录："潜艇名|物品ID" -> 存储用完整名字（含颜色代码）
    private static readonly Dictionary<string, string> fileRecords = new();

    // 插件内部增删标签时抑制事件补丁，防止重入
    private static bool suppressTagEvents;

    private static bool initialized;

    internal static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        LoadFileRecords();
    }

    internal static void Shutdown()
    {
        initialized = false;
    }

    // ---------------- 编码（与旧 Lua 版本一致，保证存档兼容） ----------------

    private static string EncodeName(string s)
    {
        return s.Replace("%", "%25").Replace(",", "%2C").Replace("\n", "%0A").Replace("\r", "");
    }

    private static string DecodeName(string s)
    {
        return s.Replace("%0A", "\n").Replace("%2C", ",").Replace("%25", "%");
    }

    // ---------------- 对外入口（供补丁调用） ----------------

    // get_Name 前缀：返回 null 表示无自定义名字，放行原逻辑
    internal static string GetStoredName(Item item)
    {
        if (item == null || item.Removed) return null;

        if (nameCache.TryGetValue(item, out var state))
        {
            return state.HasRename ? state.StoredName : null;
        }

        // 首次见到该物品：扫描标签（同会话/读档恢复）
        foreach (Identifier tag in item.GetTags())
        {
            string value = tag.Value;
            if (value == null || !value.StartsWith(TagPrefix)) continue;

            string stored = DecodeName(value.Substring(TagPrefix.Length));
            if (TryConvertLegacyV2(stored, out string converted))
            {
                // 特效版残留标签：重写为当前静态格式，完成自清理
                stored = converted;
                RewriteTag(item, value, TagPrefix + EncodeName(stored));
            }
            SetState(item, stored);
            return stored;
        }

        // 标签没有（游戏重启后标签未随存档保存）：查文件记录按物品 ID 恢复并补回标签
        string key = RecordKey(item);
        if (key != null && fileRecords.TryGetValue(key, out string fileStored))
        {
            AddTagInternal(item, TagPrefix + EncodeName(fileStored));
            // AddTagInternal 走抑制通道不会触发事件补丁，这里手动写入缓存
            SetState(item, fileStored);
            return fileStored;
        }

        // 负缓存：已确认无自定义名字
        SetState(item, null);
        return null;
    }

    internal static void OnTagAdded(Item item, string tagValue)
    {
        if (suppressTagEvents || item == null || tagValue == null) return;

        if (tagValue == ClearedTag)
        {
            // 重置标记：清缓存、删文件记录，并移除标记自身
            SetState(item, null);
            DeleteFileRecord(item);
            RemoveTagInternal(item, new Identifier(ClearedTag));
            return;
        }

        if (!tagValue.StartsWith(TagPrefix)) return;

        string stored = DecodeName(tagValue.Substring(TagPrefix.Length));
        if (TryConvertLegacyV2(stored, out string converted))
        {
            stored = converted;
        }

        // 状态未变化（例如 string/Identifier 两个重载先后触发），直接跳过
        if (nameCache.TryGetValue(item, out var state) && state.HasRename && state.StoredName == stored)
        {
            return;
        }

        SetState(item, stored);
        SaveFileRecord(item, stored);

        // 清掉同一物品上的其他重命名标签（旧格式残留、外部擦除后补回的旧标签等），防止累积
        suppressTagEvents = true;
        try
        {
            foreach (Identifier tag in item.GetTags())
            {
                string v = tag.Value;
                if (v != null && v.StartsWith(TagPrefix) && v != tagValue)
                {
                    item.RemoveTag(tag);
                }
            }
        }
        finally
        {
            suppressTagEvents = false;
        }
    }

    internal static void OnTagRemoved(Item item, string tagValue)
    {
        if (suppressTagEvents || item == null || tagValue == null) return;
        if (tagValue == ClearedTag) return;
        if (!tagValue.StartsWith(TagPrefix)) return;

        if (!nameCache.TryGetValue(item, out var state) || !state.HasRename) return;

        // 被移除的不是当前名字对应的标签（旧格式残留等），不干预
        if (tagValue != TagPrefix + EncodeName(state.StoredName)) return;

        // 外部效果把重命名标签抹掉了：按缓存补回
        AddTagInternal(item, TagPrefix + EncodeName(state.StoredName));
    }

    // ---------------- 内部工具 ----------------

    private static void SetState(Item item, string storedName)
    {
        nameCache.Remove(item);
        nameCache.Add(item, new RenamerNameState
        {
            StoredName = storedName,
            HasRename = storedName != null
        });
    }

    private static void AddTagInternal(Item item, string tag)
    {
        suppressTagEvents = true;
        try { item.AddTag(tag); }
        finally { suppressTagEvents = false; }
    }

    private static void RemoveTagInternal(Item item, Identifier tag)
    {
        suppressTagEvents = true;
        try { item.RemoveTag(tag); }
        finally { suppressTagEvents = false; }
    }

    private static void RewriteTag(Item item, string oldTagValue, string newTagValue)
    {
        suppressTagEvents = true;
        try
        {
            item.RemoveTag(new Identifier(oldTagValue));
            item.AddTag(newTagValue);
        }
        finally { suppressTagEvents = false; }
    }

    // 兼容特效版残留（v2|特效|R|G|B|A|名字）：还原为静态颜色名
    private static bool TryConvertLegacyV2(string payload, out string converted)
    {
        converted = null;
        if (!payload.StartsWith("v2|")) return false;

        string[] parts = payload.Split('|');
        if (parts.Length < 7) return false;
        if (!int.TryParse(parts[2], out int r) || !int.TryParse(parts[3], out int g) ||
            !int.TryParse(parts[4], out int b) || !int.TryParse(parts[5], out int a))
        {
            return false;
        }

        string name = string.Join("|", parts.Skip(6));
        converted = (r == 255 && g == 255 && b == 255)
            ? name
            : $"‖color:{r},{g},{b},{a}‖{name}‖color:end‖";
        return true;
    }

    // ---------------- 文件持久化层 ----------------

    private static string GetDataPath()
    {
        try
        {
            string folder = SaveUtil.DefaultSaveFolder;
            if (!string.IsNullOrEmpty(folder))
            {
                return Path.Combine(folder, DataFileName);
            }
        }
        catch
        {
            // 存档目录不可用时走兜底路径
        }
        return Path.Combine("Data", "Saves", DataFileName);
    }

    private static void LoadFileRecords()
    {
        try
        {
            string path = GetDataPath();
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int sep = line.IndexOf('=');
                if (sep <= 0) continue;
                fileRecords[line.Substring(0, sep)] = DecodeName(line.Substring(sep + 1));
            }
        }
        catch (Exception e)
        {
            DebugConsole.Log("[Touhou.Renamer] 读取改名记录失败：" + e.Message);
        }
    }

    private static void SaveFileRecords()
    {
        try
        {
            var lines = fileRecords.Select(kv => kv.Key + "=" + EncodeName(kv.Value));
            File.WriteAllLines(GetDataPath(), lines.ToArray());
        }
        catch (Exception e)
        {
            DebugConsole.Log("[Touhou.Renamer] 写入改名记录失败：" + e.Message);
        }
    }

    // 当前潜艇名（区分不同存档/战役，降低 ID 撞名概率）
    private static string RecordKey(Item item)
    {
        try
        {
            string subName = Submarine.MainSub?.Info?.Name ?? "unknown";
            return subName + "|" + item.ID;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveFileRecord(Item item, string storedName)
    {
        string key = RecordKey(item);
        if (key == null) return;
        if (fileRecords.TryGetValue(key, out string existing) && existing == storedName) return;
        fileRecords[key] = storedName;
        SaveFileRecords();
    }

    private static void DeleteFileRecord(Item item)
    {
        string key = RecordKey(item);
        if (key == null) return;
        if (fileRecords.Remove(key))
        {
            SaveFileRecords();
        }
    }
}

[HarmonyPatch(typeof(Item), "get_Name")]
internal static class ItemNamePatch
{
    private static bool Prefix(Item __instance, ref string __result)
    {
        string stored = RenamerState.GetStoredName(__instance);
        if (stored == null) return true;
        __result = stored;
        return false;
    }
}

[HarmonyPatch(typeof(Item), "AddTag", typeof(string))]
internal static class ItemAddTagStringPatch
{
    private static void Postfix(Item __instance, string tag)
    {
        RenamerState.OnTagAdded(__instance, tag);
    }
}

[HarmonyPatch(typeof(Item), "AddTag", typeof(Identifier))]
internal static class ItemAddTagIdentifierPatch
{
    private static void Postfix(Item __instance, Identifier tag)
    {
        RenamerState.OnTagAdded(__instance, tag.Value);
    }
}

[HarmonyPatch(typeof(Item), "RemoveTag", typeof(Identifier))]
internal static class ItemRemoveTagPatch
{
    private static void Postfix(Item __instance, Identifier tag)
    {
        RenamerState.OnTagRemoved(__instance, tag.Value);
    }
}
