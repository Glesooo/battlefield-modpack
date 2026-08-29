-- QUICK NOTE!
-- I dont know Lua and wrote this script using my general programming knowledge
-- and by analyzing default pack scripts and asking Chatbots for help.
-- So if some part of script looks sketchy, just know that maybe i did some mistakes

-- importing default state machine script
local default = require("tacz_default_state_machine")

-- defining static track line. It's a track line on which all weapon action tracks are put on
local static_track_top = default.static_track_top
local STATIC_TRACK_LINE = default.STATIC_TRACK_LINE

-- I have no idea what is this doing but it's needed to create new tracks
local function increment(obj)
    obj.value = obj.value + 1
    return obj.value - 1
end
-- defining my own track for windowed mag animations
local WMAG_TRACK = increment(static_track_top)

-- defining switches
local wmag_states = {
    enabled = {}, -- windowed mag is installed
    disabled = {} -- windowed mag is not installed
}

-- defining function to check if windowed mag attachment is being used
local function isWmag(context)
	-- getting the magazine attachment ID and checking if it starts with "wmags:"
	-- returning true if it is and false if it isn't or there is no magazine attachment
	return context:getAttachment("EXTENDED_MAG") and string.match(context:getAttachment("EXTENDED_MAG"), "tacz:sniper_extended_mag_3") ~= nil
end

-- checking if the attachment was installed
function wmag_states.disabled.update(this, context)
    if (isWmag(context)) then
        context:trigger(this.INPUT_WMAG_ENABLED)
    end
end

-- switching to installed status
function wmag_states.disabled.transition(this, context, input)
    if (input == this.INPUT_WMAG_ENABLED) then
        return this.wmag_states.enabled
    end
end

-- starting animation which shows windowed mag and hides default one
function wmag_states.enabled.entry(this, context)
	this.wmagTrack = context:getTrack(STATIC_TRACK_LINE, WMAG_TRACK)
	context:runAnimation("window_mag", this.wmagTrack, false, LOOP, 0)
end

-- checking if the attachment was uninstalled
function wmag_states.enabled.update(this, context)
    if (not isWmag(context)) then
        context:trigger(this.INPUT_WMAG_DISABLED)
	else
		-- if attachment is still installed, constantly monitoring and setting ammo amount
		context:setAnimationProgress(this.wmagTrack,0.1+(context:getMaxAmmoCount()-(context:getAmmoCount()))*0.5,false)
    end
end

-- switching to attachment uninstalled status 
function wmag_states.enabled.transition(this, context, input)
    if (input == this.INPUT_WMAG_DISABLED) then
        return this.wmag_states.disabled
	end
end

-- stopping the windowed mag animation
-- WARNING!
-- If you'll try removing this part by moving stopAnimation() to the transition (like in bolt_caught_states from default script),
-- it may cause a shit ton of animation bugs, so its better to use exit method
function wmag_states.enabled.exit(this, context)
    context:stopAnimation(this.wmagTrack)
end

-- and this part is some returning values shit or idk
local M = setmetatable({
	WMAG_TRACK = WMAG_TRACK,
	wmag_states = wmag_states,
	INPUT_WMAG_ENABLED = "wmag_enabled",
	INPUT_WMAG_DISABLED = "wmag_disabled"
}, {__index = default})

function M:initialize(context)
    default.initialize(self, context)
end

function M:states()
local default_states = default:states()
    table.insert(
		default_states,
		self.wmag_states.disabled
	)
    return default_states
end

return M