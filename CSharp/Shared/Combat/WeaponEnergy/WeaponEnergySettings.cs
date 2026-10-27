// 武器能量配置：XML 属性与效果元素（AttackAffliction / StunEffect / SedationEffect /
// RegenModifier / OnActivate / OnDeactivate）的解析，以及能量状态机。
using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;

namespace Touhou.WeaponEnergy
{
    // 元素的生效时机：未激活 / 激活中 / 都生效。对应缄默的“普通减速 / 强化减速 / 眩晕麻醉”。
    internal enum EffectMode { Always, Normal, Enhanced }

    // 命中目标时附加的普通 affliction（可多条）。护甲默认生效；ignorearmor 才直赋。
    internal sealed class AttackAfflictionDef
    {
        internal Identifier Affliction;
        internal float Strength = 100f;
        internal float Probability = 1f;
        internal EffectMode Mode = EffectMode.Always;
        internal bool IgnoreArmor;
        // 对应原版 multiplyAfflictionsByMaxVitality（默认 false）：
        // true = 强度 × MaxVitality/100（高血量目标也吃固定数值）；false = 原版百分比语义。
        internal bool MultiplyByMaxVitality;
        // 对应原版 StatusEffect 的 setvalue：false（默认）= 叠加，true = 每次覆盖强度。
        internal bool SetValue;
    }

    // 硬控：只对非人类/非机械/非 Boss/非眩晕免疫生效（沿用 WeaponEnergyRules）。
    internal sealed class StunEffectDef
    {
        internal float Duration = 1.5f;
        internal float Cooldown = 4f;
        internal Identifier CooldownAffliction;
        internal bool HardControl = true;
        internal EffectMode Mode = EffectMode.Always;
    }

    // 麻醉：累加到阈值触发眩晕，清零并给恢复期（缄默原逻辑元素化）。
    internal sealed class SedationEffectDef
    {
        internal Identifier Affliction;
        internal float Threshold = 100f;
        internal float Normal = 30f;
        internal float Enhanced = 60f;
        internal float StunDuration = 10f;
        internal float Recovery = 6f;
        internal Identifier RecoveryAffliction;
        internal float LargeTargetVitality = 1000f;
        internal float LargeTargetMultiplier = 0.5f;
        internal EffectMode Mode = EffectMode.Always;
    }

    // 回能加成：三种条件可单用也可同时用（同时=与），命中则乘倍率。
    internal sealed class RegenModifierDef
    {
        internal float Natural = 1f;
        internal float Damage = 1f;
        internal Identifier Tag;
        internal Identifier Affliction;
        internal Identifier Talent;

        internal static bool IsSet(Identifier id) => !string.IsNullOrEmpty(id.Value);

        internal bool Matches(Func<Identifier, bool> hasTag, Func<Identifier, bool> hasAffliction,
            Func<Identifier, bool> hasTalent)
        {
            if (IsSet(Tag) && !hasTag(Tag)) return false;
            if (IsSet(Affliction) && !hasAffliction(Affliction)) return false;
            if (IsSet(Talent) && !hasTalent(Talent)) return false;
            return true;
        }
    }

