using System;
using System.Linq;

namespace Touhou.EngineerTransitions
{
    // 新增一个原版职业异传的步骤：
    // 1. Jobs/ 加两个 hidden Job；Talents/ 加可编辑的完整树和天赋，不在运行时拼树。
    // 2. Items/ 加两本异传书；Text/ 加名称与描述。
    // 3. 在本文件的 All 里加一条 TransitionProfile（BaseJob / PrimarySubtree / Job / Branch / Book / Skill / SaveKey）。
    // 4. 主流程不用改：书本识别、技能门槛、树就绪检查、jobtransit 命令和 HasJob 兼容补丁都按 profile 走。
    //
    // 一个原版职业的异传配置。以后加新职业只在这里加一条 profile，
    // 书本识别、技能门槛和提示都会跟着走。
    internal sealed class TransitionProfile
    {
        public string Key;              // 日志/缓存键
        public string Name;             // 中文职业名，用于提示
        public string BaseJob;          // 原版职业 identifier
        public string PrimarySubtree;   // 原版基础子树 identifier

        public string ArcaneJob;        // 魔法侧 Job
        public string MysticJob;        // 神秘侧 Job
        public string ArcaneBranch;     // 模板里的魔法侧子树
        public string MysticBranch;     // 模板里的神秘侧子树

        public string ArcaneBook;       // 魔法侧异传书 item identifier
        public string MysticBook;       // 神秘侧异传书 item identifier
        public string ArcaneBookName;   // 提示用书名
        public string MysticBookName;

        public string BaseSkill;        // 可选：原版职业技能门槛（书配方之外再兜一层）
        public float BaseSkillLevel;
        public string ArcaneSkill;      // 魔法侧额外技能门槛
        public float ArcaneSkillLevel;
        public string MysticSkill;      // 神秘侧额外技能门槛
        public float MysticSkillLevel;

        public string SaveKey;          // CampaignMetadata 键

        public bool IsTarget(string id) => id == ArcaneJob || id == MysticJob;

        public bool IsArcane(string target) => target == ArcaneJob;

        public string TargetOfBook(string book)
        {
            if (string.Equals(book, ArcaneBook, StringComparison.OrdinalIgnoreCase)) return ArcaneJob;
            if (string.Equals(book, MysticBook, StringComparison.OrdinalIgnoreCase)) return MysticJob;
            return null;
        }

        public string BranchForTarget(string target) =>
            target == ArcaneJob ? ArcaneBranch : target == MysticJob ? MysticBranch : null;

        public string BookForTarget(string target) =>
            target == ArcaneJob ? ArcaneBook : target == MysticJob ? MysticBook : null;

        public string BookNameForTarget(string target) =>
            target == ArcaneJob ? ArcaneBookName : target == MysticJob ? MysticBookName : null;

        public string SkillForTarget(string target) =>
            target == ArcaneJob ? ArcaneSkill : target == MysticJob ? MysticSkill : null;

        public float SkillLevelForTarget(string target) =>
            target == ArcaneJob ? ArcaneSkillLevel : target == MysticJob ? MysticSkillLevel : 0;
    }

    internal static class TransitionProfiles
    {
        public static readonly TransitionProfile Engineer = new TransitionProfile
        {
            Key = "engineer",
            Name = "工程师",
            BaseJob = "engineer",
            PrimarySubtree = "engineer_primary",
            ArcaneJob = "engineer_arcane",
            MysticJob = "engineer_mystic",
            ArcaneBranch = "arcane_engineer",
            MysticBranch = "mystic_engineer",
            ArcaneBook = "Touhou_Engineer_Arcane_Book",
            MysticBook = "Touhou_Engineer_Mystic_Book",
            ArcaneBookName = "电荷异传书",
            MysticBookName = "放射异传书",
            ArcaneSkill = "Touhou_Magic",
            ArcaneSkillLevel = 80,
            MysticSkill = "medical",
            MysticSkillLevel = 60,
            SaveKey = "touhou.engineertransitions.v1"
        };

        public static readonly TransitionProfile Captain = new TransitionProfile
        {
            Key = "captain",
            Name = "舰长",
            BaseJob = "captain",
            PrimarySubtree = "captain_primary",
            ArcaneJob = "captain_arcane",
            MysticJob = "captain_mystic",
            ArcaneBranch = "astral_captain",
            MysticBranch = "abyssal_captain",
            ArcaneBook = "Touhou_Captain_Arcane_Book",
            MysticBook = "Touhou_Captain_Mystic_Book",
            ArcaneBookName = "星图异传书",
            MysticBookName = "深渊异传书",
            BaseSkill = "helm",
            BaseSkillLevel = 80,
            ArcaneSkill = "Touhou_Magic",
            ArcaneSkillLevel = 60,
            MysticSkill = "weapons",
            MysticSkillLevel = 60,
            SaveKey = "touhou.captaintransitions.v1"
        };

        public static readonly TransitionProfile Security = new TransitionProfile
        {
            Key = "securityofficer",
            Name = "安全官",
            BaseJob = "securityofficer",
            PrimarySubtree = "security_primary",
            ArcaneJob = "securityofficer_arcane",
            MysticJob = "securityofficer_mystic",
            ArcaneBranch = "magic_bullet_hunter",
            MysticBranch = "abyss_hunter",
            ArcaneBook = "Touhou_Security_Arcane_Book",
            MysticBook = "Touhou_Security_Mystic_Book",
            ArcaneBookName = "魔弹异传书",
            MysticBookName = "猎渊异传书",
            // 先沿用现有异传的档位，之后只在这里改使用门槛。
            BaseSkill = "weapons",
            BaseSkillLevel = 80,
            ArcaneSkill = "Touhou_Magic",
            ArcaneSkillLevel = 60,
            MysticSkill = "weapons",
            MysticSkillLevel = 80,
            SaveKey = "touhou.securitytransitions.v1"
        };

        public static readonly TransitionProfile Mechanic = new TransitionProfile
        {
            Key = "mechanic",
            Name = "机修工",
            BaseJob = "mechanic",
            PrimarySubtree = "mechanic_primary",
            ArcaneJob = "mechanic_arcane",
            MysticJob = "mechanic_mystic",
            ArcaneBranch = "arcane_mechanic",
            MysticBranch = "fractured_artisan",
            ArcaneBook = "Touhou_Mechanic_Arcane_Book",
            MysticBook = "Touhou_Mechanic_Mystic_Book",
            ArcaneBookName = "机关异传书",
            MysticBookName = "破碎异传书",
            BaseSkill = "mechanical",
            BaseSkillLevel = 80,
            ArcaneSkill = "Touhou_Magic",
            ArcaneSkillLevel = 60,
            MysticSkill = "weapons",
            MysticSkillLevel = 60,
            SaveKey = "touhou.mechanictransitions.v1"
        };

        public static readonly TransitionProfile[] All = { Engineer, Captain, Security, Mechanic };

        public static TransitionProfile ForTarget(string target) =>
            target == null ? null : All.FirstOrDefault(profile => profile.IsTarget(target));

        public static TransitionProfile ForBook(string book) =>
            book == null ? null : All.FirstOrDefault(profile =>
                string.Equals(book, profile.ArcaneBook, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(book, profile.MysticBook, StringComparison.OrdinalIgnoreCase));

        public static TransitionProfile ForBaseJob(string job) =>
            job == null ? null : All.FirstOrDefault(profile => profile.BaseJob == job);

        public static bool IsTarget(string target) => ForTarget(target) != null;
    }
}
