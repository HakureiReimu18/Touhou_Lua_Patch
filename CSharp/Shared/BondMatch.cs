using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Client = Barotrauma.Networking.Client;

namespace Touhou.Bond
{
    // 天上天下，唯我独尊
    // 服务端说了算：严格 1 对 1、多人只配真人（单人可配 AI）、每巡回只能主动变更一次；
    // 被动断链（下线/摘符）免费解散，配对跨巡回靠桥接文件保留。
    public static class BondMatch
    {
        public static readonly string[] CommandNames = { "bondmatch", "bondbreak", "bondpairs", "bondclear", "bonddbg", "bondcfg" };

        // 有账号 id 就用账号 id，没认证的退化成 "n:" + 名字
        public static string IdOf(Client c)
        {
            try
            {
                string s = c.AccountId.ToString(); // "Some(Steam:7656...)" / "None"
                if (s.StartsWith("Some(", StringComparison.Ordinal) && s.EndsWith(")", StringComparison.Ordinal))
                    return s.Substring(5, s.Length - 6);
            }
            catch { }
            return "n:" + c.Name;
        }

        public static IReadOnlyList<Client> Clients =>
            GameMain.NetworkMember?.ConnectedClients ?? (IReadOnlyList<Client>)Array.Empty<Client>();

        public static Client FindClientById(string id)
        {
            foreach (var c in Clients)
                if (c != null && SafeIdMatches(c, id)) return c;
            return null;
        }

        static bool SafeIdMatches(Client c, string id)
        {
            if (string.Equals(IdOf(c), id, StringComparison.OrdinalIgnoreCase)) return true;
            try { return c.SessionOrAccountIdMatches(id); } catch { return false; }
        }

        // 给 BondNet 跨文件用的防代签入口
        public static bool SafeIdMatchesPublic(Client c, string id) => SafeIdMatches(c, id);

        public static Client FindClientByName(string name)
            => Clients.FirstOrDefault(c => c != null &&
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        // 解析不到就当离线；角色列表加载线程会动，全程 try/catch
        public static Character FindCharacterById(string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return null;
                if (id.StartsWith("bot:", StringComparison.Ordinal))
                {
                    string name = id.Substring(4);
                    return Character.CharacterList.FirstOrDefault(ch => ch != null && !ch.Removed && !ch.IsDead
                        && ch.AIController != null
                        && string.Equals(ch.Name, name, StringComparison.OrdinalIgnoreCase));
                }
                var client = FindClientById(id);
                if (client?.Character != null) return client.Character;
                if (id.StartsWith("host:", StringComparison.Ordinal) &&
                    GameMain.NetworkMember?.IsServer == true &&
                    string.Equals(Character.Controlled?.Name, id.Substring(5), StringComparison.OrdinalIgnoreCase))
                    return Character.Controlled;
            }
            catch { }
            return null;
        }

        public static string IdOfCharacter(Character ch)
        {
            if (ch == null) return null;
            var client = Clients.FirstOrDefault(c => c?.Character == ch);
            if (client != null) return IdOf(client);
            if (ch.AIController != null) return "bot:" + ch.Name;
            if (GameMain.NetworkMember?.IsServer == true && ch == Character.Controlled)
                return "host:" + ch.Name;
            return null;
        }

        // 成功返回 null，否则返回原因；selfCharHint 直连发送者角色，免得 id 绕回匹配自己
        public static string RequestPair(string selfId, string targetId, Character selfCharHint = null)
        {
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer)
                return "仅服务端可执行配对";
            if (string.IsNullOrEmpty(selfId) || string.IsNullOrEmpty(targetId))
                return "身份缺失";
            if (selfId == targetId)
                return "不能与自己绑定";

            var selfCh = selfCharHint ?? FindCharacterById(selfId);
            if (selfCh == null) return "未找到你的角色（你在线吗？）";
            if (BondShare.FindCharm(selfCh) == null) return "你未佩戴绑定护符";

            // 变更判定：在配对中换对象（switching）、解绑后重绑"新对象"（rebind）都要消耗配额；
            // 被动断链后恢复原对象（restoration）免费
            bool switching = BondState.Pairs.ContainsKey(selfId);
            bool restoration = !switching
                && BondState.LastForcedPartner.TryGetValue(selfId, out var forcedPartner)
                && forcedPartner == targetId;
            bool rebind = !switching && !restoration && BondState.PairHistoryThisRound.Contains(selfId);
            if ((switching || rebind) && BondState.SwitchUsedThisRound.Contains(selfId))
                return "本巡回的变更配额已用完（每巡回只能变更一次）";
            if (BondState.Pairs.ContainsKey(targetId))
            {
                var existing = BondState.Pairs[targetId];
                if (!existing.Has(selfId))
                    return $"对方已与 {existing.OtherName(targetId)} 绑定（严格一对一）";
            }

