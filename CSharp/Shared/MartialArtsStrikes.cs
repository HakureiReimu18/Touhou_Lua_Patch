using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.MartialArts
{
    /// <summary>武术系统插件入口。</summary>
    public sealed class MartialArtsPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public void Initialize()
        {
            harmony = new Harmony("touhou.martialarts");
        }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone) return;
            oneTimeInitDone = true;

            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(StrikeDrivePatch));
            harmony.PatchAll(typeof(StrikeUsePatch));
            harmony.PatchAll(typeof(StrikeMeleePosePatch));
            LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.MartialArts.RoundEnd", OnRoundEnd);
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.MartialArts.RoundEnd");
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
            StrikeManager.ClearAll();
            StrikeManager.RestoreInjections();
        }

        object OnRoundEnd(object[] args)
        {
            StrikeManager.ClearAll();
            return null;
        }
    }

    /// <summary>
    /// 通用武术招式系统（第六轮）。
    ///
    /// 动作层：
    /// ① 拳类 = HoldItem 运动学曲线（道具沿瞄准方向伸缩，手臂 IK 跟随）——已验证稳定好用。
    /// ② 蹬腿 = 原生肢体攻击管线（Limb.attack 注入 + UpdateAttack 力驱动）——
    ///    最初验证"特效正常"的版本。只叠两个明确的自旋修复：扭矩为 0、
    ///    每帧把脚部拉力关节锚点钉到脚心（UpdateAttack 的冲量施加点是该锚点，
    ///    原版站立时它停在地面形成杠杆 → 自旋爆炸的根因）。不叠任何其他干预：
    ///    无道具跟随、无 Disabled、无轨迹、无收势驱动（原生收招本来就稳）。
    ///
    /// 命中层：命中窗口内距离判定（出招肢体 HitRadius 内的敌方肢体），隔墙检查沿用
    /// vanilla 模式；伤害用武器 MeleeWeapon.Attack 结算（affliction 原版格式，
    /// 倍率 (1+MeleeAttackMultiplier)×(1+品质威力)）。
    ///
    /// 攻速层：reloadTimer = max(武器reload, 招式全长) ÷ (1+MeleeAttackSpeed) ÷ (1+品质攻速)。
    ///
    /// 操作：右键（瞄准）= 架势；瞄准中左键 = 出招。测试键 J=蹬腿 K=直拳。
    /// </summary>
    public static class StrikeManager
    {
        // ---------------- 招式元数据 ----------------
        class StrikeMeta
        {
            public LimbType LimbA, LimbB;
            public string DisplayName;
            public bool IsKick;
            public float MaxAimAngleDeg;
            public float AimMinDeg, AimMaxDeg;
            public float Reach;
            public float HeightBias;
            public float HitWindowStart;
            public float Duration;
        }

        static readonly Dictionary<string, StrikeMeta> strikeMeta = new(StringComparer.OrdinalIgnoreCase)
        {
            ["kick"] = new StrikeMeta
            {
                LimbA = LimbType.LeftFoot, LimbB = LimbType.RightFoot,
                DisplayName = "正蹬腿", IsKick = true,
                MaxAimAngleDeg = 30f, Reach = 1.0f, HeightBias = -0.35f,
                HitWindowStart = 0.2f, Duration = 0.4f,
            },
            ["punch"] = new StrikeMeta
            {
                LimbA = LimbType.LeftHand, LimbB = LimbType.RightHand,
                DisplayName = "直拳", IsKick = false,
                MaxAimAngleDeg = 55f, Reach = 0.85f, HeightBias = 0f,
                HitWindowStart = 0.4f, Duration = 0.35f,
            },
        };

        /// <summary>一次出招的动作层数值（伤害数值不在此——在武器 attack XML）</summary>
        public class StrikeDef
        {
            public string Id, DisplayName;
            public LimbType LimbA, LimbB;
            public bool IsKick;
            public float Reach, HeightBias, MaxAimAngleDeg, AimMinDeg, AimMaxDeg, HitWindowStart, Duration;
            public float Force;   // 蹬腿驱动力（拳类不用）
        }

        // ---------------- 失控防护 ----------------
        const float AbortTimeout = 1.5f;
        const float LockFlipTime = 0.7f;
        const float RecoveryTime  = 0.3f;
        const float StabilityMaxVel = 7f;
        const float StabilityMaxAngular = 6f;
        const float EmergencyVel = 10f;
        const float EmergencyAngular = 9f;
        const float KickMaxAngularVel = 12f;
        const float KickMaxLinearVel = 8f;

        // 命中判定半径（米）：出招肢体到这个距离内的敌方肢体即命中
        const float HitRadius = 0.4f;

        static FieldInfo fallingProneTimerField;
        static bool proneFieldSearched;

        class StrikeState
        {
            public StrikeDef Def;
            public Limb Limb;              // 出招肢体（命中体）；拳类=持武器的手 / 连击换手的另一只手
            public MeleeWeapon Melee;
            public Item WeaponItem;
            public float Timer;
            public double StartTime;
            public float Recovery;
            public bool WasRunning;        // 踢腿：原生攻击 IsRunning 跟踪
            public int ComboIndex;         // 连击序号（0=起手手，1=换手）
            public Vector2 OriginPos;      // 世界固定原点（出招时躯干位置）——轨迹目标必须用它，
            public float StartDir;         // 出招时朝向——实时躯干会反作用力反馈成"向身后飞"
            public readonly HashSet<Entity> HitTargets = new();
            /// <summary>被碰过的手部拉力关节的原始状态（收招时逐个还原——不还原会出现"手持物握持位置偏移"）</summary>
            public readonly List<(Limb Limb, bool Enabled, float MaxForce)> HandJointBackup = new();
        }

        static readonly Dictionary<Character, StrikeState> active = new();

        /// <summary>连击记录：上一招的结束时间与序号（决定下一招是否换手）</summary>
        class ComboInfo { public double EndTime; public int ComboIndex; }
        static readonly Dictionary<Character, ComboInfo> comboTrack = new();
        /// <summary>连击窗口：上一招结束后这么久内再次出招算连击</summary>
        const double ComboWindow = 1.0;

        // 架势道具位（HoldItem 空间，X 朝前自动翻转）
        static readonly Vector2 PunchGuardPos = new Vector2(0.30f, 0.0f);
        /// <summary>空手时另一只手的戒备位（相对躯干）</summary>
        static readonly Vector2 OffHandGuardOffset = new Vector2(0.28f, 0.05f);

        // 踢腿注入用（原生攻击管线）
        static FieldInfo limbAttackField;
        static bool reflectionFailed;
        static readonly Dictionary<Limb, Attack> originalAttacks = new();

        public static void Log(object msg, bool warn = false)
        {
            LuaCsLogger.LogMessage($"[Touhou.MartialArts] {msg ?? "null"}",
                warn ? Color.Orange : Color.LightGreen);
        }

        // ---------------- 招式数值构建 ----------------

        static StrikeDef DefFromTemplate(string strikeId)
        {
            if (!strikeMeta.TryGetValue(strikeId, out var meta)) return null;
            return BaseDef(strikeId, meta);
        }

        static StrikeDef DefFromComponent(MartialArtsWeapon comp)
        {
            string strikeId = (comp.Strike ?? "punch").Trim().ToLowerInvariant();
            if (!strikeMeta.TryGetValue(strikeId, out var meta)) strikeId = "punch";
            meta = strikeMeta[strikeId];
            var def = BaseDef(strikeId, meta);
            def.Reach = comp.Reach;
            def.HeightBias = comp.Height;
            def.Duration = comp.Duration;
            def.Force = comp.Force;
            return def;
        }

        static StrikeDef DefFromElement(XElement el)
        {
            string strikeId = (el.Attribute("strike")?.Value ?? "punch").Trim().ToLowerInvariant();
            if (!strikeMeta.TryGetValue(strikeId, out var meta)) strikeId = "punch";
            meta = strikeMeta[strikeId];
            var def = BaseDef(strikeId, meta);
            def.Reach = el.GetAttributeFloat("reach", def.Reach);
            def.HeightBias = el.GetAttributeFloat("height", def.HeightBias);
            def.Duration = el.GetAttributeFloat("duration", def.Duration);
            def.Force = el.GetAttributeFloat("force", def.Force);
            return def;
        }

        static StrikeDef BaseDef(string strikeId, StrikeMeta meta) => new StrikeDef
        {
            Id = strikeId,
            DisplayName = meta.DisplayName,
            LimbA = meta.LimbA, LimbB = meta.LimbB,
            IsKick = meta.IsKick,
            Reach = meta.Reach, HeightBias = meta.HeightBias,
            MaxAimAngleDeg = meta.MaxAimAngleDeg, AimMinDeg = meta.AimMinDeg, AimMaxDeg = meta.AimMaxDeg,
            HitWindowStart = meta.HitWindowStart,
            Duration = meta.Duration,
            Force = 6f,
        };

        // ---------------- 踢腿的原生攻击注入 ----------------

        /// <summary>
        /// 踢腿的原生攻击定义（内联模板，数值来自 def）。torque 恒 0——扭矩是每帧自旋注入。
        /// 无 affliction 子元素：伤害走武器 MeleeWeapon.Attack，这里只管动作驱动。
        /// </summary>
        static Attack BuildKickAttack(StrikeDef def)
        {
            string xml =
                $"<Attack context=\"Any\" cooldown=\"0.1\" range=\"120\" damagerange=\"60\" duration=\"{F(def.Duration)}\" " +
                $"stun=\"0\" structuredamage=\"0\" itemdamage=\"0\" targetimpulse=\"0\" targetimpulseworld=\"0,0\" " +
                $"severlimbsprobability=\"0\" force=\"{F(def.Force)}\" torque=\"0\" hitdetectiontype=\"Contact\" " +
                $"onlyhumans=\"False\" targetforce=\"0\" targetforceworld=\"0,0\" priority=\"0\" targettype=\"Character\" " +
                $"secondarycooldown=\"0.01\" applyforcesonlyonce=\"False\" stickchance=\"0\" cooldownrandomfactor=\"0\" " +
                $"afterattack=\"Pursue\" reverse=\"False\" targetlimbtype=\"None\" retreat=\"False\" afterattackdelay=\"0\" " +
                $"rootforceworldstart=\"0,0\" rootforceworldmiddle=\"0,0\" rootforceworldend=\"0,0\" roottransitioneasing=\"Smooth\" " +
                $"fullspeedafterattack=\"False\" emitstructuredamageparticles=\"False\" penetration=\"0\" levelwalldamage=\"0\" " +
                $"ranged=\"False\" avoidfriendlyfire=\"False\" requiredangle=\"20\" submarineimpactmultiplier=\"1\" blink=\"False\" />";
            try
            {
                return new Attack(new ContentXElement(null, XElement.Parse(xml)), "Touhou.MartialArts kick", null);
            }
            catch (Exception e)
            {
                Log($"创建踢腿攻击实例失败：{e.Message}", true);
                return null;
            }
        }

        static string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        static bool BindAttack(Limb limb, Attack atk)
        {
            if (reflectionFailed || atk == null) return false;
            if (limbAttackField == null)
            {
                limbAttackField = typeof(Limb).GetField("attack", BindingFlags.Public | BindingFlags.Instance);
                if (limbAttackField == null) { reflectionFailed = true; Log("反射失败：Limb.attack 字段不存在", true); return false; }
            }
            if (!originalAttacks.ContainsKey(limb)) originalAttacks[limb] = limb.attack;
            try { limbAttackField.SetValue(limb, atk); }
            catch (Exception e) { Log($"换绑攻击失败：{e.Message}", true); return false; }
            return true;
        }

        static void UnbindAttack(Limb limb)
        {
            if (limb == null || limb.Removed || limbAttackField == null) return;
            if (!originalAttacks.TryGetValue(limb, out var orig)) return;
            try { limbAttackField?.SetValue(limb, orig); originalAttacks.Remove(limb); } catch { }
        }

        // ---------------- 触发 ----------------

        internal static bool ReadWeaponConfig(Item item, out string strikeId, out StrikeDef def)
        {
            strikeId = null; def = null;
            if (item == null) return false;
            var comp = item.GetComponent<MartialArtsWeapon>();
            if (comp != null)
            {
                def = DefFromComponent(comp);
                strikeId = def.Id;
                return true;
            }
            var el = item.Prefab?.ConfigElement?.GetChildElement("MartialArtsWeapon");
            if (el != null)
            {
                def = DefFromElement(el);
                strikeId = def.Id;
                return true;
            }
            return false;
        }

        static readonly Dictionary<Item, string> strikeIdCache = new();

        /// <summary>架势分支专用轻量查询：只取小写 strike id，结果按 Item 缓存</summary>
        internal static bool TryGetStrikeId(Item item, out string strikeId)
        {
            if (item == null) { strikeId = null; return false; }
            if (strikeIdCache.TryGetValue(item, out strikeId)) return true;
            if (!ReadWeaponConfig(item, out strikeId, out _)) return false;
            strikeIdCache[item] = strikeId;
            return true;
        }

        /// <summary>键/命令路径：优先当前选中武器，其次手持扫描，最后空手模板（空手踢腿可测动作，拳类需要武器才能摆曲线）。</summary>
        public static void TryStartBest(Character c, string strikeId)
        {
            if (c.SelectedItem != null && ReadWeaponConfig(c.SelectedItem, out string selStrike, out StrikeDef selDef))
            {
                TryStart(c, selStrike, c.SelectedItem, selDef);
                return;
            }
            foreach (var held in c.HeldItems)
            {
                if (held == null) continue;
                if (ReadWeaponConfig(held, out string wStrike, out StrikeDef wDef))
                {
                    TryStart(c, wStrike, held, wDef);
                    return;
                }
            }
            TryStart(c, strikeId, null, null);
        }

        public static void TryStart(Character c, string strikeId, Item weaponItem = null, StrikeDef weaponDef = null)
        {
            strikeId = (strikeId ?? "").Trim().ToLowerInvariant();
            void Refuse(string why) { /* 出招拒绝静默处理（调试日志已按定稿移除） */ }

            StrikeDef def = weaponDef ?? DefFromTemplate(strikeId);
            if (def == null) { Refuse("未知招式"); return; }
            strikeId = def.Id;
            if (c == null || c.Removed || c.IsDead) { Refuse("角色无效"); return; }
            var ac = c.AnimController;
            if (ac == null) { Refuse("无 AnimController"); return; }
            if (active.TryGetValue(c, out var stale))
            {
                if (Timing.TotalTime - stale.StartTime < AbortTimeout) { Refuse("招式进行中"); return; }
                End(c, stale);
            }
            if (!c.IsHuman) { Refuse("非人类角色"); return; }
            if (c.IsRagdolled || c.IsKnockedDownOrRagdolled) { Refuse("角色已瘫倒"); return; }
            if (ac.SimplePhysicsEnabled) { Refuse("简化物理中"); return; }
            if (ac.IsClimbing) { Refuse("爬梯子中"); return; }
            if (!c.AllowInput || c.Stun > 0.01f) { Refuse("无法行动"); return; }

            // 空手拳类无法摆曲线（没有道具可驱动）——提示持武器
            if (!def.IsKick && weaponItem == null) { Refuse("拳类需要持有武术武器"); return; }
            // 踢腿：禁止水中 + 禁止移动（边走边踢动画系统扛不住）
            if (def.IsKick)
            {
                if (ac.InWater) { Refuse("水中不能踢腿"); return; }
                if (ac.TargetMovement.LengthSquared() > 0.01f) { Refuse("移动中不能踢腿（先站定）"); return; }
            }

            // 稳定门
            Limb mainLimb = ac.MainLimb;
            if (mainLimb?.body != null)
            {
                if (Math.Abs(mainLimb.body.AngularVelocity) > StabilityMaxAngular) { Refuse("身体旋转未稳定"); return; }
                if (mainLimb.body.LinearVelocity.LengthSquared() > StabilityMaxVel * StabilityMaxVel) { Refuse("身体移动未稳定"); return; }
            }

            Limb torso = ac.GetLimb(LimbType.Torso);
            if (torso == null) { Refuse("找不到躯干"); return; }

            float dir = ac.Dir >= 0 ? 1f : -1f;

            // 连击：上一招结束 ComboWindow 内再次出招 → 换手（拳）/换脚（踢）；超时回起手
            int comboIndex = 0;
            if (comboTrack.TryGetValue(c, out var ct) && Timing.TotalTime - ct.EndTime < ComboWindow)
                comboIndex = (ct.ComboIndex + 1) % 2;

            MeleeWeapon mw = weaponItem?.GetComponent<MeleeWeapon>();
            Limb limb;
            if (def.IsKick)
            {
                // 踢腿换手 = 换脚：起手后脚，连击前脚
                Limb rear = PickRearLimb(dir, ac.GetLimb(def.LimbA), ac.GetLimb(def.LimbB));
                Limb other = rear == null ? null : (rear.type == def.LimbA ? ac.GetLimb(def.LimbB) : ac.GetLimb(def.LimbA));
                limb = comboIndex == 0 ? rear : other;
            }
            else
            {
                Limb lh = ac.GetLimb(LimbType.LeftHand), rh = ac.GetLimb(LimbType.RightHand);
                Limb weaponHand = NearestLimb(weaponItem.SimPosition, lh, rh);
                limb = weaponHand;
                if (comboIndex == 1)
                {
                    // 换手只认"另一只手持有武术武器"（双持交替，已验证稳定）；
                    // 空手换手轨迹拳已废弃——pull joint 驱动空闲手反复爆炸/向后飞，多次修复未果，
                    // 空手时保持武器手连打，不换手
                    Item otherWeapon = null;
                    foreach (var held in c.HeldItems)
                    {
                        if (held == null || held == weaponItem) continue;
                        if (ReadWeaponConfig(held, out _, out _)) { otherWeapon = held; break; }
                    }
                    if (otherWeapon != null)
                    {
                        limb = weaponHand == lh ? rh : lh;
                        weaponItem = otherWeapon;
                        mw = weaponItem.GetComponent<MeleeWeapon>();
                    }
                }
            }
            if (limb == null || limb.IsSevered || limb.body == null || !limb.body.Enabled)
            { Refuse("招式肢体不可用（断肢？）"); return; }

            // 攻速门（对实际出招的武器；reloadTimer 在出招开始时结算，覆盖整个出招周期）
            if (mw != null && mw.reloadTimer > 0f)
            {
                Refuse($"武器冷却中（{mw.reloadTimer:F1}s）");
                return;
            }

            // 踢腿：注入原生攻击（动作驱动用）
            if (def.IsKick)
            {
                var atk = BuildKickAttack(def);
                if (!BindAttack(limb, atk)) { Refuse("攻击注入失败"); return; }
                limb.attack.ResetCoolDown();
            }

            var st = new StrikeState
            {
                Def = def, Limb = limb, Melee = mw, WeaponItem = weaponItem,
                StartTime = Timing.TotalTime,
                ComboIndex = comboIndex,
                OriginPos = torso.SimPosition, StartDir = dir,
            };
            // 备份将被驱动的手部拉力关节状态（收招还原，杜绝"握持位置偏移"残留）
            BackupHandJoint(st, ac.GetLimb(LimbType.LeftHand));
            BackupHandJoint(st, ac.GetLimb(LimbType.RightHand));

            if (mw != null)
            {
                if (mw.Attack == null)
                {
                    Log("警告：武器的 MeleeWeapon 缺少 <attack> 元素，本次出招无伤害", true);
                }
                // 攻速结算：冷却覆盖整个出招周期（vanilla 公式折算攻速加成）
                float speedMul = (1f + c.GetStatValue(StatTypes.MeleeAttackSpeed))
                               * (1f + weaponItem.GetQualityModifier(Quality.StatType.StrikingSpeedMultiplier));
                mw.reloadTimer = Math.Max(mw.Reload, def.Duration + RecoveryTime + 0.05f) / speedMul;
            }

            active[c] = st;
            ac.LockFlipping(LockFlipTime);
        }

        /// <summary>备份手部拉力关节状态（收招时还原）</summary>
        static void BackupHandJoint(StrikeState st, Limb hand)
        {
            if (hand == null || hand.Removed) return;
            foreach (var b in st.HandJointBackup) { if (b.Limb == hand) return; }
            st.HandJointBackup.Add((hand, hand.PullJointEnabled, hand.PullJointMaxForce));
        }

        static Limb PickRearLimb(float dir, Limb la, Limb lb)
        {
            if (la == null) return lb;
            if (lb == null) return la;
            bool aIsRear = dir > 0 ? la.SimPosition.X < lb.SimPosition.X : la.SimPosition.X > lb.SimPosition.X;
            return aIsRear ? la : lb;
        }

        static Limb NearestLimb(Vector2 pos, Limb la, Limb lb)
        {
            if (la == null) return lb;
            if (lb == null) return la;
            return Vector2.DistanceSquared(la.SimPosition, pos) <= Vector2.DistanceSquared(lb.SimPosition, pos) ? la : lb;
        }

        // ---------------- 命中（距离判定 → 武器 Attack 结算） ----------------

        /// <summary>
        /// 命中窗口内每帧：出招肢体 HitRadius 内的最近敌方肢体即命中（不依赖碰撞系统，
        /// 不可能"碰不到"；睡眠布娃娃也能中）。隔墙检查沿用 vanilla MeleeWeapon 模式。
        /// </summary>
        static void DamageScan(StrikeState st, Character c)
        {
            var attack = st.Melee.Attack;
            Vector2 pos = st.Limb.SimPosition;
            foreach (var other in Character.CharacterList)
            {
                if (other == c || other.Removed || other.IsDead || !other.Enabled) continue;
                if (other.IgnoreMeleeWeapons) continue;
                if (st.HitTargets.Contains(other)) continue;

                Limb best = null;
                float bestSq = HitRadius * HitRadius;
                foreach (var l in other.AnimController.Limbs)
                {
                    if (l.IsSevered || l.Removed || l.body == null || !l.body.Enabled) continue;
                    float d = Vector2.DistanceSquared(l.SimPosition, pos);
                    if (d < bestSq) { bestSq = d; best = l; }
                }
                if (best == null) continue;

                // 隔墙检查（vanilla MeleeWeapon 模式）
                if (Submarine.PickBody(c.AnimController.AimSourceSimPos, best.SimPosition,
                        collisionCategory: Physics.CollisionWall | Physics.CollisionLevel | Physics.CollisionItemBlocking,
                        allowInsideFixture: true,
                        customPredicate: (Fixture fx) => fx.CollidesWith.HasFlag(Physics.CollisionItem) && fx.Body != best.body.FarseerBody) != null)
                    continue;

                attack.SetUser(c);
                attack.DamageMultiplier = 1f + c.GetStatValue(StatTypes.MeleeAttackMultiplier);
                if (st.WeaponItem != null)
                {
                    attack.DamageMultiplier *= 1f + st.WeaponItem.GetQualityModifier(Quality.StatType.StrikingPowerMultiplier);
                }
                other.LastDamageSource = st.WeaponItem;
                attack.DoDamageToLimb(c, best, st.Limb.WorldPosition, 1f, playSound: true, st.Limb.body, st.Limb);
                st.HitTargets.Add(other);
            }
        }

        // ---------------- 每帧驱动 ----------------

        internal static void DriveStanding(HumanoidAnimController ac)
        {
            Character c = ac.character;
            if (c == null) return;
            if (!active.TryGetValue(c, out var st)) return;

            float dt = (float)Timing.Step;
            st.Timer += dt;

            if (c.Removed || c.IsDead || c.IsRagdolled || ac.IsClimbing || st.Limb.IsSevered || !st.Limb.body.Enabled
                || st.Timer > AbortTimeout)
            {
                End(c, st);
                return;
            }

            // 紧急制动
            Limb mainLimb = ac.MainLimb;
            if (mainLimb?.body != null &&
                (Math.Abs(mainLimb.body.AngularVelocity) > EmergencyAngular ||
                 mainLimb.body.LinearVelocity.LengthSquared() > EmergencyVel * EmergencyVel))
            {
                foreach (var l in ac.Limbs)
                {
                    if (l?.body == null || !l.body.Enabled) continue;
                    l.body.AngularVelocity *= 0.2f;
                    l.body.LinearVelocity *= 0.2f;
                }
                End(c, st);
                return;
            }

            ResetFallingProne(ac);

            // 全身角速度保险（任何出招）：玩家的大动作（爬梯/被抓/被撞倒）随时可能发生，
            // 任何肢体自旋都先压住——通用防崩网，之前只有踢腿有这个待遇
            foreach (var l in ac.Limbs)
            {
                if (l?.body == null || !l.body.Enabled) continue;
                ClampAngular(l, 30f);
            }

            // 收势期：踢腿只阻尼（原生驱动收招本来就稳，别接管）
            if (st.Recovery > 0f)
            {
                st.Recovery -= dt;
                if (st.Def.IsKick && st.Limb?.body != null && st.Limb.body.Enabled)
                {
                    st.Limb.body.AngularVelocity *= 0.8f;
                    st.Limb.body.LinearVelocity *= 0.9f;
                }
                if (st.Recovery <= 0f)
                {
                    End(c, st);
                }
                return;
            }

            // 命中窗口
            if (st.Melee?.Attack != null && st.Timer >= st.Def.Duration * st.Def.HitWindowStart
                && st.Timer <= st.Def.Duration)
            {
                try { DamageScan(st, c); }
                catch (Exception e) { Log($"命中扫描异常：{e.Message}", true); }
            }

            if (st.Def.IsKick)
            {
                // 蹬腿：原生攻击力驱动（最初验证"特效正常"的版本）
                Limb torso = ac.GetLimb(LimbType.Torso);
                if (torso == null) { End(c, st); return; }
                Vector2 attackPos = ComputeAttackPos(c, ac, torso.SimPosition, st.Def);
                // 关键：UpdateAttack 的冲量施加点是 pullJoint.WorldAnchorA——原版站立时它停在脚下方
                // 地面形成杠杆 → 自旋爆炸的根因。每帧先钉到脚心，冲量变纯平移
                st.Limb.PullJointWorldAnchorA = st.Limb.SimPosition;
                try
                {
                    st.Limb.UpdateAttack(dt, attackPos, null, out _);
                }
                catch (Exception e)
                {
                    Log($"UpdateAttack 异常：{e.Message}", true);
                    End(c, st);
                    return;
                }
                // 只压出招脚自己的速度（其他肢体不碰）
                ClampAngular(st.Limb, KickMaxAngularVel);
                ClampLinear(st.Limb, KickMaxLinearVel);
                if (st.WasRunning && !st.Limb.attack.IsRunning)
                {
                    st.Recovery = RecoveryTime;
                    return;
                }
                st.WasRunning = st.Limb.attack.IsRunning;
            }
            else if (st.Timer >= st.Def.Duration)
            {
                // 拳类：时长到点进收势（曲线收回到架势位）
                st.Recovery = RecoveryTime;
                return;
            }
        }

        /// <summary>瞄准方向（本地空间，X 朝前）：光标方向经锥角收拢。</summary>
        static Vector2 ComputeAimDirLocal(Character c, AnimController ac, StrikeDef def)
        {
            float dir = ac.Dir >= 0 ? 1f : -1f;
            Vector2 cursorSim = c.SimPosition + ConvertUnits.ToSimUnits(c.CursorPosition - c.Position);
            Vector2 torsoPos = ac.GetLimb(LimbType.Torso)?.SimPosition ?? c.SimPosition;
            Vector2 aim = cursorSim - torsoPos;
            if (aim.LengthSquared() < 1e-6f) return new Vector2(1f, 0f);
            aim.Normalize();
            float angle = (float)Math.Atan2(aim.Y, aim.X * dir); // 相对朝向：前=0°，上=+90°
            float minA = def.AimMaxDeg != 0f || def.AimMinDeg != 0f ? def.AimMinDeg : -def.MaxAimAngleDeg;
            float maxA = def.AimMaxDeg != 0f || def.AimMinDeg != 0f ? def.AimMaxDeg : def.MaxAimAngleDeg;
            angle = MathHelper.Clamp(angle, MathHelper.ToRadians(minA), MathHelper.ToRadians(maxA));
            return new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        }

        static Vector2 ComputeAttackPos(Character c, AnimController ac, Vector2 torsoPos, StrikeDef def)
        {
            float dir = ac.Dir >= 0 ? 1f : -1f;
            Vector2 local = ComputeAimDirLocal(c, ac, def);
            return torsoPos + new Vector2(local.X * dir, local.Y) * def.Reach + new Vector2(0, def.HeightBias);
        }

        static float EaseOut(float t) { t = MathHelper.Clamp(t, 0f, 1f); return 1f - (1f - t) * (1f - t); }
        static float SmoothStep(float t) { t = MathHelper.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); }

        // ---------------- 武器姿态（出招曲线 + 架势；StrikeMeleePosePatch 调用） ----------------

        internal static bool MeleeUpdateOverride(MeleeWeapon w, float deltaTime)
        {
            StrikeState st = null;
            Character striker = null;
            foreach (var kv in active)
            {
                if (kv.Value.Melee == w) { st = kv.Value; striker = kv.Key; break; }
            }

            if (st != null)
            {
                // 模式看门狗：爬梯/瘫倒/简化物理时 UpdateStanding 会停（驱动死了），
                // 但本补丁（物品 Update）还在跑——必须立刻安全中止，否则姿态曲线和
                // 爬梯/瘫倒系统拽手的力量硬碰硬，直接把肢体甩爆（RightHand ±1500 的教训）
                var picker0 = w.picker;
                if (picker0 == null || picker0.IsRagdolled || picker0.IsKnockedDownOrRagdolled
                    || picker0.AnimController == null || picker0.AnimController.IsClimbing
                    || picker0.AnimController.SimplePhysicsEnabled)
                {
                    End(striker, st);
                    return true;
                }

                VanillaHousekeeping(w, deltaTime);
                if (w.picker == null) { return false; }
                AnimController ac = w.picker.AnimController;

                if (st.Def.IsKick)
                {
                    // 蹬腿：武器保持常态持握（不做道具跟随——道具刚体跟着脚跑是爆炸帮凶之一）
                    ac.HoldItem(deltaTime, w.item, w.handlePos, w.holdPos, false, w.holdAngle);
                }
                else
                {
                    // 拳类：道具沿瞄准曲线运动（aimMelee = 近战手臂伸展模式，手臂打直）
                    float t = MathHelper.Clamp(st.Timer / Math.Max(0.05f, st.Def.Duration), 0f, 1f);
                    Vector2 guard = PunchGuardPos;
                    Vector2 localDir = ComputeAimDirLocal(striker, ac, st.Def);
                    Vector2 extended = localDir * st.Def.Reach + new Vector2(0, st.Def.HeightBias);
                    Vector2 pos;
                    // 直拳：40% 快出、15% 到位保持、45% 收回架势
                    if (t < 0.40f) pos = Vector2.Lerp(guard, extended, EaseOut(t / 0.40f));
                    else if (t < 0.55f) pos = extended;
                    else pos = Vector2.Lerp(extended, guard, SmoothStep((t - 0.55f) / 0.45f));
                    ac.HoldItem(deltaTime, w.item, w.handlePos, pos, false, w.holdAngle, aimMelee: true);

                    // 空手另一只手拉起到戒备位（用固定原点，不用实时躯干——随动会反馈漂移）
                    Limb offHand = st.Limb.type == LimbType.LeftHand
                        ? ac.GetLimb(LimbType.RightHand) : ac.GetLimb(LimbType.LeftHand);
                    if (offHand != null && !offHand.IsSevered && offHand.body.Enabled)
                    {
                        offHand.PullJointEnabled = true;
                        offHand.PullJointWorldAnchorA = st.OriginPos + new Vector2(OffHandGuardOffset.X * st.StartDir, OffHandGuardOffset.Y);
                    }
                }
                return false;
            }

            // ---- 准备架势分支 ----
            var picker = w.picker;
            if (picker == null || !picker.HeldItems.Contains(w.item)) return true;
            if (w.hitting) return true;
            // 爬梯/瘫倒/简化物理：架势也全交原版（和出招同款的模式看门狗）
            if (picker.IsRagdolled || picker.IsKnockedDownOrRagdolled
                || picker.AnimController == null || picker.AnimController.IsClimbing
                || picker.AnimController.SimplePhysicsEnabled) return true;
            if (!picker.IsKeyDown(InputType.Aim)) return true;
            if (!TryGetStrikeId(w.item, out string sid)) return true;

            VanillaHousekeeping(w, deltaTime);
            if (w.picker == null) { return false; }
            AnimController ac2 = picker.AnimController;
            if (sid == "kick")
            {
                // 踢腿架势：武器保持常态持握
                ac2.HoldItem(deltaTime, w.item, w.handlePos, w.holdPos, false, w.holdAngle);
            }
            else
            {
                // 拳架：道具举到戒备位（与出招曲线起手位一致，出招无缝衔接）
                ac2.HoldItem(deltaTime, w.item, w.handlePos, PunchGuardPos, false, w.holdAngle + 0.2f, aimMelee: true);
            }
            return false;
        }

        /// <summary>照抄 vanilla MeleeWeapon.Update 前段的 housekeeping（impact 队列/reload 计时/状态效果/翻转）</summary>
        static void VanillaHousekeeping(MeleeWeapon w, float deltaTime)
        {
            if (!w.item.body.Enabled)
            {
                w.impactQueue.Clear();
                return;
            }
            if (w.picker == null || !w.picker.HeldItems.Contains(w.item))
            {
                w.impactQueue.Clear();
                w.IsActive = false;
            }
            while (w.impactQueue.Count > 0)
            {
                var impact = w.impactQueue.Dequeue();
                w.HandleImpact(impact);
            }
            if (w.picker == null) { return; }
            w.reloadTimer -= deltaTime;
            if (w.reloadTimer < 0) { w.reloadTimer = 0; }
            w.ApplyStatusEffects(ActionType.OnActive, deltaTime, w.picker);
            if (w.item.body.Dir != w.picker.AnimController.Dir)
            {
                w.item.FlipX(relativeToSub: false);
            }
        }

        static void ClampAngular(Limb limb, float maxAv)
        {
            float av = limb.body.AngularVelocity;
            if (Math.Abs(av) > maxAv) limb.body.AngularVelocity = Math.Sign(av) * maxAv * 0.5f;
        }

        static void ClampLinear(Limb limb, float maxV)
        {
            Vector2 v = limb.body.LinearVelocity;
            float lenSq = v.LengthSquared();
            if (lenSq > maxV * maxV)
            {
                limb.body.LinearVelocity = v * (maxV / (float)Math.Sqrt(lenSq));
            }
        }

        static void ResetFallingProne(HumanoidAnimController ac)
        {
            if (!proneFieldSearched)
            {
                proneFieldSearched = true;
                fallingProneTimerField = typeof(HumanoidAnimController).GetField("fallingProneAnimTimer",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (fallingProneTimerField == null) Log("fallingProneAnimTimer 字段未找到，假摔压制停用", true);
            }
            if (fallingProneTimerField == null) return;
            try
            {
                if ((float)fallingProneTimerField.GetValue(ac) > 0f)
                    fallingProneTimerField.SetValue(ac, 0f);
            }
            catch { }
        }

        static void End(Character c, StrikeState st)
        {
            if (c?.AnimController is HumanoidAnimController hac) ResetFallingProne(hac);
            // 踢腿：还原肢体 attack（原版人类为 null）
            UnbindAttack(st.Limb);
            // 还原所有被碰过的手部拉力关节（架势/换手轨迹会写 Enabled/锚点）
            // 关键：锚点必须钉回手的当前位置——原版对空闲的手不重写锚点，残留的轨迹锚点会
            // 把手永远钉在那个世界点（人一动，手就"一直向后飞"）；锚点=当前位置 = 拉力归零
            foreach (var b in st.HandJointBackup)
            {
                if (b.Limb == null || b.Limb.Removed || b.Limb.body == null || !b.Limb.body.Enabled) continue;
                try
                {
                    b.Limb.PullJointWorldAnchorA = b.Limb.SimPosition;
                    b.Limb.PullJointMaxForce = b.MaxForce;
                    b.Limb.PullJointEnabled = b.Enabled;
                }
                catch { }
            }
            // 武器显式归位一次（立刻回到持握位，不等 vanilla 下一帧）
            if (st.Melee != null && c != null && !c.Removed && c.AnimController != null)
            {
                try
                {
                    c.AnimController.HoldItem(0f, st.Melee.item, st.Melee.handlePos, st.Melee.holdPos, false, st.Melee.holdAngle);
                }
                catch { }
            }
            // 记录连击（供下一招换手判定）
            comboTrack[c] = new ComboInfo { EndTime = Timing.TotalTime, ComboIndex = st.ComboIndex };
            active.Remove(c);
        }

        public static void ClearAll()
        {
            active.Clear();
            comboTrack.Clear();
            strikeIdCache.Clear();
        }

        public static void RestoreInjections()
        {
            foreach (var kv in originalAttacks)
            {
                if (kv.Key.Removed) continue;
                try { limbAttackField?.SetValue(kv.Key, kv.Value); } catch { }
            }
            originalAttacks.Clear();
        }
    }

    /// <summary>
    /// LMB 触发补丁：MeleeWeapon.Use 前缀。武术武器需要右键瞄准中才出招
    /// （符合常规近战"右键架势+左键攻击"的操作习惯）；原版挥舞一律拦截。
    /// </summary>
    [HarmonyPatch]
    public static class StrikeUsePatch
    {
        static MethodBase TargetMethod()
            => AccessTools.Method(typeof(MeleeWeapon), "Use", new[] { typeof(float), typeof(Character) });

        static bool Prefix(MeleeWeapon __instance, float deltaTime, Character character)
        {
            try
            {
                if (!StrikeManager.ReadWeaponConfig(__instance.item, out string sid, out var def)) return true;
                if (character == null || !character.IsKeyDown(InputType.Aim)) return false; // 没瞄准：拦住不动
                StrikeManager.TryStart(character, sid, __instance.item, def);
                return false; // 原版挥舞拦截（出招由招式系统驱动）
            }
            catch (Exception e)
            {
                StrikeManager.Log($"Use 路由异常：{e.Message}", true);
                return true;
            }
        }
    }

    /// <summary>武器姿态补丁：MeleeWeapon.Update 前缀（出招曲线 / 架势，仅接管武术武器）。</summary>
    [HarmonyPatch]
    public static class StrikeMeleePosePatch
    {
        static MethodBase TargetMethod()
            => AccessTools.Method(typeof(MeleeWeapon), "Update", new[] { typeof(float), typeof(Camera) });

        static bool Prefix(MeleeWeapon __instance, float deltaTime, Camera cam)
        {
            try { return StrikeManager.MeleeUpdateOverride(__instance, deltaTime); }
            catch (Exception e)
            {
                StrikeManager.Log($"武器姿态补丁异常：{e.Message}", true);
                return true;
            }
        }
    }

    /// <summary>招式的每帧驱动：同时挂在 UpdateStanding 与 UpdateSwimming 的 postfix。</summary>
    [HarmonyPatch]
    public static class StrikeDrivePatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var standing = AccessTools.Method(typeof(HumanoidAnimController), "UpdateStanding");
            if (standing == null) StrikeManager.Log("找不到 HumanoidAnimController.UpdateStanding", true);
            else yield return standing;
            var swimming = AccessTools.Method(typeof(HumanoidAnimController), "UpdateSwimming");
            if (swimming == null) StrikeManager.Log("找不到 HumanoidAnimController.UpdateSwimming", true);
            else yield return swimming;
        }

        static void Postfix(HumanoidAnimController __instance)
        {
            try { StrikeManager.DriveStanding(__instance); }
            catch (Exception e) { StrikeManager.Log($"招式驱动异常：{e.Message}", true); }
        }
    }
}
