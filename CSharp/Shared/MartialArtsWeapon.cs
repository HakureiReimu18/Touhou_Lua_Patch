using Barotrauma;
using Barotrauma.Items.Components;

namespace Touhou.MartialArts
{
    /// <summary>
    /// 武术武器组件：只管"动作层"调参（出招方式/力度/时长/攻击点）。
    /// 伤害在武器 &lt;MeleeWeapon&gt;&lt;attack&gt; 的 affliction 里，命中范围在 &lt;Body&gt; 碰撞体上，
    /// 攻速走 reload，全是原版机制，不用打补丁。引擎按"元素名=类名"自动实例化，用法：
    /// &lt;MartialArtsWeapon strike="punch" force="4" torque="2.5" duration="0.2" reach="0.95" height="-0.2"/&gt;
    /// </summary>
    public class MartialArtsWeapon : ItemComponent
    {
        /// 招式：kick 正蹬腿 / punch 直拳 / uppercut 上勾拳
        [Serialize("punch", IsPropertySaveable.No)]
        public string Strike { get; set; }

        /// 往攻击点拉的冲量，每帧结算，个位数就够
        [Serialize(4f, IsPropertySaveable.No)]
        public float Force { get; set; }

        /// 转向扭矩，自旋就来自这，给小
        [Serialize(2.5f, IsPropertySaveable.No)]
        public float Torque { get; set; }

        /// 攻击动作时长（秒）
        [Serialize(0.2f, IsPropertySaveable.No)]
        public float Duration { get; set; }

        /// 攻击点前伸距离（米，相对躯干），决定肢体前伸幅度
        [Serialize(0.95f, IsPropertySaveable.No)]
        public float Reach { get; set; }

        /// 攻击点高度偏移（米，相对躯干；0≈胸口，负值偏下）
        [Serialize(-0.2f, IsPropertySaveable.No)]
        public float Height { get; set; }

        public MartialArtsWeapon(Item item, ContentXElement element) : base(item, element) { }
    }
}