            var targetClient = FindClientById(targetId);
            bool singleplayer = GameMain.NetworkMember == null;
            if (!singleplayer && targetClient == null && !targetId.StartsWith("host:", StringComparison.Ordinal))
                return "多人模式下只能与真实玩家缔结（不允许 AI 挡伤害）";
            if (FindCharacterById(targetId) == null)
                return "目标角色不存在（对方离线或未在艇上？）";

            if (switching) BreakPair(selfId, forced: false);

            string selfName = selfCh.Name;
            string targetName = targetClient?.Name
                ?? FindCharacterById(targetId)?.Name
                ?? targetId;
            var entry = new PairEntry { IdA = selfId, IdB = targetId, NameA = selfName, NameB = targetName };
            BondState.Pairs[selfId] = entry;
            BondState.Pairs[targetId] = entry;
            BondState.PairHistoryThisRound.Add(selfId);
            BondState.PairHistoryThisRound.Add(targetId);
            if (switching || rebind) BondState.SwitchUsedThisRound.Add(selfId);
            BondState.LastForcedPartner.Remove(selfId); // 主动建立后，被动恢复资格失效

            string suffix = restoration ? "（恢复原配对，不消耗配额）"
                : (switching || rebind) ? "（消耗本巡回变更配额）" : "";
            BondLog.Log($"{selfName} ↔ {targetName} 绑定建立{suffix}");
            BondNet.BroadcastPairs();
            SavePairs();
            return null;
        }

        // forced = 被动断链，双方保留"免费恢复原对象"资格
        public static bool BreakPair(string selfId, bool forced, string reason = null)
        {
            if (!BondState.Pairs.TryGetValue(selfId, out var entry)) return false;
            BondState.Pairs.Remove(entry.IdA);
            BondState.Pairs.Remove(entry.IdB);
            if (forced)
            {
                BondState.LastForcedPartner[entry.IdA] = entry.IdB;
                BondState.LastForcedPartner[entry.IdB] = entry.IdA;
            }
            else
            {
                BondState.LastForcedPartner.Remove(selfId);
            }
            string why = forced ? $"（强制解散：{reason ?? "链接失效"}）" : "";
            BondLog.Log($"{entry.NameA} ↔ {entry.NameB} 绑定解除{why}");
            BondNet.BroadcastPairs();
            SavePairs();
            return true;
        }

        // id → 首次发现没戴符的时刻
        static readonly Dictionary<string, double> noCharmSince = new();

        // 本巡回见过的在线 id：只有"在线上过又掉线"才算下线；
        // 服务器刚起时还没连接的视为休眠（配对保留，等其上线），不然开局瞬间全被拆
        static readonly HashSet<string> seenOnlineThisRound = new();

        public static void ResetSeenOnline() => seenOnlineThisRound.Clear();

        // 下线/摘符的强制解散；在不在线看能不能解析到角色
        public static void ValidatePairs(double now)
        {
            foreach (var entry in DistinctPairs().ToList())
            {
                foreach (var id in new[] { entry.IdA, entry.IdB })
                {
                    var ch = FindCharacterById(id);
                    if (ch == null)
                    {
                        if (IsOnline(id)) { seenOnlineThisRound.Add(id); continue; } // 在线但角色未就位（加载/换人）
                        if (seenOnlineThisRound.Contains(id))
                        {
                            // 在线上过又消失 = 真下线，强制解散（不消耗配额）
                            BreakPair(id, forced: true, reason: "对方已下线");
                            seenOnlineThisRound.Remove(id);
                            noCharmSince.Remove(id);
                        }
                        // 从未在线 = 休眠配对（等待上线），保留
                        continue;
                    }
                    seenOnlineThisRound.Add(id);
                    if (!BondState.Wearers.ContainsKey(ch))
                    {
                        if (!noCharmSince.TryGetValue(id, out double since)) noCharmSince[id] = now;
                        else if (now - since > BondConfig.CharmGraceTime)
                        {
                            BreakPair(id, forced: true, reason: "对方已摘下护符");
                            noCharmSince.Remove(id);
                        }
                    }
                    else noCharmSince.Remove(id);
                }
            }
        }

        static bool IsOnline(string id)
        {
            if (id.StartsWith("bot:", StringComparison.Ordinal)) return true; // 单人生效，角色离线即整体不可用
            return FindClientById(id) != null
                || (id.StartsWith("host:", StringComparison.Ordinal) && Character.Controlled != null);
        }