    internal sealed class WeaponEnergySettings
    {
        internal float MaxEnergy { get; init; } = 100f;
        internal float ActivationCost { get; init; }
        internal float MinimumActivationEnergy { get; init; } = 15f;
        internal float DrainPerSecond { get; init; } = 5f;
        internal float ShotCost { get; init; } = 15f;
        internal float ToggleCooldown { get; init; } = 0.25f;
        internal bool NaturalRecharge { get; init; } = true;
        internal float RechargePerSecond { get; init; } = 12f;   // 可为负：一直掉能量，只能靠伤害回能
        internal float RechargeDelay { get; init; } = 3f;
        internal bool RechargeWhileHolstered { get; init; }
        internal bool RechargeWhileActive { get; init; }
        internal bool AllowRegenBonuses { get; init; } = true;   // false=不吃 tag/aff/天赋/登记表的回能加成
        internal float ActiveRegenMultiplier { get; init; } = 0.5f; // 激活期间能量“获取”的总倍率（防无限改装）
        internal float HitRechargeAmount { get; init; }
        internal float HitRechargeCooldown { get; init; } = 0.25f;
        internal bool HitRechargeWhileActive { get; init; }
        internal bool HitRechargeHostileOnly { get; init; } = true;
        internal bool HitRechargeRequireHeld { get; init; } = true;
        internal bool HitRechargeOnMachines { get; init; }
        internal bool HitRechargeOncePerProjectile { get; init; } = true;
        internal bool ShowEnergyBar { get; init; } = true;
        internal bool EnableSlow { get; init; }
        internal string NormalSlowAffliction { get; init; } = "Touhou_Silence_Suppression";
        internal string EnhancedSlowAffliction { get; init; } = "Touhou_Silence_ControlSuppression";
        internal float SlowDuration { get; init; } = 2f;
        internal bool EnableStun { get; init; }
        internal float StunDuration { get; init; } = 1.5f;
        internal float StunCooldown { get; init; } = 4f;
        internal bool EnableSedation { get; init; }
        internal float NormalSedation { get; init; } = 30f;
        internal float EnhancedSedation { get; init; } = 60f;
        internal float SedationThreshold { get; init; } = 100f;
        internal float SedationStunDuration { get; init; } = 10f;
        internal float SedationRecovery { get; init; } = 6f;
        internal float LargeTargetVitality { get; init; } = 1000f;
        internal float LargeTargetSedationMultiplier { get; init; } = 0.5f;
        internal float NonLethalDamageMultiplier { get; init; } = 1f;

        // ===== 通用武器能量 =====
        internal float ActivationDuration { get; init; }             // >0：激活到点自动结束
        internal bool ToggleOff { get; init; }                       // 激活中再按一次关闭（默认关）
        internal float DamageRegenPerDamage { get; init; } = 0.1f;
        internal float DamageRegenMin { get; init; } = 1f;
        internal float DamageRegenMax { get; init; } = 10f;
        internal float DamageRegenWindow { get; init; } = 0.25f;     // 同窗口内的多弹丸/多肢体算一次
        internal bool DamageRegenOnMachines { get; init; }
        internal Identifier HolderAffliction { get; init; } = default;
        internal float HolderStrength { get; init; } = 100f;
        internal float HolderDelay { get; init; }
        internal float HolderRefresh { get; init; }
        internal bool HolderRemoveOnHolster { get; init; } = true;
        internal string BarTitle { get; init; } = "";
        internal string BarReady { get; init; } = "touhou.energy.ready";
        internal string BarActive { get; init; } = "touhou.energy.active";
        internal string BarStandby { get; init; } = "touhou.energy.standby";
        internal string BarSyncing { get; init; } = "touhou.energy.syncing";
        internal string BarColorIdle { get; init; } = "";
        internal string BarColorActive { get; init; } = "";

        internal List<AttackAfflictionDef> AttackAfflictions { get; } = new List<AttackAfflictionDef>();
        internal List<StunEffectDef> StunEffects { get; } = new List<StunEffectDef>();
        internal List<SedationEffectDef> SedationEffects { get; } = new List<SedationEffectDef>();
        internal List<RegenModifierDef> RegenModifiers { get; } = new List<RegenModifierDef>();
        // 开启/结束时执行的 XML StatusEffect：给弹匣打 tag、加武器 tag、放音效这类一次性动作，
        // 不依赖按钮链，也就不会因为“按钮链只在开启时跑”而漏掉关闭时的还原。
        internal List<StatusEffect> OnActivateEffects { get; } = new List<StatusEffect>();
        internal List<StatusEffect> OnDeactivateEffects { get; } = new List<StatusEffect>();

        internal static WeaponEnergySettings Read(ContentXElement element)
        {
            WeaponEnergySettings settings = Read((name, fallback) => element.GetAttributeString(name, fallback));
            foreach (ContentXElement child in element.Elements())
            {
                switch (child.Name.ToString().ToLowerInvariant())
                {
                    case "attackaffliction": settings.ReadAttackAffliction(child); break;
                    case "stuneffect": settings.ReadStunEffect(child); break;
                    case "sedationeffect": settings.ReadSedationEffect(child); break;
                    case "regenmodifier": settings.ReadRegenModifier(child); break;
                    case "onactivate": ReadHooks(child, settings.OnActivateEffects); break;
                    case "ondeactivate": ReadHooks(child, settings.OnDeactivateEffects); break;
                }
            }
            return settings;
        }

