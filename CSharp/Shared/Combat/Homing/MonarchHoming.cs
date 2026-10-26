using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.Homing
{
    /// <summary>
    /// 君王追踪（Round01 定位鱼叉 + Round02 追踪弹）。
    /// 行为规格照抄 Lua 版（Lua/Scripts/Server/Disabled/Touhou_Monarch.lua，文件留在磁盘但已停止加载）——
    /// 两套同时跑会双重转向，所以迁移后 Lua 那半边不再进游戏。
    /// 在 Lua 规格之上加了两条：引导释放（贴到 3 米就交棒给弹道，不再绕圈）和穿身补伤兜底，见 §4.6。
    /// </summary>
    public static class MonarchTracker
    {
        const string LauncherId = "Touhou_Monarch";
        const string Round01Id = "Touhou_Monarch_Round01";
        const string Round02Id = "Touhou_Monarch_Round02";

        // Lua 的 magic numbers，转向部分一个不改
        const float MinimumSpeed = 7f;
        const float AccelerationMagnitude = 0.25f;
        const float SteeringMagnitude = 5f;
        const float AcquireCone = (float)(30.0 * Math.PI / 180.0); // math.rad(30)
        const float AcquireRange = 2000f;
        static readonly float AcquireConeCosSqr =
            (float)(Math.Cos(AcquireCone) * Math.Cos(AcquireCone));
        const float AcquireRangeSqr = AcquireRange * AcquireRange;
        const double Round02RegisterDelay = 0.1; // Lua 的 Timer.Wait(..., 100) 毫秒

        // 新增的两条，跟通用系统的默认值对齐：300 = 3 米交棒，100 = 1 米内算贴脸穿过
        const float ReleaseDist = 300f;
        const float PassHitDist = 100f;

        sealed class Round
        {
            public Item Item;
            public Item Harpoon; // null 表示改用鼠标引导
            public Character Shooter;
            public Entity Target;            // 当前追的东西（鱼叉或人），用来判断换目标
            public float MinDist = float.MaxValue; // 对当前目标的最近距离
            public bool Released;            // 已交棒给弹道，不再施加任何冲量
        }

        sealed class PendingRound
        {
            public Item Item;
            public Item Harpoon;
            public Character Shooter;
            public double RegisterAt;
        }

        static readonly Dictionary<Item, Item> lastHarpoon = new Dictionary<Item, Item>(); // 发射武器 -> Round01
        static readonly List<Round> rounds = new List<Round>();
        static readonly List<PendingRound> pending = new List<PendingRound>();
        static readonly Dictionary<Character, Character> targetCache = new Dictionary<Character, Character>();

        public static void Clear()
        {
            lastHarpoon.Clear();
            rounds.Clear();
            pending.Clear();
            targetCache.Clear();
        }

        public static int ActiveCount => rounds.Count;

        /// <summary>Projectile.Shoot postfix 入口。</summary>
        public static void OnProjectileShot(Projectile proj, Character user)
        {
            Item item = proj?.Item;
            if (item == null) return;
            string id = item.Prefab.Identifier.Value;
            bool isRound01 = id == Round01Id;
            if (!isRound01 && id != Round02Id) return;
            if (user == null) return;
            // 服务端权威：纯客户端上下文不登记（与 HomingLaunchCommon 同规则）
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) return;

            // 发射武器 = 手里最后一件（照 Lua 的 for value in user.HeldItems 写法）
            Item weapon = null;
            foreach (Item held in user.HeldItems) weapon = held;
            if (weapon == null || weapon.Prefab.Identifier.Value != LauncherId) return;

            if (isRound01)
            {
                lastHarpoon[weapon] = item;
                return;
            }

            // Round02 出膛帧的位置/刚体还没稳，延迟 0.1 秒再登记：存进 pending，Tick 里到点才处理
            Item harpoon = null;
            if (lastHarpoon.TryGetValue(weapon, out Item recorded) && recorded != null && !recorded.Removed)
            {
                if (HarpoonOwnerDead(recorded))
                {
                    lastHarpoon.Remove(weapon); // 鱼叉插的怪已经死了：清掉这条，这发按"没有鱼叉"走鼠标引导
                }
                else
                {
                    harpoon = recorded;
                }
            }
            pending.Add(new PendingRound
            {
                Item = item,
                Harpoon = harpoon,
                Shooter = user,
                RegisterAt = Timing.TotalTime + Round02RegisterDelay,
            });
        }

        /// <summary>
        /// 鱼叉插住的生物是不是死了/没了（复用穿身补伤那套受害者反查）。
        /// 插在墙/结构上解析不出角色，不算死亡——那种情况维持原样继续追鱼叉。
        /// </summary>
        static bool HarpoonOwnerDead(Item harpoon)
        {
            Character victim = HomingFallbackHit.ResolveVictim(harpoon);
            return victim != null && (victim.IsDead || victim.Removed);
        }

        /// <summary>把 lastHarpoon 里指向这根鱼叉的记录清掉（字典很小，按值扫一遍就够）。</summary>
        static void ForgetHarpoon(Item harpoon)
        {
            Item staleKey = null;
            foreach (KeyValuePair<Item, Item> kv in lastHarpoon)
            {
                if (kv.Value == harpoon) { staleKey = kv.Key; break; }
            }
            if (staleKey != null) lastHarpoon.Remove(staleKey);
        }

        public static void Tick()
        {
            if (rounds.Count == 0 && pending.Count == 0) return;
            if (GameMain.GameSession == null) return;
            double now = Timing.TotalTime;

            // 每帧开头清空目标解算缓存：同一帧内多个射手/多枚弹复用结果，跨帧必须重算（目标会动、会死）
            targetCache.Clear();

            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PendingRound p = pending[i];
                if (now < p.RegisterAt) continue;
                pending[i] = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                if (p.Item.Removed) continue;                                 // 弹已经没了
                if (p.Harpoon != null && p.Harpoon.Removed) continue;         // 有鱼叉但发射后即刻失效：保持原行为，不追踪
                rounds.Add(new Round { Item = p.Item, Harpoon = p.Harpoon, Shooter = p.Shooter });
            }

            if (rounds.Count == 0) return;

            for (int i = rounds.Count - 1; i >= 0; i--)
            {
                Round r = rounds[i];
                Item item = r.Item;
                if (item.Removed || item.body == null)
                {
                    // 没刚体就没法转向（刚出膛刚体未建好 / 正在解体）：直接移出，否则下面取成员会每帧报错
                    RemoveAt(i);
                    continue;
                }

                // 鱼叉插住的生物死了：放弃这根鱼叉，转回鼠标引导找新目标（同一帧就走下面的鼠标分支）。
                // 引导状态要一起清：已经释放过的弹不会再转向，MinDist 留着旧目标的最小距离会误判穿身补伤。
                if (r.Harpoon != null && HarpoonOwnerDead(r.Harpoon))
                {
                    ForgetHarpoon(r.Harpoon);
                    r.Harpoon = null;
                    r.Target = null;
                    r.MinDist = float.MaxValue;
                    r.Released = false;
                }

                Vector2 targetPosition;
                Entity targetEntity;
                if (r.Harpoon != null)
                {
                    if (r.Harpoon.Removed)
                    {
                        RemoveAt(i); // 鱼叉半路没了就不追了（保持原行为）
                        continue;
                    }
                    targetEntity = r.Harpoon;
                    targetPosition = r.Harpoon.WorldPosition;
                }
                else
                {
                    Character target = FindMouseTarget(r.Shooter);
                    if (target == null) continue;
                    targetEntity = target;
                    targetPosition = target.WorldPosition;
                }

                Vector2 roundPosition = item.WorldPosition;
                Vector2 toTarget = targetPosition - roundPosition;
                float targetDistance = toTarget.Length();

                // 穿身补伤兜底（同通用追踪）：这一帧比历史最近距离还远 → 已经飞过最近点。
                // 最近点在 1 米以内还没打出伤害，就是引擎没登记命中（坐标系失配的幽灵弹等），
                // 按伤害入口补一刀再移除。HiddenInGame 是引擎登记命中后立刻置位的标志，
                // 它 true 时绝不能补，否则就是双倍伤害。
                if (targetEntity != r.Target)
                {
                    r.Target = targetEntity; // 换目标重新量最近距离
                    r.MinDist = float.MaxValue;
                }
                if (targetDistance > r.MinDist)
                {
                    if (r.MinDist <= PassHitDist && !item.HiddenInGame)
                    {
                        HomingFallbackHit.Apply(item, r.Shooter, targetEntity);
                        RemoveAt(i);
                        continue;
                    }
                }
                else
                {
                    r.MinDist = targetDistance;
                }

                // 引导释放：进到 3 米以内就交棒给弹道，此后不再施加任何冲量。
                // 一直转向会让弹贴着鱼叉/目标绕圈，引擎反而碰不上，永远没伤害。
                if (!r.Released && targetDistance <= ReleaseDist) r.Released = true;
                if (r.Released) continue;

                PhysicsBody body = item.body; // 上面已判过非 null
                Vector2 velocity = body.LinearVelocity;
                float speed = velocity.Length();
                // 零向量：Lua 的 atan2(0, 0) = 0，等价于方向朝正东
                Vector2 roundDirection = speed > 0f ? velocity / speed : new Vector2(1f, 0f);
                Vector2 targetDirection = targetDistance > 0f ? toTarget / targetDistance : new Vector2(1f, 0f);

                if (speed < MinimumSpeed)
                    body.ApplyLinearImpulse(roundDirection * (AccelerationMagnitude * (MinimumSpeed - speed)));
                body.ApplyLinearImpulse((targetDirection - roundDirection) * SteeringMagnitude);
            }
        }

        static void RemoveAt(int index)
        {
            rounds[index] = rounds[rounds.Count - 1];
            rounds.RemoveAt(rounds.Count - 1);
        }

        /// <summary>
        /// 没有鱼叉就靠鼠标引导：取准星方向锥角里最近的非本阵营角色。
        /// 必须用 CursorWorldPosition——潜艇里 CursorPosition 是艇内相对坐标，跟世界坐标一减，瞄准方向会一直偏。
        /// </summary>
        static Character FindMouseTarget(Character shooter)
        {
            if (shooter == null || shooter.Removed) return null;

            // 同一帧同一射手只解算一次（Tick 每帧开头清空本表）
            if (targetCache.TryGetValue(shooter, out Character cached)) return cached;
            HomingProfiler.Scans++;

            // 每个实体只取一次 WorldPosition，之后一律拿 X/Y 做标量运算，不反复生成 Vector2
            Vector2 shooterPosition = shooter.WorldPosition;
            float shooterX = shooterPosition.X, shooterY = shooterPosition.Y;
            Vector2 cursorPosition = shooter.CursorWorldPosition;
            float aimX = cursorPosition.X - shooterX, aimY = cursorPosition.Y - shooterY;
            float aimLengthSqr = aimX * aimX + aimY * aimY;
            if (aimLengthSqr == 0f)
            {
                // 准星和射手位置完全重合：原实现 atan2(0, 0) = 0，等价于基准方向朝正东
                aimX = 1f; aimY = 0f; aimLengthSqr = 1f;
            }

            Character bestTarget = null;
            float bestCosSqr = AcquireConeCosSqr;
            var list = Character.CharacterList;
            for (int i = 0; i < list.Count; i++)
            {
                Character c = list[i];
                if (c == null || c.Removed || c.IsDead || c == shooter || c.TeamID == shooter.TeamID) continue;

                Vector2 position = c.WorldPosition;
                float dx = position.X - shooterX, dy = position.Y - shooterY;
                float distanceSqr = dx * dx + dy * dy;
                if (distanceSqr > AcquireRangeSqr) continue;
                if (distanceSqr == 0f)
                {
                    // 和射手位置完全重合：原实现 get_direction((0, 0)) = 0，等价于朝向正东
                    dx = 1f; dy = 0f; distanceSqr = 1f;
                }
                float dot = dx * aimX + dy * aimY;
                // dot > 0 把夹角锁在 90° 以内，cos² 在该区间单调递减，
                // 于是 cos²(夹角) > cos²(最优角) 与原来的 angle < best_angle 同序同号
                if (dot > 0f && dot * dot > bestCosSqr * distanceSqr * aimLengthSqr)
                {
                    bestCosSqr = dot * dot / (distanceSqr * aimLengthSqr);
                    bestTarget = c;
                }
            }

            targetCache[shooter] = bestTarget;
            return bestTarget;
        }
    }

    /// <summary>Projectile.Shoot postfix：手持发射入口（君王 Round01/02 走这里）。</summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Shoot))]
    public static class MonarchShootPatch
    {
        static void Postfix(Projectile __instance, Character user)
        {
            MonarchTracker.OnProjectileShot(__instance, user);
        }
    }
}