        public static IEnumerable<PairEntry> DistinctPairs()
        {
            var seen = new HashSet<PairEntry>();
            foreach (var kv in BondState.Pairs)
                if (seen.Add(kv.Value)) yield return kv.Value;
        }

        public static List<BondClientState.CandInfo> BuildCandidateList()
        {
            var list = new List<BondClientState.CandInfo>();
            try
            {
                if (GameMain.NetworkMember == null)
                {
                    var me = Character.Controlled;
                    if (me != null)
                        list.Add(new BondClientState.CandInfo { Id = IdOfCharacter(me), Name = me.Name });
                    List<Character> chars;
                    try { chars = Character.CharacterList.ToList(); }
                    catch { chars = null; }
                    if (chars != null)
                        foreach (var ch in chars)
                            if (ch?.AIController != null && !ch.Removed && !ch.IsDead &&
                                !list.Any(x => x.Name == ch.Name))
                                list.Add(new BondClientState.CandInfo { Id = "bot:" + ch.Name, Name = ch.Name });
                }
                else
                {
                    foreach (var c in Clients)
                        if (c?.Character != null)
                            list.Add(new BondClientState.CandInfo { Id = IdOf(c), Name = c.Name });
                    var host = Character.Controlled; // 监听服务器房主兜底（不在 ConnectedClients 时）
                    if (host != null && !list.Any(x => x.Name == host.Name))
                        list.Add(new BondClientState.CandInfo { Id = IdOfCharacter(host), Name = host.Name });
                }
            }
            catch (Exception ex) { BondLog.Warn($"构建候选人清单失败：{ex.Message}"); }
            return list;
        }

        static string SavePath =>
            Path.Combine(BondPlugin.PackageDir, "Data", "bond_match.xml");

