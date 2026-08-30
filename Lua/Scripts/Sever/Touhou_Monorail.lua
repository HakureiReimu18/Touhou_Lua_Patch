--[[
单轨（Touhou_Monorail）蓄力系统 —— 服务端权威

操作方式：按住右键瞄准，按住左键蓄力，松开左键发射。
- 伤害与穿甲随蓄力时间线性提升，FULL_CHARGE_TIME 秒蓄满；
- 蓄力超过临界时间（CRITICAL_TIME，默认 3 秒）后，每帧按概率自爆，
  超过越久概率越高，自爆通过 XML 的 OnBroken 爆炸实现，对持握者及周围造成伤害；
- 到达 HARD_CAP_TIME（6 秒）强制满蓄力发射，保证原生 maxchargetime（8 秒）的自动开火永不触发。

配套 XML 要点（Items/Item.xml）：
- RangedWeapon maxchargetime="8" 与 ChargeSound/ParticleEmitterCharge：
  原生蓄力状态机负责蓄力音效与粒子。联机时各客户端通过同步的按键状态本地预测武器使用，
  因此所有客户端都能听到蓄力声（这也是 XML 中不能再禁用 isshootable 的原因）；
- 实际蓄力进度、松手发射、过蓄自爆判定由本脚本（服务端）控制；
- OnBroken 爆炸 StatusEffect：脚本将 Condition 置 0 触发，1 秒后自动修复，
  Condition 变化会自动同步到客户端，因此自爆的爆炸画面与伤害联机可用。

联机同步说明：
- 服务器开火只调用组件级 weapon.Use（服务器无音频，且不产生任何网络事件）；
- 开火后广播 FIRE_MESSAGE，所有客户端收到后在本地播放开火音效、枪口闪光，
  并重置本地原生蓄力状态（防止客户端预测出不存在的“幽灵射击”）；
- 单机走完整的 item.Use 路径（音效/枪口闪光/弹药检查原生完成），不发送网络消息。

武器改装预留（通过物品 tag，可跨模组/跨端传递）：
- monorail_overclock ：临界时间缩短至 2 秒
- monorail_stabilizer：自爆概率降为 1/4
]]

local FULL_CHARGE_TIME       = 3    -- 蓄满所需秒数（伤害与穿甲达到上限）
local CRITICAL_TIME          = 3    -- 临界时间（秒）：超过后开始累积自爆风险
local HARD_CAP_TIME          = 6    -- 强制发射时间（秒），必须小于 XML 的 maxchargetime
local NATIVE_MAX_CHARGE      = 8    -- 必须与 XML 的 maxchargetime 保持一致

local MIN_DAMAGE_MULT        = 1    -- 未蓄力时的伤害倍率
local MAX_DAMAGE_MULT        = 10   -- 蓄满时的伤害倍率
local MIN_PENETRATION        = 0    -- 未蓄力时的武器穿甲（与弹药 Attack 穿甲相加，上限 1）
local MAX_PENETRATION        = 1    -- 蓄满时的武器穿甲

local EXPLODE_BASE_PER_SEC   = 0.10 -- 刚超过临界时，每秒自爆概率
local EXPLODE_GROWTH_PER_SEC = 0.30 -- 每多超过 1 秒，每秒概率的增加量
local EXPLODE_MAX_PER_SEC    = 0.95 -- 每秒概率上限

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

-- 客户端：收到开火广播后，在本地补齐开火表现（音效 + 枪口闪光）并重置蓄力表现
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
                    weapon.currentChargeTime = 0       -- 停止本地蓄力音效/粒子
                    weapon.ReloadTimer = weapon.Reload -- 与服务器装填节奏保持一致，防止客户端预测出幽灵射击
                    weapon.PlaySound(ActionType.OnUse) -- 播放 XML 中 type="OnUse" 的开火音效
                    weapon.ApplyStatusEffects(ActionType.OnUse, 1.0, holder) -- 枪口闪光（仅视觉）
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

