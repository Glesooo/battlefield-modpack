--- START OF FILE text/plain ---

local track_line_top = {value = 0}
local static_track_top = {value = 0}
local blending_track_top = {value = 0}

local function increment(obj)
    obj.value = obj.value + 1
    return obj.value - 1
end

local STATIC_TRACK_LINE = increment(track_line_top)
local BASE_TRACK = increment(static_track_top)
local MAIN_TRACK = increment(static_track_top)

local BLENDING_TRACK_LINE = increment(track_line_top)
local MOVEMENT_TRACK = increment(blending_track_top)

-----------------------------------------------------------
-- 核心逻辑变量
-----------------------------------------------------------
local main_track_states = {
    start = {},
    idle = {},
    inspect = {},
    final = {},
    attack_index = 0,
    model_mode = 0 -- 0: Devil, 1: Angel
}

local movement_track_states = {
    idle = {},
    run = { mode = -1, time = 0 },
    walk = { mode = -1 }
}

-- 动画名拼接辅助函数
local function getAnim(baseName)
    if main_track_states.model_mode == 1 then
        return baseName .. "_angel"
    end
    return baseName
end

-- 【新增】安全可见性锁定逻辑 (双保险)
-- 即使动画关键帧漏掉了一帧，这里也会每秒20次强行修正显示
local function safeVisibilityGuard(context)
    local is_angel = (main_track_states.model_mode == 1)
    local setVis = context.setMeshVisible or context.setGroupVisible
    if setVis then
        pcall(function()
            setVis(context, "devil", not is_angel)
            setVis(context, "angel", is_angel)
        end)
    end
end

-----------------------------------------------------------
-- 基础轨道 (Base Track)
-----------------------------------------------------------
local base_track_state = {}
function base_track_state.entry(this, context)
    context:runAnimation(getAnim("static_idle"), context:getTrack(STATIC_TRACK_LINE, BASE_TRACK), false, LOOP, 0)
end

function base_track_state.update(this, context)
    -- 每帧执行一次安全检查，确保模型永远显示正确
    safeVisibilityGuard(context)
end

-----------------------------------------------------------
-- 主轨道动作
-----------------------------------------------------------
function main_track_states.start.update(this, context)
    context:trigger(INPUT_DRAW)
end

function main_track_states.start.transition(this, context, input)
    if (input == INPUT_DRAW) then
        local drawNum = math.random(0, 1)
        local baseDrawName = (drawNum > 0) and "draw_1" or "draw"
        context:runAnimation(getAnim(baseDrawName), context:getTrack(STATIC_TRACK_LINE, MAIN_TRACK), false, PLAY_ONCE_STOP, 0)
        return main_track_states.idle
    end
end

function main_track_states.idle.transition(this, context, input)
    if (input == INPUT_PUT_AWAY) then
        local put_away_time = context:getPutAwayTime()
        local track = context:getTrack(STATIC_TRACK_LINE, MAIN_TRACK)
        context:runAnimation(getAnim("put_away"), track, false, PLAY_ONCE_HOLD, put_away_time * 0.75)
        context:setAnimationProgress(track, 1, true)
        context:adjustAnimationProgress(track, -put_away_time, false)
        return main_track_states.final
    elseif (input == INPUT_INSPECT) then
        local i = math.random(0, 1)
        local baseInspectName = (i > 0) and "inspect_1" or "inspect"
        context:runAnimation(getAnim(baseInspectName), context:getTrack(STATIC_TRACK_LINE, MAIN_TRACK), false, PLAY_ONCE_STOP, 0.2)
        return main_track_states.idle
    elseif input == "attack_left" then
        local counter = main_track_states.attack_index
        local baseMeleeName = "melee_" .. tostring(counter + 1)
        main_track_states.attack_index = (counter + 1) % 2
        context:runAnimation(getAnim(baseMeleeName), context:getTrack(STATIC_TRACK_LINE, MAIN_TRACK), false, PLAY_ONCE_STOP, 0.2)
        return main_track_states.idle
    elseif input == "attack_right" then
        context:runAnimation(getAnim("melee_3"), context:getTrack(STATIC_TRACK_LINE, MAIN_TRACK), false, PLAY_ONCE_STOP, 0.2)
        return main_track_states.idle
    end
end

-----------------------------------------------------------
-- 移动轨道动作
-----------------------------------------------------------
function movement_track_states.idle.update(this, context)
    local track = context:getTrack(BLENDING_TRACK_LINE, MOVEMENT_TRACK)
    if (context:isStopped(track) or context:isHolding(track)) then
        context:runAnimation(getAnim("idle"), track, true, LOOP, 0)
    end
end