        internal static WeaponEnergySettings Read(Func<string, string, string> attribute)
        {
            float Number(string name, float fallback, float min = 0f, float max = 1000000f)
            {
                string text = attribute(name, "");
                float value = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                    && float.IsFinite(parsed) ? parsed : fallback;
                return Math.Clamp(value, min, max);
            }
            bool Flag(string name, bool fallback)
                => bool.TryParse(attribute(name, ""), out bool value) ? value : fallback;
            string Id(string name, string fallback)
            {
                string value = attribute(name, fallback)?.Trim();
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            float capacity = Number("maxenergy", 100f, 0.01f);
            float shotCost = Number("shotcost", 15f, max: capacity);
            var settings = new WeaponEnergySettings
            {
                MaxEnergy = capacity,
                ShotCost = shotCost,
                ActivationCost = Number("activationcost", 0f, max: capacity - shotCost),
                MinimumActivationEnergy = Number("minimumactivationenergy", shotCost, max: capacity),
                DrainPerSecond = Number("drainpersecond", 5f),
                ToggleCooldown = Number("togglecooldown", 0.25f, max: 60f),
                NaturalRecharge = Flag("naturalrecharge", true),
                RechargePerSecond = Number("rechargepersecond", 12f, -1000f, 1000f),
                AllowRegenBonuses = Flag("allowregenbonuses", true),
                ActiveRegenMultiplier = Number("activeregenmultiplier", 0.5f, 0f, 10f),
                RechargeDelay = Number("rechargedelay", 3f, max: 3600f),
                RechargeWhileHolstered = Flag("rechargewhileholstered", false),
                RechargeWhileActive = Flag("rechargewhileactive", false),
                HitRechargeAmount = Number("hitrechargeamount", 0f, max: capacity),
                HitRechargeCooldown = Number("hitrechargecooldown", 0.25f, max: 3600f),
                HitRechargeWhileActive = Flag("hitrechargewhileactive", false),
                HitRechargeHostileOnly = Flag("hitrechargehostileonly", true),
                HitRechargeRequireHeld = Flag("hitrechargerequireheld", true),
                HitRechargeOnMachines = Flag("hitrechargeonmachines", false),
                HitRechargeOncePerProjectile = Flag("hitrechargeonceperprojectile", true),
                ShowEnergyBar = Flag("showenergybar", true),
                EnableSlow = Flag("enableslow", false),
                NormalSlowAffliction = Id("normalslowaffliction", "Touhou_Silence_Suppression"),
                EnhancedSlowAffliction = Id("enhancedslowaffliction", "Touhou_Silence_ControlSuppression"),
                // 原生状态同步只有 8 位，减速计时上限不能拉得太大。
                SlowDuration = Number("slowduration", 2f, max: 30f),
                EnableStun = Flag("enablestun", false),
                StunDuration = Number("stunduration", 1.5f, max: 3600f),
                StunCooldown = Number("stuncooldown", 4f, max: 3600f),
                EnableSedation = Flag("enablesedation", false),
                NormalSedation = Number("normalsedation", 30f),
                EnhancedSedation = Number("enhancedsedation", 60f),
                SedationThreshold = Number("sedationthreshold", 100f, 0.01f),
                SedationStunDuration = Number("sedationstunduration", 10f, max: 3600f),
                SedationRecovery = Number("sedationrecovery", 6f, max: 3600f),
                LargeTargetVitality = Number("largetargetvitality", 1000f),
                LargeTargetSedationMultiplier = Number("largetargetsedationmultiplier", 0.5f, max: 1f),
                NonLethalDamageMultiplier = Number("nonlethaldamagemultiplier", 1f, max: 1f),
                ActivationDuration = Number("activeduration", 0f, max: 3600f),
                ToggleOff = Flag("toggleoff", false),
                DamageRegenPerDamage = Number("damageregenperdamage", 0.1f),
                DamageRegenMin = Number("damageregenmin", 1f),
                DamageRegenMax = Number("damageregenmax", 10f),
                DamageRegenWindow = Number("damageregenwindow", 0.25f, max: 5f),
                DamageRegenOnMachines = Flag("damageregenonmachines", false),
                HolderAffliction = Id("holderafficliction", "").ToIdentifier(),
                HolderStrength = Number("holderstrength", 100f),
                HolderDelay = Number("holderdelay", 0f, max: 60f),
                HolderRefresh = Number("holderrefresh", 0f, max: 3600f),
                HolderRemoveOnHolster = Flag("holderremoveonholster", true),
                BarTitle = Id("bartitle", ""),
                BarReady = Id("barready", "touhou.energy.ready"),
                BarActive = Id("baractive", "touhou.energy.active"),
                BarStandby = Id("barstandby", "touhou.energy.standby"),
                BarSyncing = Id("barsyncing", "touhou.energy.syncing"),
                BarColorIdle = Id("barcoloridle", ""),
                BarColorActive = Id("barcoloractive", ""),
            };
            settings.AddLegacyEffects();
            return settings;
        }

        // 旧写法（enableslow/enablestun/enablesedation + 一串参数）转成通用元素，行为不变。
        // 新内容直接写元素即可，这里只是兼容层。
        private void AddLegacyEffects()
        {
            if (EnableSlow && SlowDuration > 0f)
            {
                AttackAfflictions.Add(new AttackAfflictionDef
                {
                    Affliction = NormalSlowAffliction.ToIdentifier(),
                    Strength = SlowDuration,
                    Mode = EffectMode.Normal,
                    IgnoreArmor = true,
                });
                AttackAfflictions.Add(new AttackAfflictionDef
                {
                    Affliction = EnhancedSlowAffliction.ToIdentifier(),
                    Strength = SlowDuration,
                    Mode = EffectMode.Enhanced,
                    IgnoreArmor = true,
                });
            }
            if (EnableStun)
            {
                StunEffects.Add(new StunEffectDef
                {
                    Duration = StunDuration,
                    Cooldown = StunCooldown,
                    CooldownAffliction = "Touhou_Silence_ControlRecovery".ToIdentifier(),
                    HardControl = true,
                });
            }
            if (EnableSedation)
            {
                SedationEffects.Add(new SedationEffectDef
                {
                    Affliction = "Touhou_Silence_Sedation".ToIdentifier(),
                    Threshold = SedationThreshold,
                    Normal = NormalSedation,
                    Enhanced = EnhancedSedation,
                    StunDuration = SedationStunDuration,
                    Recovery = SedationRecovery,
                    RecoveryAffliction = "Touhou_Silence_SleepRecovery".ToIdentifier(),
                    LargeTargetVitality = LargeTargetVitality,
                    LargeTargetMultiplier = LargeTargetSedationMultiplier,
                });
            }
        }

        private static void ReadHooks(ContentXElement parent, List<StatusEffect> list)
        {
            foreach (ContentXElement child in parent.Elements())
            {
                if (!child.Name.ToString().Equals("StatusEffect", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    list.Add(StatusEffect.Load(child, "TouhouWeaponEnergy"));
                }
                catch
                {
                    // 单条效果写错不该让整把武器的组件加载失败
                }
            }
        }

        private static EffectMode ParseMode(string text)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "normal": return EffectMode.Normal;
                case "enhanced": return EffectMode.Enhanced;
                default: return EffectMode.Always;
            }
        }