-- 获取当前持握该物品的角色（未持握时返回 nil）
local function getHolder(item)
    local inv = item.ParentInventory
    if inv == nil then return nil end
    local owner = inv.Owner
    if owner ~= nil and LuaUserData.IsTargetType(owner, "Barotrauma.Character") then
        return owner
    end
    return nil
end

-- 以指定蓄力比例发射（0~1），发射后恢复武器默认参数
local function fire(item, weapon, user, chargeRatio, deltaTime)
    weapon.WeaponDamageModifier = lerp(MIN_DAMAGE_MULT, MAX_DAMAGE_MULT, chargeRatio)
    weapon.Penetration = lerp(MIN_PENETRATION, MAX_PENETRATION, chargeRatio)
    weapon.MaxChargeTime = 0 -- 临时解除原生蓄力门槛，使 Use 立即发射

    if Game.IsSingleplayer then
        -- 单机：完整物品级路径，原生处理开火音效、枪口闪光与弹药检查
        pcall(function() item.Use(deltaTime, user) end)
    else
        -- 联机服务器：仅权威伤害（服务器无音频，组件级 Use 不产生网络事件）
        pcall(function() weapon.Use(deltaTime, user) end)
        -- 广播给所有客户端，由客户端本地补齐开火表现
        local msg = Networking.Start(FIRE_MESSAGE)
        msg.WriteUInt16(item.ID)
        Networking.Send(msg)
    end

    weapon.WeaponDamageModifier = MIN_DAMAGE_MULT
    weapon.Penetration = MIN_PENETRATION
    weapon.MaxChargeTime = NATIVE_MAX_CHARGE
end

-- 过蓄自爆：Condition 置 0 触发 XML 的 OnBroken 爆炸，随后自动修复
-- Condition 变化会自动同步到客户端，联机下客户端能看到爆炸画面与伤害
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
    -- 服务端权威：仅单人或服务器执行，避免客户端重复判定
    if CLIENT and not Game.IsSingleplayer then return end

    local weapon = item.GetComponentString("RangedWeapon")
    if weapon == nil then return end

    local user = getHolder(item)
    local state = charging[item]

    -- 无人持握 / 使用者死亡或移除：取消蓄力
    if user == nil or user.Removed or user.IsDead then
        charging[item] = nil
        return
    end

    local aiming = user.IsKeyDown(InputType.Aim)
    local shooting = user.IsKeyDown(InputType.Shoot)

    if state == nil then
        -- 开始蓄力：瞄准中 + 按住左键 + 不在装填
        if aiming and shooting and weapon.ReloadTimer <= 0 then
            charging[item] = { time = 0 }
        end
        return
    end

    if aiming and shooting then
        -- 蓄力中
        if weapon.ReloadTimer > 0 then return end -- 装填期间蓄力暂停
        state.time = state.time + deltaTime

        if state.time >= HARD_CAP_TIME then
            -- 硬上限：强制满蓄力发射（抢在原生自动开火之前，无弹药则直接取消）
            if weapon.FindProjectile(false) ~= nil then
                fire(item, weapon, user, 1, deltaTime)
            end
            charging[item] = nil
            return
        end

        local criticalTime = GetCriticalTime(item)
        if state.time > criticalTime then
            -- 过蓄：按帧 roll 自爆，概率随超过时间增长
            local chancePerSec = clamp(
                EXPLODE_BASE_PER_SEC + EXPLODE_GROWTH_PER_SEC * (state.time - criticalTime),
                0, EXPLODE_MAX_PER_SEC) * GetExplodeChanceMultiplier(item)
            if math.random() < chancePerSec * deltaTime then
                charging[item] = nil
                explode(item)
            end
        end
    else
        -- 松开左键且仍保持瞄准：按当前蓄力比例发射（无弹药时直接取消，避免空放音效）
        if aiming and not shooting and weapon.ReloadTimer <= 0 and weapon.FindProjectile(false) ~= nil then
            local ratio = clamp(state.time / FULL_CHARGE_TIME, 0, 1)
            fire(item, weapon, user, ratio, deltaTime)
        end
        -- 其余情况（松开瞄准、切枪等）：取消蓄力
        charging[item] = nil
    end
end)
