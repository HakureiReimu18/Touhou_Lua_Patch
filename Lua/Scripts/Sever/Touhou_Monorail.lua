--[[
单轨蓄力，服务端说了算：按住左键蓄、松开打，伤害和穿甲随蓄力时间涨。
蓄过 3 秒开始累积自爆风险（走 XML 的 OnBroken，Condition 清零会触发爆炸并自动同步，联机能看到画面和伤害），6 秒硬顶强制发射，抢在原版 8 秒自动开火之前。
服务器开火只调组件级 weapon.Use——服务器没音频，组件级 Use 也不发网络事件——再广播 FIRE_MESSAGE 让客户端补音效和枪口闪光、重置本地蓄力，免得预测出幽灵射击。单机走完整 item.Use，原生全包。
改装走物品 tag，跨模组跨端都认：monorail_overclock 把临界压到 2 秒，monorail_stabilizer 自爆概率乘 0.25。
]]

--[[真拿你们没办法，坐好喽
「苍」和「赫」互相碰撞就是能产生假想质量爆发的「虚式·茈」
但此时空中的「赫」与当时绕场一圈击中宿傩的那次一样，在爆炸之前速度并不算快。魔虚罗只要比「赫」先一步接触到「苍」完成适应的它就可以轻松的将这个术式消除，成功阻止「苍」和「赫」碰撞
此时已经十分接近「苍」的魔虚罗脑海中已经开始浮现「任务完成」的想法，没想到五条悟居然凭借「苍」的引力直接出现在他和「仓」之间！ 
五条悟甚至都不需要选择木式对象，因为魔虚罗对于「苍」的适应让他完全不受引力的影响，这个千年来近乎无解的能力居然在这个时候成为五条悟逆转战局的关键！
魔虚罗将退魔之剑横在面前却依然无法阻挡五条悟充满咒力的重拳，宿傩也终于在此时回到战场并摆出了「穿血」的起式，他想要在他们碰撞之前用手中射出的水流将「赫」提前引爆
五条悟瞬间出现在宿傩面前，但他的拳头却无法阻止已经射出的「穿血」！
宿傩看着即将命中「赫」的水柱不忘嘲讽自己的对手「动作太慢」五条悟却在这个时候再次开始吟唱咒词！
「位相」 「黄昏」 「智慧之瞳」！急促且简短的咒词瞬间恢复了「苍」的输出，巨大的引力让原本射向「赫」的水流方向直接偏转并在片刻之后就被全力输出的「苍」直接吸收！战场中也响起了对于宿傩来说有些陌生的咒词
「九钢」「偏光」「乌与声明」「表里之间」此时战场中再也没有任何人可以阻止「苍」和「赫」互相靠近了…  
「虚式·茈」
充斥天地的紫色光辉仿佛利剑一般刺入了所有人的双眼，当其中蕴含的可怕力量开始喷涌时这座城市便在假想质量中的爆发渐渐粉碎，曾经代表着浮世繁华的高楼与街区此刻都无法阻挡来自「最强」的无差别攻击
原本能适应一切的法阵也在紫色光芒的吞噬下彻底化为齑粉
当一切归于寂静他们所处的街区也成为一片废墟，自烟尘中踉踉跄跄走出的是已经失去了左手同时身体各处都破败不堪的宿傩，似乎是来自战场的嘲讽就连他倚靠的半堵围墙也在此刻直接倒塌
而作为他对手的五条悟也开始了颇具嘲讽意味的「战斗复盘」
五条悟：「不指定对象，连我自己都会被卷进去的无限制的「茈」……」「但是…好像受伤的程度不太一样呢」「看来是不是自己的咒力影响很大啊」「不过…结果好就行了吧」「即兴创作的远距离操作「茈」」「好像完成的还不错？」「这还是我第一次自爆呢」
「也就是说……」
「没错 是五条赢了」]]