        private static float ClampNumber(ContentXElement element, string name, float fallback, float min, float max)
        {
            float value = element.GetAttributeFloat(name, float.NaN);
            if (!float.IsFinite(value)) value = fallback;
            return Math.Clamp(value, min, max);
        }

        private void ReadAttackAffliction(ContentXElement element)
        {
            string id = element.GetAttributeString("identifier", "").Trim();
            if (string.IsNullOrEmpty(id)) return;
            AttackAfflictions.Add(new AttackAfflictionDef
            {
                Affliction = id.ToIdentifier(),
                Strength = ClampNumber(element, "strength", 100f, 0f, 1000000f),
                Probability = ClampNumber(element, "probability", 1f, 0f, 1f),
                Mode = ParseMode(element.GetAttributeString("when", "always")),
                IgnoreArmor = ParseBool(element, "ignorearmor", false),
                MultiplyByMaxVitality = ParseBool(element, "multiplyafflictionsbymaxvitality", false),
                SetValue = ParseBool(element, "setvalue", false),
            });
        }

        private void ReadStunEffect(ContentXElement element)
        {
            string cooldownId = element.GetAttributeString("cooldownaffliction", "").Trim();
            StunEffects.Add(new StunEffectDef
            {
                Duration = ClampNumber(element, "duration", 1.5f, 0f, 3600f),
                Cooldown = ClampNumber(element, "cooldown", 4f, 0f, 3600f),
                CooldownAffliction = string.IsNullOrEmpty(cooldownId) ? default : cooldownId.ToIdentifier(),
                HardControl = ParseBool(element, "hardcontrol", true),
                Mode = ParseMode(element.GetAttributeString("when", "always")),
            });
        }