        // 存档身份：换存档时旧配对作废（与主插件同款反射路径）
        static readonly PropertyInfo GameSessionDataPathProp =
            typeof(GameMain).Assembly.GetType("Barotrauma.GameSession")
                ?.GetProperty("DataPath", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo CampaignSavePathField =
            GameSessionDataPathProp?.PropertyType.GetField("SavePath", BindingFlags.Public | BindingFlags.Instance);

        static string CurrentSaveId
        {
            get
            {
                try
                {
                    object dp = GameSessionDataPathProp?.GetValue(GameMain.GameSession);
                    return dp == null ? "" : CampaignSavePathField?.GetValue(dp) as string ?? "";
                }
                catch { return ""; }
            }
        }

        public static void SavePairs()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
                var doc = new XDocument(new XElement("BondPairs",
                    new XAttribute("saveid", CurrentSaveId)));
                foreach (var p in DistinctPairs())
                {
                    doc.Root.Add(new XElement("Pair",
                        new XAttribute("ida", p.IdA), new XAttribute("idb", p.IdB),
                        new XAttribute("namea", p.NameA), new XAttribute("nameb", p.NameB)));
                }
                doc.Save(SavePath);
            }
            catch (Exception ex) { BondLog.Warn($"配对存档失败：{ex.Message}"); }
        }

        public static void LoadPairs()
        {
            BondState.Pairs.Clear();
            noCharmSince.Clear();
            try
            {
                if (!File.Exists(SavePath)) return;
                var root = XDocument.Load(SavePath).Root;
                if (root == null) return;
                string fileSaveId = root.Attribute("saveid")?.Value ?? "";
                if (string.IsNullOrEmpty(CurrentSaveId))
                {
                    BondLog.Debug("非战役存档（编辑器/测试场景），不恢复绑定");
                    return;
                }
                if (fileSaveId != CurrentSaveId)
                {
                    BondLog.Debug("配对桥接文件属于另一个存档，丢弃");
                    return;
                }
                int loaded = 0;
                foreach (var el in root.Elements("Pair"))
                {
                    string ida = el.Attribute("ida")?.Value, idb = el.Attribute("idb")?.Value;
                    if (string.IsNullOrEmpty(ida) || string.IsNullOrEmpty(idb)) continue;
                    var entry = new PairEntry
                    {
                        IdA = ida, IdB = idb,
                        NameA = el.Attribute("namea")?.Value ?? ida,
                        NameB = el.Attribute("nameb")?.Value ?? idb
                    };
                    BondState.Pairs[ida] = entry;
                    BondState.Pairs[idb] = entry;
                    loaded++;
                }
                if (loaded > 0) BondLog.Log($"已从桥接文件恢复 {loaded} 对绑定");
            }
            catch (Exception ex) { BondLog.Warn($"配对读档失败：{ex.Message}"); }
        }

        public static void RegisterCommands()
        {
            var game = LuaCsSetup.Instance.Game;

            // bondmatch <目标名> [自己名]：自己名缺省 = 本机控制角色（服务器控制台=房主）
            game.AddCommand("bondmatch", "建立/更换绑定: bondmatch <目标名> [自己名]", args =>
            {
                var a = Touhou.Affixes.Mod.CmdArgs(args);
                if (a.Length < 1) { BondLog.Warn("用法：bondmatch <目标名> [自己名]"); return; }
                string selfName = a.Length > 1 ? a[1] : Character.Controlled?.Name;
                if (string.IsNullOrEmpty(selfName)) { BondLog.Warn("无法确定自己（服务器控制台请指定自己名）"); return; }

                var selfClient = FindClientByName(selfName);
                string selfId = selfClient != null ? IdOf(selfClient)
                    : Character.Controlled?.Name == selfName ? IdOfCharacter(Character.Controlled)
                    : null;
                if (selfId == null) { BondLog.Warn($"找不到「{selfName}」（本指令只服务在线角色）"); return; }

                var targetClient = FindClientByName(a[0]);
                string targetId = targetClient != null ? IdOf(targetClient) : null;
                if (targetId == null && GameMain.NetworkMember == null)
                {
                    var bot = Character.CharacterList.FirstOrDefault(ch => ch?.AIController != null && !ch.Removed
                        && string.Equals(ch.Name, a[0], StringComparison.OrdinalIgnoreCase));
                    if (bot != null) targetId = "bot:" + bot.Name;
                }
                if (targetId == null) { BondLog.Warn($"找不到目标「{a[0]}」"); return; }

                string err = RequestPair(selfId, targetId);
                if (err != null) BondLog.Warn($"配对失败：{err}");
            }, null, false);

            game.AddCommand("bondbreak", "解除绑定: bondbreak [自己名]", args =>
            {
                var a = Touhou.Affixes.Mod.CmdArgs(args);
                string selfName = a.Length > 0 ? a[0] : Character.Controlled?.Name;
                var selfClient = FindClientByName(selfName ?? "");
                string selfId = selfClient != null ? IdOf(selfClient)
                    : Character.Controlled?.Name == selfName ? IdOfCharacter(Character.Controlled)
                    : null;
                if (selfId == null) { BondLog.Warn("找不到该角色"); return; }
                if (!BreakPair(selfId, forced: false)) BondLog.Warn("该角色当前没有绑定");
            }, null, false);

            game.AddCommand("bondpairs", "列出当前全部绑定", _ =>
            {
                var pairs = DistinctPairs().ToList();
                if (pairs.Count == 0) { BondLog.Log("当前没有任何绑定"); return; }
                BondLog.Log($"绑定共 {pairs.Count} 对：");
                foreach (var p in pairs)
                    BondLog.Log($"  {p.NameA} ↔ {p.NameB}（{p.IdA} / {p.IdB}）");
            }, null, false);

            game.AddCommand("bondclear", "清除全部绑定（含桥接存档；测试编辑器残留数据用）", _ =>
            {
                int n = BondState.Pairs.Count / 2;
                BondState.Pairs.Clear();
                BondState.SwitchUsedThisRound.Clear();
                SavePairs();          // 立即写空档，防止 roundEnd 把旧对写回去
                BondNet.BroadcastPairs();
                BondLog.Log($"已清除 {n} 对绑定");
            }, null, false);

            game.AddCommand("bonddbg", "切换绑定结算调试日志（调试图表数据）", _ =>
            {
                BondConfig.Debug = !BondConfig.Debug;
                BondConfig.Save();
                BondLog.Log($"调试日志：{(BondConfig.Debug ? "开" : "关")}");
            }, null, false);

            game.AddCommand("bondcfg", "查看/修改绑定配置: bondcfg [键] [值]（无参=列出全部）", args =>
            {
                var a = Touhou.Affixes.Mod.CmdArgs(args);
                if (a.Length == 0)
                {
                    BondLog.Log($"settleinterval={BondConfig.SettleInterval}  resistancescale={BondConfig.ResistanceScale}");
                    BondLog.Log($"maxlinkdistance={BondConfig.MaxLinkDistance}  powerdref={BondConfig.PowerDRef}  powern={BondConfig.PowerN}");
                    BondLog.Log($"minshare={BondConfig.MinShare}  maxshare={BondConfig.MaxShare}  debug={BondConfig.Debug}");
                    return;
                }
                if (a.Length < 2 || !BondConfig.Set(a[0], a[1]))
                { BondLog.Warn("未知配置键"); return; }
                BondLog.Log($"{a[0]} = {a[1]}（已写盘）");
            }, null, false);
        }
    }
}
