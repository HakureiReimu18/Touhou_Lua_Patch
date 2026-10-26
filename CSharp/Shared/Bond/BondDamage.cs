using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.Bond
{
    // 计量挂在 Character.ApplyAttack（受击伤害的唯一漏斗）：前缀快照受害者各 affliction 强度，
    // 后缀重读一次，差值就是这次攻击真实入账的量。装备/药物 DoT 没攻击者天然不计，多段命中每段单独算。
    // 飞了

    public static class BondMeterPatch
    {
        // 按 prefab 全量加总（快照/存量/钳制都走这个入口）
        public static float StockOf(Character c, AfflictionPrefab prefab)
        {
            float s = 0f;
            foreach (var a in c.CharacterHealth.GetAllAfflictions())
                if (a.Prefab == prefab) s += a.Strength;
            return s;
        }

        sealed class MeterSnapshot
        {
            public readonly List<AfflictionPrefab> Prefabs = new List<AfflictionPrefab>(4);
            public float[] Before = new float[8];
        }

        // 别用 Harmony 的 __state：同一次调用里所有补丁共享它，会重复计量
        static readonly MeterSnapshot pending = new MeterSnapshot();
        static bool pendingValid;
        static readonly MeterSnapshot lastConsumed = new MeterSnapshot();
        static bool lastConsumedValid;

        // 快照内容一致 = 同一次计量被重复执行（补丁重复注册时去重）
        static bool SameSnapshot(MeterSnapshot a, MeterSnapshot b)
        {
            if (a.Prefabs.Count != b.Prefabs.Count) return false;
            for (int i = 0; i < a.Prefabs.Count; i++)
                if (a.Prefabs[i] != b.Prefabs[i] || a.Before[i] != b.Before[i]) return false;
            return true;
        }

        static void CopySnapshot(MeterSnapshot from, MeterSnapshot to)
        {
            to.Prefabs.Clear();
            to.Prefabs.AddRange(from.Prefabs);
            if (to.Before == null || to.Before.Length < from.Before.Length)
                to.Before = new float[from.Before.Length];
            Array.Copy(from.Before, to.Before, from.Before.Length);
        }

        static MethodBase TargetMethod()
        {
            var m = typeof(Character).GetMethod("ApplyAttack", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) BondLog.Warn("BondMeterPatch: Character.ApplyAttack 未找到");
            return m;
        }

        static void Prefix(Character __instance, Character attacker, Attack attack, Limb targetLimb)
        {
            try
            {
                if (!Touhou.Affixes.Mod.IsGameplayAuthority) return; // 只跑单上下文，listen server 双程序集防双计
                var wearers = BondState.Wearers;
                if (wearers.Count == 0) return;
                if (__instance == null || __instance.IsDead || __instance.Removed) return;
                if (!wearers.TryGetValue(__instance, out var charm) || charm == null || charm.Removed) return;
                // 只计受击：装备/药物/环境的持续伤害没有攻击者（或是自己），一律不计量
                if (attacker == null || attacker == __instance) return;
                var affs = attack?.Afflictions;                    // Dictionary<Affliction, XElement>
                if (affs == null) return;

                pendingValid = false;
                var counted = BondConfig.CountedTypeSet;
                pending.Prefabs.Clear();
                foreach (var aff in affs.Keys)
                {
                    if (aff == null || aff.Strength <= 0f) continue;
                    string t = aff.Prefab.AfflictionType.Value;
                    if (!counted.Contains(t))
                    {
                        // 排查"这个 affliction 类型到底叫啥"用，Debug 开着才打
                        if (BondConfig.Debug)
                        {
                            if (!uncountedLogged.TryGetValue(t, out double tAt) || tAt < Timing.TotalTime)
                            {
                                uncountedLogged[t] = Timing.TotalTime + 10.0;
                                BondLog.Debug($"未计入的 affliction 类型：{t}（{aff.Prefab.Identifier}）——要转移请 bondcfg countedtypes 加入");
                            }
                        }
                        continue;
                    }
                    // 同 prefab 多条目要去重：快照差按 prefab 总量算，重复条目会多计
                    if (pending.Prefabs.Contains(aff.Prefab)) continue;
                    pending.Prefabs.Add(aff.Prefab);
                }
                if (pending.Prefabs.Count == 0) return;

                int n = pending.Prefabs.Count;
                if (pending.Before == null || pending.Before.Length < n)
                    pending.Before = new float[n];
                for (int i = 0; i < n; i++)
                    pending.Before[i] = StockOf(__instance, pending.Prefabs[i]);
                pendingValid = true;
            }
            catch (Exception ex)
            {
                // 热路径，计量挂了也不能崩局
                BondLog.Warn($"计量前缀抑制了异常：{ex.Message}");
            }
        }

        static void Postfix(Character __instance, Limb targetLimb)
        {
            if (!pendingValid) return;
            try
            {
                if (lastConsumedValid && SameSnapshot(pending, lastConsumed)) return;
                lastConsumedValid = true;
                CopySnapshot(pending, lastConsumed);
                pendingValid = false;

                LimbType lt = targetLimb != null ? targetLimb.type : LimbType.Torso;
                bool added = false;
                for (int i = 0; i < pending.Prefabs.Count; i++)
                {
                    var prefab = pending.Prefabs[i];
                    float delta = StockOf(__instance, prefab) - pending.Before[i];
                    if (delta <= 0.0001f) continue;
                    if (!BondState.Meters.TryGetValue(__instance, out var meter))
                        BondState.Meters[__instance] = meter = new DamageMeter();
                    meter.Add(lt, prefab, delta);
                    added = true;
                }
                // 结算表头打调用次数，一发多次 = 补丁堆叠了
                if (added && BondState.Meters.TryGetValue(__instance, out var m2)) m2.Calls++;
            }
            catch (Exception ex)
            {
                BondLog.Warn($"计量后缀抑制了异常：{ex.Message}");
            }
        }

        // 类型 → 下次允许打日志的时刻，节流用
        static readonly Dictionary<string, double> uncountedLogged = new();
    }


    public static class BondTickerPatch
    {
        static MethodBase TargetMethod()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var m = typeof(GameMain).GetMethod("Update", flags);
            if (m != null) return m;
            var serverType = typeof(GameMain).Assembly.GetType("Barotrauma.Networking.GameServer");
            m = serverType?.GetMethod("Update", flags);
            if (m == null) BondLog.Warn("BondTickerPatch: 未找到每帧 Update 入口");
            return m;
        }

        static void Postfix() => BondTicker.Tick();
    }

    public static class BondTicker
    {
        static double nextSettle;

        public static void Tick()
        {
            double now = Timing.TotalTime;
            if (now < nextSettle) return;
            nextSettle = now + Math.Max(BondConfig.SettleInterval, 0.1);

            try
            {
                if (!Touhou.Affixes.Mod.IsGameplayAuthority) return;
                // 回合开始后延迟补推配对状态（客户端镜像刷新）
                if (BondNet.PendingStatePushAt > 0 && now >= BondNet.PendingStatePushAt)
                {
                    BondNet.PendingStatePushAt = 0;
                    BondNet.BroadcastPairs();
                }
                if (BondState.Pairs.Count == 0) { BondState.Wearers.Clear(); return; }

                RebuildWearers();
                BondMatch.ValidatePairs(now);
                if (BondState.Pairs.Count == 0) return;

                foreach (var pair in BondMatch.DistinctPairs().ToList())
                {
                    var a = BondMatch.FindCharacterById(pair.IdA);
                    var b = BondMatch.FindCharacterById(pair.IdB);
                    if (a == null || b == null) continue; // 离线：解散归 ValidatePairs 管
                    if (a.IsDead || b.IsDead) continue;   // 死亡窗口不划转
                    SettleOneWay(pair, a, b, now);
                    SettleOneWay(pair, b, a, now);
                }
            }
            catch (Exception ex)
            {
                BondLog.Warn($"结算抑制了异常：{ex.Message}");
            }
        }

        static void RebuildWearers()
        {
            List<Character> chars;
            try { chars = Character.CharacterList.ToList(); }
            catch { return; } // 加载线程正在改角色列表，这轮先跳过
            BondState.Wearers.Clear();
            foreach (var ch in chars)
            {
                if (ch == null || ch.Removed || ch.IsDead || ch.Inventory == null) continue;
                var charm = BondShare.FindCharm(ch);
                if (charm != null) BondState.Wearers[ch] = charm;
            }
            foreach (var key in BondState.Meters.Keys.ToList())
                if (key == null || key.Removed || key.IsDead || !BondState.Wearers.ContainsKey(key))
                    BondState.Meters.Remove(key);
        }

        // 把 victim 本窗口的入账按 offload 逐 (肢体, prefab) 转给 partner
        static void SettleOneWay(PairEntry pair, Character victim, Character partner, double now)
        {
            if (!BondState.Wearers.TryGetValue(victim, out var vCharm) ||
                !BondState.Wearers.TryGetValue(partner, out var pCharm)) return;
            if (!BondState.Meters.TryGetValue(victim, out var meter) || !meter.Any) return;

            if (BondConfig.MaxLinkDistance > 0 &&
                Vector2.Distance(victim.WorldPosition, partner.WorldPosition) > BondConfig.MaxLinkDistance)
            {
                BondLog.Debug($"{victim.Name} → {partner.Name} 超出链接距离，本窗口不划转");
                meter.Clear();
                return;
            }

            // fv = victim 的保留份额；fp = partner 的接收意愿；offload = 实际划转率
            float fv = BondShare.Share(vCharm, victim, meter.Total, now);
            float fp = BondShare.Share(pCharm, partner, meter.Total, now);
            float offload = MathHelper.Clamp(Math.Min(1f - fv, fp), 0f, BondConfig.MaxShare);

            float curTotal = 0f;
            if (BondConfig.Debug)
                foreach (var a in victim.CharacterHealth.GetAllAfflictions())
                    if (a.Prefab != null && BondConfig.CountedTypeSet.Contains(a.Prefab.AfflictionType.Value))
                        curTotal += a.Strength;

            var header = $"{victim.Name}→{partner.Name} 保留f={fv:0.##} 接收f={fp:0.##} 划转率={offload:0.##} " +
                         $"窗口原始={meter.Total:0.#} 受害者当前存量={curTotal:0.#} 计量调用={meter.Calls}次";
            if (offload <= 0.001f)
            {
                BondLog.Debug(header + " → 划转率 0，跳过");
                meter.Clear();
                return;
            }

            float vp = Math.Max(partner.CharacterHealth.MaxVitality, 1f);

            BondLog.Debug(header);
            // 逐 (命中肢体, prefab) 转移到对方对应肢体；预算 = 该 prefab 全身存量 × offload，计量失真也不会超转
            var budgets = new Dictionary<AfflictionPrefab, float>();
            var stocks = new Dictionary<AfflictionPrefab, float>();

            foreach (var kv in meter.Entries)
            {
                LimbType lt = kv.Key.Limb;
                AfflictionPrefab prefab = kv.Key.Prefab;
                float raw = kv.Value;

                if (!stocks.TryGetValue(prefab, out float stock))
                {
                    stock = BondMeterPatch.StockOf(victim, prefab);
                    stocks[prefab] = stock;
                    budgets[prefab] = stock * offload;
                }
                float moved = Math.Min(raw * offload, budgets[prefab]);
                if (moved <= 0.01f) continue;
                budgets[prefab] -= moved;

                var vLimb = FindLimb(victim, lt);
                var pLimb = FindLimb(partner, lt) ?? partner.AnimController?.MainLimb;

                // 目标肢体抗性按可调系数折算后，反推 ApplyAffliction 的输入量
                float r = partner.CharacterHealth.GetResistance(prefab, lt);
                float eff = MathHelper.Clamp(r * BondConfig.ResistanceScale, 0f, 0.95f);
                float applied = Math.Min(moved * (vp / 100f) / Math.Max(1f - eff, 0.05f), prefab.MaxStrength);
                float storedOnPartner = applied > 0.01f ? applied * (100f / vp) * (1f - r) : 0f;

                if (prefab.LimbSpecific)
                {
                    if (applied > 0.01f && pLimb != null)
                        partner.CharacterHealth.ApplyAffliction(pLimb, new Affliction(prefab, applied),
                            allowStacking: true, ignoreUnkillability: false, recalculateVitality: true);
                    // 命中肢体优先扣，不够扣的溢出全身摊（守恒兜底）
                    if (vLimb != null)
                    {
                        float stockBefore = BondMeterPatch.StockOf(victim, prefab);
                        victim.CharacterHealth.ReduceAfflictionOnLimb(vLimb, prefab.Identifier, moved);
                        float leftover = moved - (stockBefore - BondMeterPatch.StockOf(victim, prefab));
                        if (leftover > 0.01f)
                            victim.CharacterHealth.ReduceAfflictionOnAllLimbs(prefab.Identifier, leftover);
                    }
                    else
                    {
                        victim.CharacterHealth.ReduceAfflictionOnAllLimbs(prefab.Identifier, moved);
                    }
                }
                else
                {
                    if (applied > 0.01f)
                        partner.CharacterHealth.ApplyAffliction(partner.AnimController?.MainLimb,
                            new Affliction(prefab, applied), true, false, true);
                    victim.CharacterHealth.ReduceAfflictionOnAllLimbs(prefab.Identifier, moved);
                }

                BondLog.Debug($"  [{prefab.Identifier}/{lt}] 入账{raw:0.#} 存量{stock:0.#} 转移{moved:0.#} " +
                              $"目标抗性{r:0.##}×{BondConfig.ResistanceScale:0.##}={eff:0.##} 实收{storedOnPartner:0.#}");
            }

            victim.CharacterHealth.RecalculateVitality();
            meter.Clear();
        }

        static Limb FindLimb(Character c, LimbType type)
        {
            var limbs = c.AnimController?.Limbs;
            if (limbs == null) return null;
            foreach (var l in limbs)
                if (l != null && l.type == type && !l.IsSevered) return l;
            return null;
        }
    }
}
