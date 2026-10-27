// 通用武器能量组件（TouhouWeaponEnergy）：能量门槛、激活/结束、回能窗口、
// 使用者 aff 与 OnActivate/OnDeactivate 钩子。效果参数见 WeaponEnergySettings。
using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;

namespace Touhou.WeaponEnergy
{
    [HarmonyPatch(typeof(ItemComponent), nameof(ItemComponent.Load),
        new[] { typeof(ContentXElement), typeof(Item), typeof(bool) })]
    internal static class WeaponEnergyComponentLoadPatch
    {
        private static bool Prefix(ContentXElement element, Item item, ref ItemComponent __result)
        {
            if (!WeaponEnergyRuntime.OwnContext ||
                !TouhouWeaponEnergy.IsComponentName(element.Name.ToString()))
                return true;
            __result = new TouhouWeaponEnergy(item, element);
            return false;
        }
    }

    [HarmonyPatch(typeof(Item), nameof(Item.Load),
        new[] { typeof(ContentXElement), typeof(Submarine), typeof(bool), typeof(IdRemap) })]
    internal static class WeaponEnergyLegacySavePatch
    {
        private static void Postfix(ContentXElement element, Item __result)
        {
            if (!WeaponEnergyRuntime.OwnContext || __result == null) return;
            var component = __result.GetComponent<TouhouWeaponEnergy>();
            if (component == null || element.Elements().Any(child =>
                string.Equals(child.Name.ToString(), TouhouWeaponEnergy.ComponentName, StringComparison.OrdinalIgnoreCase))) return;
            var legacy = element.Elements().FirstOrDefault(child =>
                string.Equals(child.Name.ToString(), TouhouWeaponEnergy.LegacyComponentName, StringComparison.OrdinalIgnoreCase));
            // 原生按组件名读档，旧节点匹配不到新名字时，只补回原有的能量字段。
            if (legacy != null) component.Energy = legacy.GetAttributeFloat("energy", component.Energy);
        }
    }

    // 通用武器能量组件：
    //   - 能量门槛（满能量启动 / 最低能量）、消耗、自然恢复、按伤害恢复；
    //   - 激活期间给使用者一个 aff（可配置延迟/刷新，放下武器立刻撤）；
    //   - 命中目标附加任意 aff（缄默的减速/眩晕/麻醉也只是这套配置）；
    //   - 元素与属性见 WeaponEnergyRules.cs；XML 写 <AttackAffliction>/<StunEffect>/<SedationEffect>/<RegenModifier>。
    public sealed class TouhouWeaponEnergy : ItemComponent, IServerSerializable
    {
        internal const string ComponentName = "TouhouWeaponEnergy";
        internal const string LegacyComponentName = "TouhouSilenceControl";
        internal static bool IsComponentName(string value)
            => string.Equals(value, ComponentName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, LegacyComponentName, StringComparison.OrdinalIgnoreCase);
        private float storedEnergy;
        internal WeaponEnergyState State;
        private float syncTimer;
        private double lastToggle = double.NegativeInfinity;
        private double lastHitRecharge = double.NegativeInfinity;
        private float sentEnergy = -1f;
        private bool sentActive;
        private bool sentCanActivate;
        internal bool SnapshotReady { get; private set; }
        internal WeaponEnergySettings Settings { get; }
        public float MaxEnergy => Settings?.MaxEnergy ?? 100f;

        // 激活时长 / 使用者 aff / 伤害回能
        // 单人暂停时 TotalTime 仍在走，模式和使用者 buff 不能跟着现实时间到期。
        private static double ModeTime => Timing.TotalTimeUnpaused;
        private double activeUntil = double.NegativeInfinity;
        private double holderApplyAt = double.NegativeInfinity;
        private double holderRefreshAt = double.NegativeInfinity;
        private Character afflictedHolder;
        private float pendingDamage;
        private double damageFlushAt = double.NegativeInfinity;
        private double nextModifierRefresh;
        private float naturalMultiplier = 1f;
        private float damageMultiplier = 1f;