        private void ReadSedationEffect(ContentXElement element)
        {
            string id = element.GetAttributeString("identifier", "").Trim();
            if (string.IsNullOrEmpty(id)) return;
            string recoveryId = element.GetAttributeString("recoveryaffliction", "").Trim();
            SedationEffects.Add(new SedationEffectDef
            {
                Affliction = id.ToIdentifier(),
                Threshold = ClampNumber(element, "threshold", 100f, 0.01f, 1000000f),
                Normal = ClampNumber(element, "normal", 30f, 0f, 1000000f),
                Enhanced = ClampNumber(element, "enhanced", 60f, 0f, 1000000f),
                StunDuration = ClampNumber(element, "stunduration", 10f, 0f, 3600f),
                Recovery = ClampNumber(element, "recovery", 6f, 0f, 3600f),
                RecoveryAffliction = string.IsNullOrEmpty(recoveryId) ? default : recoveryId.ToIdentifier(),
                LargeTargetVitality = ClampNumber(element, "largetargetvitality", 1000f, 0f, 1000000f),
                LargeTargetMultiplier = ClampNumber(element, "largetargetmultiplier", 0.5f, 0f, 1f),
                Mode = ParseMode(element.GetAttributeString("when", "always")),
            });
        }

        private void ReadRegenModifier(ContentXElement element)
        {
            string tag = element.GetAttributeString("tag", "").Trim();
            string affliction = element.GetAttributeString("affliction", "").Trim();
            string talent = element.GetAttributeString("talent", "").Trim();
            RegenModifiers.Add(new RegenModifierDef
            {
                Natural = ClampNumber(element, "natural", 1f, 0f, 100f),
                Damage = ClampNumber(element, "damage", 1f, 0f, 100f),
                Tag = string.IsNullOrEmpty(tag) ? default : tag.ToIdentifier(),
                Affliction = string.IsNullOrEmpty(affliction) ? default : affliction.ToIdentifier(),
                Talent = string.IsNullOrEmpty(talent) ? default : talent.ToIdentifier(),
            });
        }

        private static bool ParseBool(ContentXElement element, string name, bool fallback)
            => bool.TryParse(element.GetAttributeString(name, ""), out bool value) ? value : fallback;
    }

    // 数值和状态机放一起，离线也能检查耗能边界。
    internal sealed class WeaponEnergyState
    {
        internal WeaponEnergySettings Settings { get; }
        internal float Energy { get; private set; }
        internal bool Active { get; private set; }
        internal float Delay { get; private set; }

        internal WeaponEnergyState(float energy, WeaponEnergySettings settings = null)
        {
            Settings = settings ?? new WeaponEnergySettings();
            Restore(energy);
        }

        internal void Restore(float energy)
            => Energy = float.IsFinite(energy) ? Math.Clamp(energy, 0f, Settings.MaxEnergy) : 0f;

        internal void Snapshot(float energy, bool active)
        {
            Restore(energy);
            // 一次性扣满能量的改装，0 能量也可以仍在持续；模式以服务端为准。
            Active = active;
        }

