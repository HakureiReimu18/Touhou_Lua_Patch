--[[因幡天为天赋的随机效果（幸运白兔 TewiTalent 的攻击/治疗抽奖 + 出血效应 MRE_Tewi）。
原实现：Talents/TalentsTewi.xml、Talents/Talents_MRE.xml 的能力组触发
Events/Touhou_Special_Events.xml 里的三段 RNG 事件（旧 XML 只注释未删，事件块留着备查）。
抽到哪一项、给哪个 affliction、持续多久全部照原事件搬；只有冷却按新数值（攻击 12 秒、治疗 120 秒）。
出血效应保留原事件 33.4% 之后那层 50/50：一半抽幸运眷顾、一半抽幸运庇护。
门槛只认装束给的常驻 aff，不认装束本体。]]

-- 服务端权威：联机客户端不执行；单机和开房主机照常执行
if CLIENT and not Game.IsSingleplayer then return end

local TT = TouhouTalents

local GATE_AFFLICTION = "Touhou_Tewi_Character_Effect"
-- 原治疗组的物品条件：只有带这 tag 的魔法医疗物品才算
local MAGIC_MEDICAL_TAG = "Touhou_Magic_Medical"

-- 妖兽05「意志坚定」的 -7% 受伤靠这两条标记判定，照旧要挂
local ATTACK_FLAG = "TewiTalent_OnAttack_Effect_Flag"
local TREATMENT_FLAG = "TewiTalent_OnApplyTreatment_Effect_Flag"

-- 三个入口的概率与冷却（冷却搬进 Lua，不再用原来的 CD affliction）
local ATTACK_CHANCE, ATTACK_COOLDOWN = 0.2, 12.0        -- 原 15 秒
local TREATMENT_CHANCE, TREATMENT_COOLDOWN = 0.5, 120.0 -- 原 180 秒
local BLEED_CHANCE, BLEED_INTERVAL = 0.334, 60.0
-- 原出血事件在 33.4% 之后还有一层 50%：一半眷顾、一半庇护（天赋文本也是这么写的）
local BLEED_FAVOR_SPLIT = 0.5

-- 原事件里 strength=10，引擎按 100/最大生命缩放后再夹到 affliction 的 maxstrength
local BLESSING_STRENGTH = 10
local FLAG_STRENGTH = 1

-- 幸运眷顾：原事件是三层 0.5 嵌套，等价于前七项各 1 份、08 拿 0.95 份、隐藏项 0.05 份
local LUCKY_FAVOR = {
    { weight = 1,    affliction = "TewiTalent_Attack_Effect01" }, -- 技能水平 +10%
    { weight = 1,    affliction = "TewiTalent_Attack_Effect02" }, -- 额外生命 +10
    { weight = 1,    affliction = "TewiTalent_Attack_Effect03" }, -- 远程攻击伤害 +10%
    { weight = 1,    affliction = "TewiTalent_Attack_Effect04" }, -- 物理抗性 +10%
    { weight = 1,    affliction = "TewiTalent_Attack_Effect05" }, -- 免疫精神病类效果
    { weight = 1,    affliction = "TewiTalent_Attack_Effect06" }, -- 近战攻击伤害 +30%
    { weight = 1,    affliction = "TewiTalent_Attack_Effect07" }, -- 物理伤害以 110% 转深层组织损伤
    { weight = 0.95, affliction = "TewiTalent_Attack_Effect08" }, -- 眩晕抗性 +35%
    { weight = 0.05, affliction = "implacable" },                 -- 隐藏项：破釜沉舟
}

-- 幸运庇护：原事件两层 0.5，四项均等
local LUCKY_PROTECTION = {
    { weight = 1, affliction = "TewiTalent_OnApplyTreatment_Effect01" }, -- 受治疗量 +15%
    { weight = 1, affliction = "TewiTalent_OnApplyTreatment_Effect02" }, -- 缓慢治愈出血与烧伤
    { weight = 1, affliction = "TewiTalent_OnApplyTreatment_Effect03" }, -- 医疗 +20
    { weight = 1, affliction = "TewiTalent_OnApplyTreatment_Effect04" }, -- 抑制戒断与中毒
}

-- 出血效应的轮询节拍（不新增 affliction，纯计时）
local BLEED_SCAN_INTERVAL = 0.5

local DEBUG_LOG = false

-- LuaCs 没注册游戏内的 Timing；Timer.GetTime 拿不到时退回自累加（think 里维护）
local THINK_STEP_FALLBACK = 1.0 / 60.0
local fallback_clock = 0

local GATE_ID = TT.id(GATE_AFFLICTION)

math.randomseed(os.time())

-- 冷却到点时刻（弱键，角色回收后条目自己消失）；只在抽中时才开始算，跟原 CD affliction 一样
local attack_ready = setmetatable({}, { __mode = "k" })
local treatment_ready = setmetatable({}, { __mode = "k" })
local bleed_next = setmetatable({}, { __mode = "k" })
local next_bleed_scan = 0

local function now_time()
    if Timer ~= nil and Timer.GetTime ~= nil then
        return Timer.GetTime()
    end
    return fallback_clock
end

local function log(message)
    if DEBUG_LOG then
        print("Tewi.LuckyTalent: " .. message)
    end
end

local function has_gate(character)
    return character ~= nil and TT.get_strength(character, GATE_ID) > 0
end