        [Serialize(100f, IsPropertySaveable.Yes)]
        public float Energy
        {
            get => State?.Energy ?? storedEnergy;
            set
            {
                // 构造基类时还没读到上限，等状态机建立后再按本枪配置夹紧。
                storedEnergy = float.IsFinite(value) ? Math.Max(0f, value) : 0f;
                State?.Restore(storedEnergy);
            }
        }

        public bool ControlActive => State != null && State.Active;
        public override bool UpdateWhenInactive => true;

        public TouhouWeaponEnergy(Item item, ContentXElement element) : base(item, element)
        {
            // 旧 XML 仍可用；后续存档统一写新名。
            name = ComponentName;
            Settings = WeaponEnergySettings.Read(element);
            State = new WeaponEnergyState(storedEnergy, Settings);
            SnapshotReady = WeaponEnergyRuntime.Authority;
            IsActive = true;
        }

        internal Character Holder()
        {
            var owner = Item.ParentInventory?.Owner as Character;
            // 倒地不等于收枪，模式只看武器是否还在活人的手槽里。
            return owner != null && !owner.Removed && !owner.IsDead && owner.HeldItems.Contains(Item) ? owner : null;
        }

        private Character Operator()
        {
            var holder = Holder();
            return holder != null && !holder.IsUnconscious && holder.Stun <= 0f ? holder : null;
        }

        // 按钮门禁：只有真正开启时才让原生 XML 链（姿态/音效/粒子）继续跑。
        internal bool Toggle(int activationButton = -1)
        {
            if (!WeaponEnergyRuntime.Authority || Operator() == null || Timing.TotalTime - lastToggle < Settings.ToggleCooldown) return false;
            if (ControlActive)
            {
                if (!Settings.ToggleOff) return false;
                lastToggle = Timing.TotalTime;
                State.Stop();
                ClearHolderAffliction();
                RunHooks(Settings.OnDeactivateEffects, Holder());
                SendState();
                return false;
            }
            State.Toggle(Item.Condition > 0f);
            if (!State.Active) return false;   // 能量不够：不占冷却，玩家补满能量后能立刻再按
            lastToggle = Timing.TotalTime;
            activeUntil = Settings.ActivationDuration > 0f ? ModeTime + Settings.ActivationDuration : double.PositiveInfinity;
            holderApplyAt = ModeTime + Settings.HolderDelay;
            RunHooks(Settings.OnActivateEffects, Holder());
            SendState(activationButton);
            return true;
        }

        internal bool PayShot()
        {
            if (!WeaponEnergyRuntime.Authority || Operator() == null) return false;
            bool wasActive = ControlActive;
            bool enhanced = State.PayShot();
            if (wasActive) SendState();
            return enhanced;
        }