        internal bool CanActivate => Energy >= Math.Max(Settings.MinimumActivationEnergy, Settings.ActivationCost + Settings.ShotCost) &&
            Energy >= Settings.ActivationCost;

        internal bool Toggle(bool usable)
        {
            if (Active)
            {
                Stop();
                return true;
            }
            // 注意是 < 不是 <=：满能量启动（cost == max == 100）必须能过。
            if (!usable || !CanActivate) return false;
            Energy -= Settings.ActivationCost;
            Active = true;
            Delay = Settings.RechargeDelay;
            return true;
        }

        internal void Stop()
        {
            if (Active) Delay = Settings.RechargeDelay;
            Active = false;
        }

        internal bool PayShot()
        {
            if (!Active) return false;
            // shotcost=0 的改装武器：开火不扣能量，也不能因为“能量已经是 0”就关掉模式
            if (Settings.ShotCost > 0f)
            {
                if (Energy < Settings.ShotCost)
                {
                    Stop();
                    return false;
                }
                Energy = Math.Max(0f, Energy - Settings.ShotCost);
                Delay = Settings.RechargeDelay;
                if (Energy <= 0f) Stop();
            }
            return true;
        }

        // 伤害/道具回能（组件已经按窗口和上下限算好数值）。
        internal void AddEnergy(float amount)
        {
            if (!float.IsFinite(amount) || amount <= 0f) return;
            Energy = Math.Min(Settings.MaxEnergy, Energy + amount);
        }

        // 这是枪自己的模式能量，不读弹药，也不替弹药充电。
        internal void Tick(float dt, bool held, bool operational = true, float naturalMultiplier = 1f)
        {
            if (!float.IsFinite(dt) || dt <= 0f) return;
            if (!operational) { Stop(); return; }
            if (!held) Stop();
            if (Active)
            {
                Delay = Settings.RechargeDelay;
                // 只有“持续耗能”才在能量耗尽时自动关闭；一次性扣满的改装（drain=0、shotcost=0）
                // 启动后能量就是 0，不能因此立刻结束，否则使用者 aff/时长效果都不会发生。
                if (Settings.DrainPerSecond > 0f)
                {
                    Energy = Math.Max(0f, Energy - Settings.DrainPerSecond * dt);
                    if (Energy <= 0f) { Stop(); return; }
                }
                if (Settings.NaturalRecharge && Settings.RechargeWhileActive)
                {
                    float rate = Settings.RechargePerSecond;
                    // 只缩放“获取”；负速率（持续流失）不受这条倍率影响
                    if (rate > 0f) rate *= Settings.ActiveRegenMultiplier;
                    Energy = Math.Clamp(Energy + rate * dt * Math.Max(0f, naturalMultiplier), 0f, Settings.MaxEnergy);
                }
                return;
            }
            float elapsedDelay = Math.Min(Delay, dt);
            Delay = Math.Max(0f, Delay - dt);
            if (!Settings.NaturalRecharge || (!held && !Settings.RechargeWhileHolstered)) return;
            Energy = Math.Clamp(
                Energy + Settings.RechargePerSecond * (dt - elapsedDelay) * Math.Max(0f, naturalMultiplier),
                0f, Settings.MaxEnergy);
        }

        internal float RecoverFromHit(bool enhancedShot)
        {
            if ((Active || enhancedShot) && !Settings.HitRechargeWhileActive) return 0f;
            float added = Math.Min(Settings.HitRechargeAmount, Settings.MaxEnergy - Energy);
            if (Active) added *= Settings.ActiveRegenMultiplier;
            Energy += Math.Max(0f, added);
            return added;
        }
    }

    internal static class WeaponEnergyRules
    {
        internal static bool ProtectFromHardControl(bool human, bool humanoid, bool player)
            => human || humanoid || player;

        internal static bool AllowHardControl(bool protectedTarget, bool machine, bool boss, bool stunImmune)
            => !protectedTarget && !machine && !boss && !stunImmune;

        internal static bool EligibleRechargeTarget(bool wasAlive, bool self, bool friendly, bool machine, WeaponEnergySettings settings)
            => wasAlive && !self && (!settings.HitRechargeHostileOnly || !friendly) &&
                (settings.HitRechargeOnMachines || !machine);
    }
}