-- 冷却没到点（只读判定；起算放 start_cooldown，没抽中不起算，跟原 XML 一样）
local function on_cooldown(timers, character, now)
    local ready = timers[character]
    return ready ~= nil and now < ready
end

local function start_cooldown(timers, character, now, cooldown)
    timers[character] = now + cooldown
end

-- 抽一项：身上已经有的（强度 > 0）跳过，别在持续时间里重复给同一项；全都在身上就这次不给
local function pick_blessing(character, entries)
    local candidates, total = {}, 0
    for _, entry in ipairs(entries) do
        if TT.get_strength(character, TT.id(entry.affliction)) <= 0 then
            total = total + entry.weight
            table.insert(candidates, entry)
        end
    end
    if total <= 0 then
        return nil
    end

    local roll = math.random() * total
    for _, entry in ipairs(candidates) do
        roll = roll - entry.weight
        if roll <= 0 then
            return entry.affliction
        end
    end
    return candidates[#candidates].affliction
end

local function grant_blessing(character, entries)
    local pick = pick_blessing(character, entries)
    if pick == nil then
        log(tostring(character.Name) .. " 身上已有全套效果，这次不给")
        return
    end
    if not TT.apply_affliction(character, pick, BLESSING_STRENGTH) then
        log("找不到 affliction " .. tostring(pick))
        return
    end
    log(tostring(character.Name) .. " 获得 " .. tostring(pick))
end

-- 攻击：原 XML 的 OnAttack 就是引擎在 AbilityAttackData 构造函数里查天赋（魔理沙同款挂法）。
-- 爆炸/多肢体命中会各建一次，抽中后起的冷却会挡掉同一瞬间的后续掷骰，跟原 CD affliction 一致。
Hook.Patch("Barotrauma.AbilityAttackData", ".ctor", function(instance, ptable)
    local attacker = ptable["attacker"]
    if not has_gate(attacker) or attacker.IsDead or attacker.Removed then
        return
    end

    local now = now_time()
    if on_cooldown(attack_ready, attacker, now) then
        return
    end
    if math.random() >= ATTACK_CHANCE then
        return
    end

    start_cooldown(attack_ready, attacker, now, ATTACK_COOLDOWN)
    -- 标记：妖兽05「意志坚定」的 -7% 受伤要它
    TT.apply_affliction(attacker, ATTACK_FLAG, FLAG_STRENGTH)
    grant_blessing(attacker, LUCKY_FAVOR)
end, Hook.HookMethodType.After)

-- 治疗：引擎的 OnApplyTreatment 是在 Item.ApplyTreatment 末尾查天赋（LuaCs 没有对应钩子，只能补方法）。
-- 原组是 applytoself，所以抽中后给的是使用者自己，被治疗的队友不受影响。
Hook.Patch("Barotrauma.Item", "ApplyTreatment", function(instance, ptable)
    local user = ptable["user"]
    if not has_gate(user) or user.IsDead or user.Removed then
        return
    end
    if instance == nil or not instance.HasTag(MAGIC_MEDICAL_TAG) then
        return
    end

    local now = now_time()
    if on_cooldown(treatment_ready, user, now) then
        return
    end
    if math.random() >= TREATMENT_CHANCE then
        return
    end

    start_cooldown(treatment_ready, user, now, TREATMENT_COOLDOWN)
    TT.apply_affliction(user, TREATMENT_FLAG, FLAG_STRENGTH)
    grant_blessing(user, LUCKY_PROTECTION)
end, Hook.HookMethodType.After)

-- 出血效应：原 XML 是 60 秒的 interval 组，这里按角色自己记到点时刻
local function update_bleed(character, now)
    if character == nil or character.Removed or character.IsDead or not character.IsHuman then
        return
    end
    if not has_gate(character) then
        -- 脱下装束/失去门槛：重新起算
        bleed_next[character] = nil
        return
    end

    local due = bleed_next[character]
    if due == nil then
        bleed_next[character] = now + BLEED_INTERVAL
        return
    end
    if now < due then
        return
    end
    bleed_next[character] = now + BLEED_INTERVAL

    if math.random() >= BLEED_CHANCE then
        return
    end
    -- 原事件 0.5 的 Success 支抽眷顾、Failure 支抽庇护
    local entries = LUCKY_FAVOR
    if math.random() >= BLEED_FAVOR_SPLIT then
        entries = LUCKY_PROTECTION
    end
    grant_blessing(character, entries)
end

Hook.Add("roundStart", "Tewi.LuckyTalent.RoundStart", function()
    -- 冷却时刻是拿Timer.GetTime当基准的，换巡回要清掉，免得旧数值卡住新巡回
    attack_ready = setmetatable({}, { __mode = "k" })
    treatment_ready = setmetatable({}, { __mode = "k" })
    bleed_next = setmetatable({}, { __mode = "k" })
    next_bleed_scan = 0
end)

-- think 不保证传 delta_time，所以时间基准走 Timer.GetTime，累加只当兜底
Hook.Add("think", "Tewi.LuckyTalent.Tick", function(delta_time)
    if TT.is_game_paused() then
        return
    end
    fallback_clock = fallback_clock + (delta_time or THINK_STEP_FALLBACK)

    local now = now_time()
    if now < next_bleed_scan then
        return
    end
    next_bleed_scan = now + BLEED_SCAN_INTERVAL

    for character in Character.CharacterList do
        local ok, err = pcall(update_bleed, character, now)
        if not ok then
            print("Tewi.LuckyTalent 错误: " .. tostring(err))
        end
    end
end)