function movement_track_states.idle.transition(this, context, input)
    if (input == INPUT_RUN) then
        if (context:isStopped(context:getTrack(STATIC_TRACK_LINE, MAIN_TRACK))) then
            return movement_track_states.run -- 修复点：直接引用，不要用 this
        else
            return movement_track_states.walk
        end
    elseif (input == INPUT_WALK) then
        return movement_track_states.walk
    end
end

function movement_track_states.run.entry(this, context)
    movement_track_states.run.mode = -1
    movement_track_states.run.time = context:getCurrentTimestamp()
    context:runAnimation(getAnim("run_start"), context:getTrack(BLENDING_TRACK_LINE, MOVEMENT_TRACK), true, PLAY_ONCE_HOLD, 0.2)
end

function movement_track_states.run.exit(this, context)
    context:runAnimation(getAnim("run_end"), context:getTrack(BLENDING_TRACK_LINE, MOVEMENT_TRACK), true, PLAY_ONCE_HOLD, 0.3)
end

function movement_track_states.run.update(this, context)
    local track = context:getTrack(BLENDING_TRACK_LINE, MOVEMENT_TRACK)
    local state = movement_track_states.run
    if (context:isHolding(track)) then
        context:runAnimation(getAnim("run"), track, true, LOOP, 0.2)
        state.mode = 0
        context:anchorWalkDist()
    end
    if (state.mode ~= -1) then
        if (not context:isOnGround()) then
            if (state.mode ~= 1) then
                state.mode = 1
                context:runAnimation(getAnim("run_hold"), track, true, LOOP, 0.6)
            end
        else
            if (state.mode ~= 0) then
                state.mode = 0
                context:runAnimation(getAnim("run"), track, true, LOOP, 0.2)
            end
            context:setAnimationProgress(track, (context:getWalkDist() % 2.0) / 2.0, true)
        end
    end
end

function movement_track_states.run.transition(this, context, input)
    if (input == INPUT_IDLE) then
        return movement_track_states.idle
    elseif (input == INPUT_WALK or not context:isStopped(context:getTrack(STATIC_TRACK_LINE, MAIN_TRACK))) then
        return movement_track_states.walk
    end
end

function movement_track_states.walk.entry(this, context)
    movement_track_states.walk.mode = -1
end

function movement_track_states.walk.exit(this, context)
    context:runAnimation(getAnim("idle"), context:getTrack(BLENDING_TRACK_LINE, MOVEMENT_TRACK), true, PLAY_ONCE_HOLD, 0.4)
end

function movement_track_states.walk.update(this, context)
    local track = context:getTrack(BLENDING_TRACK_LINE, MOVEMENT_TRACK)
    local state = movement_track_states.walk
    if (not context:isOnGround()) then
        if (state.mode ~= 0) then
            state.mode = 0
            context:runAnimation(getAnim("idle"), track, true, LOOP, 0.6)
        end
    elseif (context:isInputUp()) then
        if (state.mode ~= 2) then
            state.mode = 2
            context:runAnimation(getAnim("walk_forward"), track, true, LOOP, 0.4)
            context:anchorWalkDist()
        end
    elseif (context:isInputDown()) then
        if (state.mode ~= 3) then
            state.mode = 3
            context:runAnimation(getAnim("walk_backward"), track, true, LOOP, 0.4)
            context:anchorWalkDist()
        end
    elseif (context:isInputLeft() or context:isInputRight()) then
        if (state.mode ~= 4) then
            state.mode = 4
            context:runAnimation(getAnim("walk_sideway"), track, true, LOOP, 0.4)
            context:anchorWalkDist()
        end
    end
    if (state.mode >= 1 and state.mode <= 4) then
        context:setAnimationProgress(track, (context:getWalkDist() % 2.0) / 2.0, true)
    end
end

function movement_track_states.walk.transition(this, context, input)
    if (input == INPUT_IDLE) then
        return movement_track_states.idle
    elseif (input == INPUT_RUN) then
        if (context:isStopped(context:getTrack(STATIC_TRACK_LINE, MAIN_TRACK))) then
            return movement_track_states.run
        end
    end
end

-----------------------------------------------------------
-- 初始化与导出
-----------------------------------------------------------
local M = {
    track_line_top = track_line_top,
    STATIC_TRACK_LINE = STATIC_TRACK_LINE,
    base_track_state = base_track_state,
    main_track_states = main_track_states,
    movement_track_states = movement_track_states,
}

function M:initialize(context)
    context:ensureTrackLineSize(track_line_top.value)
    context:ensureTracksAmount(STATIC_TRACK_LINE, static_track_top.value)
    context:ensureTracksAmount(BLENDING_TRACK_LINE, blending_track_top.value)
    
    -- 核心：初始化时随机
    main_track_states.model_mode = math.random(0, 1)
    
    movement_track_states.run.mode = -1
    movement_track_states.walk.mode = -1
end

function M:states()
    return {
        self.base_track_state,
        self.main_track_states.start,
        self.movement_track_states.idle,
    }
end

return M