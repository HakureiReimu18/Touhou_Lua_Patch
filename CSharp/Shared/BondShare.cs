using System;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace Touhou.Bond
{
    // 纯圈外人不懂，谁能告诉我在2018年12月24日，北纬35度41分，东经139度42分，充满咒灵废墟的战场，最强与最强的对决，当时日本东京新宿区发生了什么？
    // 承伤曲线 = 核心挑函数族、芯片给参数，无状态
    public static class BondShare
    {
        public const string CHARM_TAG = "Bond_Charm";

        enum Core { Linear, Logistic, Sine, Power }

        // 扫全部装备槽找带 Bond_Charm 标签的，耐久耗光算没戴
        public static Item FindCharm(Character c)
        {
            if (c?.Inventory is not CharacterInventory inv) return null;
            int slotCount = Math.Min(inv.Capacity, inv.SlotTypes.Length);
            for (int i = 0; i < slotCount; i++)
            {
                var slotType = inv.SlotTypes[i];
                if (slotType == InvSlotType.Any || slotType == InvSlotType.None ||
                    slotType.HasFlag(InvSlotType.LeftHand) || slotType.HasFlag(InvSlotType.RightHand))
                    continue;
                var item = inv.GetItemAt(i);
                if (item != null && item.HasTag(CHARM_TAG) &&
                    (item.MaxCondition <= 0f || item.Condition > 0f))
                    return item;
            }
            return null;
        }

        // 承担份额 f，0~1；结算时还会钳到 [MinShare, MaxShare]
        public static float Share(Item charm, Character wearer, float windowDamage, double now)
        {
            var (core, amp, k, period) = ReadCoreChip(charm);
            float f;
            switch (core)
            {
                case Core.Linear:
                    // 均契：恒定份额
                    f = amp;
                    break;

                case Core.Logistic:
                {
                    // 庇护：S 曲线，血越多扛得越多，平台期让渡
                    float h = (wearer?.CharacterHealth?.HealthPercentage ?? 100f) / 100f;
                    float th = (float)Math.Tanh(k * (2.0 * h - 1.0));
                    f = 0.5f + amp * th / (float)Math.Tanh(k);
                    break;
                }

                case Core.Sine:
                {
                    // 潮汐：正弦呼吸，相位从护符 ID 派生，每件错相
                    double phase = (charm?.ID ?? 0) % 997 / 997.0 * period;
                    f = 0.5f + amp * (float)Math.Sin(2.0 * Math.PI * (now + phase) / period);
                    break;
                }

                case Core.Power:
                default:
                {
                    // 蓄能：按本窗口承伤量走幂函数，n<1 大伤分摊变缓（反爆发）
                    float d = Math.Min(windowDamage / Math.Max(BondConfig.PowerDRef, 0.01f), 1f);
                    f = amp * (float)Math.Pow(d, BondConfig.PowerN);
                    break;
                }
            }
            return MathHelper.Clamp(f, BondConfig.MinShare, BondConfig.MaxShare);
        }

        // 读容器槽 0（核心）和槽 1（芯片）。identifier 规则见 BondCharm.xml，后缀数字档位别乱改
        static (Core core, float amp, float k, float period) ReadCoreChip(Item charm)
        {
            var core = Core.Linear; float amp = 0.5f, k = 3f, period = 15f;
            var inv = charm?.OwnInventory;
            if (inv == null) return (core, amp, k, period);

            var coreItem = inv.GetItemAt(0);
            if (coreItem != null)
            {
                string id = coreItem.Prefab.Identifier.Value;
                if (id.StartsWith("touhou_bond_core_", StringComparison.OrdinalIgnoreCase))
                {
                    string family = id.Substring("touhou_bond_core_".Length).ToLowerInvariant();
                    switch (family)
                    {
                        case "logistic": core = Core.Logistic; amp = 0.4f; break;
                        case "sine": core = Core.Sine; amp = 0.4f; break;
                        case "power": core = Core.Power; amp = 0.4f; break;
                        default: core = Core.Linear; amp = 0.5f; break;
                    }
                }
            }

            var chipItem = inv.GetItemAt(1);
            if (chipItem != null)
            {
                string id = chipItem.Prefab.Identifier.Value.ToLowerInvariant();
                if (id.StartsWith("touhou_bond_chip_", StringComparison.Ordinal) && id.Length > "touhou_bond_chip_".Length)
                {
                    char family = id["touhou_bond_chip_".Length];
                    int tier = id[id.Length - 1] - '0'; // Mk.I/II/III → 1/2/3
                    switch (family)
                    {
                        case 'a': amp = tier == 1 ? 0.3f : tier == 3 ? 0.5f : 0.4f; break;
                        case 'k': k = tier == 1 ? 2f : tier == 3 ? 5f : 3f; break;
                        case 't': period = tier == 1 ? 10f : tier == 3 ? 25f : 15f; break;
                    }
                }
            }
            return (core, amp, k, period);
        }
    }
}