local FULL_CHARGE_TIME       = 3    
local CRITICAL_TIME          = 3    
local HARD_CAP_TIME          = 6    -- 强制发射，必须小于 XML 的 maxchargetime
local NATIVE_MAX_CHARGE      = 8    -- 要跟 XML 的 maxchargetime 对上

local MIN_DAMAGE_MULT        = 1    
local MAX_DAMAGE_MULT        = 10   
local MIN_PENETRATION        = 0    -- 和弹药的 Attack 穿甲相加，封顶 1
local MAX_PENETRATION        = 1    

local EXPLODE_BASE_PER_SEC   = 0.10 
local EXPLODE_GROWTH_PER_SEC = 0.30 -- 每多超 1 秒，每秒概率加这么多
local EXPLODE_MAX_PER_SEC    = 0.95 

local OVERCLOCK_TAG          = "monorail_overclock"
local OVERCLOCK_CRITICAL     = 2
local STABILIZER_TAG         = "monorail_stabilizer"
local STABILIZER_CHANCE_MULT = 0.25

local FIRE_MESSAGE           = "Touhou_Monorail_fired"

LuaUserData.MakePropertyAccessible(Descriptors["Barotrauma.Items.Components.RangedWeapon"], "WeaponDamageModifier")
LuaUserData.MakePropertyAccessible(Descriptors["Barotrauma.Items.Components.RangedWeapon"], "Penetration")
LuaUserData.MakePropertyAccessible(Descriptors["Barotrauma.Items.Components.RangedWeapon"], "MaxChargeTime")
LuaUserData.MakePropertyAccessible(Descriptors["Barotrauma.Items.Components.RangedWeapon"], "ReloadTimer")
LuaUserData.MakeFieldAccessible(Descriptors["Barotrauma.Items.Components.RangedWeapon"], "currentChargeTime")

local function lerp(a, b, t) return a + (b - a) * t end
local function clamp(x, lo, hi)
    if x < lo then return lo end
    if x > hi then return hi end
    return x
end

if CLIENT then
    Networking.Receive(FIRE_MESSAGE, function(message, connection)
        local itemId = message.ReadUInt16()
        for item in Item.ItemList do
            if not item.Removed and item.ID == itemId and item.Prefab.Identifier == "Touhou_Monorail" then
                local weapon = item.GetComponentString("RangedWeapon")
                if weapon ~= nil then
                    local holder = nil
                    local inv = item.ParentInventory
                    if inv ~= nil and inv.Owner ~= nil
                            and LuaUserData.IsTargetType(inv.Owner, "Barotrauma.Character") then
                        holder = inv.Owner
                    end
                    weapon.currentChargeTime = 0       -- 停掉本地蓄力音效/粒子
                    weapon.ReloadTimer = weapon.Reload -- 跟服务器装填对齐，免得客户端预测出幽灵射击
                    weapon.PlaySound(ActionType.OnUse) -- 播 XML 里 OnUse 的开火音效
                    weapon.ApplyStatusEffects(ActionType.OnUse, 1.0, holder) -- 枪口闪光，纯视觉
                end
                break
            end
        end
    end)
end

local function GetCriticalTime(item)
    if item.HasTag(OVERCLOCK_TAG) then return OVERCLOCK_CRITICAL end
    return CRITICAL_TIME
end

local function GetExplodeChanceMultiplier(item)
    if item.HasTag(STABILIZER_TAG) then return STABILIZER_CHANCE_MULT end
    return 1
end

-- item -> { time = 已蓄力秒数 }；弱键表，物品移除后自动回收
local charging = setmetatable({}, { __mode = "k" })

Hook.Add("roundStart", "Touhou_Monorail_roundStart", function()
    charging = setmetatable({}, { __mode = "k" })
end)

local function getHolder(item)
    local inv = item.ParentInventory
    if inv == nil then return nil end
    local owner = inv.Owner
    if owner ~= nil and LuaUserData.IsTargetType(owner, "Barotrauma.Character") then
        return owner
    end
    return nil