        // 伤害回能：按“一次扳机窗口”把多弹丸/多肢体累加，避免霰弹吃多次上限。
        // 窗口只在第一次命中时定时，后续命中只累加：高射速持续命中也会每窗结算，
        // 不会像防抖那样“一直打就一直不结算”。
        internal void AccumulateDamage(float damage)
        {
            if (!WeaponEnergyRuntime.Authority || !float.IsFinite(damage) || damage <= 0f) return;
            if (Settings.DamageRegenPerDamage <= 0f) return;
            if (pendingDamage <= 0f) damageFlushAt = Timing.TotalTime + Settings.DamageRegenWindow;
            pendingDamage += damage;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (!WeaponEnergyRuntime.Authority) return;
            if (Item.Removed)
            {
                // 物品被拆掉/移除时没人再跑 Update，这里补一次清理，别把 buff 留在身上
                ClearHolderAffliction();
                return;
            }
            Character holder = Holder();
            bool operational = Item.Condition > 0f;
            bool held = holder != null && operational;
            bool wasActive = ControlActive;

            if (Timing.TotalTime >= nextModifierRefresh)
            {
                nextModifierRefresh = Timing.TotalTime + 0.5;  // 扫 tag/aff/天赋有分配，半秒一次足够
                RefreshModifiers(holder);
            }

            if (ControlActive && ModeTime >= activeUntil) State.Stop();

            State.Tick(deltaTime, held, operational, naturalMultiplier);
            SnapshotReady = true;

            // 任何原因结束（时长到、收枪、能量耗尽、损坏）都走一次收尾：撤使用者 aff + 执行关闭 SE
            if (wasActive && !ControlActive)
            {
                ClearHolderAffliction();
                RunHooks(Settings.OnDeactivateEffects, holder);
            }

            if (Settings.HolderRemoveOnHolster && afflictedHolder != null &&
                (holder == null || holder != afflictedHolder || !ControlActive))
                ClearHolderAffliction();

            if (ControlActive && holder != null && RegenModifierDef.IsSet(Settings.HolderAffliction))
            {
                if (afflictedHolder == null)
                {
                    if (ModeTime >= holderApplyAt) ApplyHolderAffliction(holder);
                }
                else if (Settings.HolderRefresh > 0f && ModeTime >= holderRefreshAt)
                {
                    ApplyHolderAffliction(holder);
                }
            }

            if (pendingDamage > 0f && Timing.TotalTime >= damageFlushAt)
            {
                float raw = Math.Clamp(pendingDamage * Settings.DamageRegenPerDamage,
                    Settings.DamageRegenMin, Math.Max(Settings.DamageRegenMin, Settings.DamageRegenMax));
                // 激活期间“获取”整体打折，避免边打边无限改装
                if (ControlActive) raw *= Settings.ActiveRegenMultiplier;
                State.AddEnergy(raw * damageMultiplier);
                pendingDamage = 0f;
            }

            syncTimer += deltaTime;
            bool boundaryChanged = ControlActive != sentActive || State.CanActivate != sentCanActivate;
            bool changed = Math.Abs(Energy - sentEnergy) >= 0.5f;
            // 手持时也低频补一次满电快照，方便中途加入的客户端。
            if (boundaryChanged || (changed && syncTimer >= 0.2f) || (held && syncTimer >= 2f)) SendState();
        }

        public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);

        internal bool RechargeHit(Character attacker, Character target, bool wasAlive, bool enhancedShot)
        {
            if (!WeaponEnergyRuntime.Authority || Item.Removed || Item.Condition <= 0f || attacker == null ||
                target == null || Settings.HitRechargeAmount <= 0f ||
                (Settings.HitRechargeRequireHeld && Holder() != attacker) ||
                Timing.TotalTime - lastHitRecharge < Settings.HitRechargeCooldown) return false;
            if (!WeaponEnergyRules.EligibleRechargeTarget(wasAlive, attacker == target,
                target.IsFriendly(attacker), target.Params.IsMachine, Settings)) return false;
            if (State.RecoverFromHit(enhancedShot) <= 0f) return false;
            lastHitRecharge = Timing.TotalTime;
            SendState();
            return true;
        }

        // 解析好的 XML StatusEffect：开启/结束时由组件执行，给弹匣打 tag、还原武器 tag 这类动作走这里。
        private void RunHooks(List<StatusEffect> effects, Character holder)
        {
            if (!WeaponEnergyRuntime.Authority || effects == null || effects.Count == 0) return;
            foreach (var effect in effects)
            {
                try
                {
                    Item.ApplyStatusEffect(effect, ActionType.OnUse, 1f, holder);
                }
                catch
                {
                    // 单条效果出错不能影响能量状态
                }
            }
        }

        private void ApplyHolderAffliction(Character holder)
        {
            WeaponEnergyRuntime.Apply(holder, Settings.HolderAffliction, Settings.HolderStrength, holder);
            afflictedHolder = holder;
            holderRefreshAt = Settings.HolderRefresh > 0f ? ModeTime + Settings.HolderRefresh : double.PositiveInfinity;
        }

        // 直接归零：无视该 aff 自己的 duration / strengthchange，避免和自动衰减打架。
        // 同一 aff 若还有别的来源，会被一起撤掉，所以改装类 buff 请用专属 id。
        private void ClearHolderAffliction()
        {
            if (Settings.HolderRemoveOnHolster && afflictedHolder != null &&
                RegenModifierDef.IsSet(Settings.HolderAffliction))
            {
                var affliction = afflictedHolder.CharacterHealth?.GetAffliction(Settings.HolderAffliction);
                if (affliction != null) affliction.Strength = 0f;
            }
            afflictedHolder = null;
            holderApplyAt = double.NegativeInfinity;
        }