end

local function fire(item, weapon, user, chargeRatio, deltaTime)
    weapon.WeaponDamageModifier = lerp(MIN_DAMAGE_MULT, MAX_DAMAGE_MULT, chargeRatio)
    weapon.Penetration = lerp(MIN_PENETRATION, MAX_PENETRATION, chargeRatio)
    weapon.MaxChargeTime = 0 -- 临时拆掉原生蓄力门槛，让 Use 立刻打

    if Game.IsSingleplayer then
        -- 单机走完整 item.Use，音效闪光弹药检查都归原生管
        pcall(function() item.Use(deltaTime, user) end)
    else
        -- 服务器没音频，组件级 Use 也不发网络事件，只出伤害
        pcall(function() weapon.Use(deltaTime, user) end)
        -- 广播出去，客户端本地自己补表现
        local msg = Networking.Start(FIRE_MESSAGE)
        msg.WriteUInt16(item.ID)
        Networking.Send(msg)
    end

    weapon.WeaponDamageModifier = MIN_DAMAGE_MULT
    weapon.Penetration = MIN_PENETRATION
    weapon.MaxChargeTime = NATIVE_MAX_CHARGE
end

-- 过蓄自爆：Condition 清零触发 XML 的 OnBroken（Condition 会自动同步给客户端，联机能看到爆炸画面和伤害），1 秒后自动修回去
local function explode(item)
    local prevCondition = item.Condition
    item.Condition = 0
    Timer.Wait(function()
        if item ~= nil and not item.Removed then
            item.Condition = prevCondition
        end
    end, 1000)
end

Hook.Add("Touhou_Monorail_charge", "Touhou_Monorail_charge", function(effect, deltaTime, item, targets, worldPosition)
    -- 只让单人/服务器跑，免得客户端重复判定
    if CLIENT and not Game.IsSingleplayer then return end

    local weapon = item.GetComponentString("RangedWeapon")
    if weapon == nil then return end

    local user = getHolder(item)
    local state = charging[item]

    if user == nil or user.Removed or user.IsDead then
        charging[item] = nil
        return
    end

    local aiming = user.IsKeyDown(InputType.Aim)
    local shooting = user.IsKeyDown(InputType.Shoot)

    if state == nil then
        if aiming and shooting and weapon.ReloadTimer <= 0 then
            charging[item] = { time = 0 }
        end
        return
    end

    if aiming and shooting then
        if weapon.ReloadTimer > 0 then return end -- 装填中就先停着
        state.time = state.time + deltaTime

        if state.time >= HARD_CAP_TIME then
            -- 硬上限：抢在原版自动开火前满蓄力打出去，没弹就拉倒
            if weapon.FindProjectile(false) ~= nil then
                fire(item, weapon, user, 1, deltaTime)
            end
            charging[item] = nil
            return
        end

        local criticalTime = GetCriticalTime(item)
        if state.time > criticalTime then
            -- 过蓄了，按帧 roll 自爆，拖得越久越容易炸
            local chancePerSec = clamp(
                EXPLODE_BASE_PER_SEC + EXPLODE_GROWTH_PER_SEC * (state.time - criticalTime),
                0, EXPLODE_MAX_PER_SEC) * GetExplodeChanceMultiplier(item)
            if math.random() < chancePerSec * deltaTime then
                charging[item] = nil
                explode(item)
            end
        end
    else
        -- 松手就按当前蓄力比例打，没弹直接取消，免得放空炮
        if aiming and not shooting and weapon.ReloadTimer <= 0 and weapon.FindProjectile(false) ~= nil then
            local ratio = clamp(state.time / FULL_CHARGE_TIME, 0, 1)
            fire(item, weapon, user, ratio, deltaTime)
        end
        charging[item] = nil
    end
end)