        // ==================== 回能加成（tag / affliction / 天赋） ====================
        private const string ModifierPrefix = "touhoumodenergy_";

        private void RefreshModifiers(Character holder)
        {
            naturalMultiplier = 1f;
            damageMultiplier = 1f;
            if (holder == null) return;
            // 有些武器（夜晚之舞、星夜分光炮这类非改装武器）明确不吃回能加成
            if (!Settings.AllowRegenBonuses) return;

            WeaponEnergyModifierRegistry.LoadIfChanged();
            var scan = new ModifierScan(holder, Item);
            float naturalBonus = 0f;
            float damageBonus = 0f;
            foreach (var id in scan.Ids)
            {
                if (!id.Value.StartsWith(ModifierPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = id.Value.Substring(ModifierPrefix.Length);
                int split = rest.LastIndexOf('_');
                if (split <= 0 || split >= rest.Length - 1) continue;
                string kind = rest.Substring(0, split).ToLowerInvariant();
                if (!int.TryParse(rest.Substring(split + 1), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int percent)) continue;
                bool natural = kind == "natural" || kind == "all";
                bool damage = kind == "damage" || kind == "all";
                if (natural) naturalBonus += percent / 100f;
                if (damage) damageBonus += percent / 100f;
            }
            // 全局登记表：天赋/affliction 这类没有 tag 的来源在这里按 id 映射成百分比
            foreach (var entry in WeaponEnergyModifierRegistry.Entries)
            {
                if (!entry.Matches(scan.HasTag, scan.HasAffliction, scan.HasTalent)) continue;
                naturalBonus += entry.Natural / 100f;
                damageBonus += entry.Damage / 100f;
            }
            naturalMultiplier += naturalBonus;
            damageMultiplier += damageBonus;

            foreach (var modifier in Settings.RegenModifiers)
            {
                if (!modifier.Matches(scan.HasTag, scan.HasAffliction, scan.HasTalent)) continue;
                naturalMultiplier *= modifier.Natural;
                damageMultiplier *= modifier.Damage;
            }
            naturalMultiplier = Math.Clamp(naturalMultiplier, 0f, 5f);
            damageMultiplier = Math.Clamp(damageMultiplier, 0f, 5f);
        }

#if SERVER
        private sealed class ActivationEventData : ItemComponent.IEventData
        {
            internal readonly ushort Button;
            internal ActivationEventData(ushort button) { Button = button; }
        }
#endif

        private void SendState(int activationButton = -1)
        {
            sentEnergy = Energy;
            sentActive = ControlActive;
            sentCanActivate = State.CanActivate;
            syncTimer = 0f;
#if SERVER
            if (WeaponEnergyRuntime.Authority)
            {
                // 启动表现跟随这次成功确认，不能靠客户端猜模式变化，更不能重放失败点击。
                ItemComponent.IEventData action = activationButton >= 0 && activationButton < ushort.MaxValue
                    ? new ActivationEventData((ushort)(activationButton + 1)) : null;
                Item.CreateServerEvent(this, action);
            }
#endif
        }

#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
        {
            msg.WriteSingle(Energy);
            msg.WriteBoolean(ControlActive);
            msg.WriteUInt16(extraData is Item.ComponentStateEventData data &&
                data.ComponentData is ActivationEventData action ? action.Button : (ushort)0);
        }
#else
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            float energy = msg.ReadSingle();
            bool active = msg.ReadBoolean();
            ushort activationButton = msg.ReadUInt16();
            if (!float.IsFinite(energy)) return;
            State.Snapshot(energy, active);
            SnapshotReady = true;
            if (activationButton > 0 && !WeaponEnergyRuntime.Authority)
                WeaponEnergyButtonPatch.ReplayPresentation(Item, activationButton - 1);
        }
#endif
    }

}
