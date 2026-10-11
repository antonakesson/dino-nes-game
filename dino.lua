-- dino.lua: headless verify for output/dino.nes, run by FCEUX.
--
--   timeout 60 ../fceux-win64/fceux64.exe -lua "$PWD/dino.lua" output/dino.nes
--
-- 0. Title: screen 0, "DINO" / "PRESS START" drawn, no score, dino standing still; A does
--    nothing; Start goes to the game screen with the title text gone. Saves output/title.png.
-- 1. Game start: playing, dino on the ground, ground line in the nametable, score on screen.
-- 2. Jumps: for several A hold lengths, peak and air time match a Lua model of the same
--    8.8 physics + jump cut, exactly; longer holds jump higher; holding A never re-jumps.
-- 2a. Jetpack: B on the ground does nothing; A + B jumps match the Lua model with thrust,
--    fuel and ceiling, exactly (peak, air time, fuel burnt); the flame shows only while
--    thrusting; the fuel bar is full / empty when the fuel is; fuel refills on the ground.
--    Saves output/jetpack.png.
-- 2b. Snakes (planted in RAM): falling onto one removes it, bounces, +50; holding A bounces
--    higher than letting go; walking into one bites (game over), then A restarts.
-- 3. No input: an obstacle arrives and ends the run inside its hit box; everything freezes.
-- 4. Press A: a fresh run (score 0, no obstacles).
-- 5. Bot: presses A when an obstacle is close, holds it to landing; must survive to top speed.
-- The score on screen (nametable row 2, cols 25-29) matches RAM in 1, 3, 4 and 5.
-- Writes output/dino.txt (PASS/FAIL), output/dino.png (mid-jump over a cactus),
-- output/snake.png (a snake coming) and output/jetpack.png, then exits.

local dir = debug.getinfo(1, "S").source:sub(2):match("^(.*[/\\])") or "./"
local out = dir .. "output/"

local syms = {}
for _, f in ipairs({ "dino.nes.0.nl", "dino.nes.ram.nl" }) do
  for line in io.lines(out .. f) do
    local addr, name = line:match("^%$(%x+)#([^#]*)#")
    if addr then syms[name] = tonumber(addr, 16) end
  end
end

local log, fails = {}, {}
local function note(fmt, ...) log[#log + 1] = string.format(fmt, ...) end
local function expect(ok, fmt, ...) if not ok then fails[#fails + 1] = string.format(fmt, ...) end end
local function rd(name, k) return memory.readbyte(syms[name] + (k or 0)) end
local function hold(buttons, n)
  for i = 1, n do joypad.set(1, buttons); emu.frameadvance() end
end

local function dinoY() return rd("dino_y", 1) end
local function state() return rd("state") end
local function onGround() return rd("on_ground") end
local function score()
  local s = ""
  for i = 0, 4 do s = s .. rd("score", i) end
  return tonumber(s)
end
local function shownScore()       -- the digits in the nametable, or nil if not all digits
  local s = ""
  for i = 0, 4 do
    local d = ppu.readbyte(0x2000 + 2 * 32 + 25 + i) - 12
    if d < 0 or d > 9 then return nil end
    s = s .. d
  end
  return tonumber(s)
end
local function speed()            -- px/frame, positive
  local raw = rd("speed") + 256 * rd("speed", 1)
  return (65536 - raw) / 256
end
local function obstacles()        -- every active obstacle: { x, snake }
  local os = {}
  for i = 0, 2 do
    if rd("obst" .. i .. "_on") ~= 0 then
      os[#os + 1] = { x = rd("obst" .. i .. "_x", 1), snake = rd("obst" .. i .. "_kind") == 1 }
    end
  end
  return os
end
local function cacti()            -- x of every active obstacle
  local xs = {}
  for _, o in ipairs(obstacles()) do xs[#xs + 1] = o.x end
  return xs
end
local function plantSnake(x)      -- obstacle 0 <- a snake at x
  memory.writebyte(syms.obst0_on, 1)
  memory.writebyte(syms.obst0_kind, 1)
  memory.writebyte(syms.obst0_x, 0)
  memory.writebyte(syms.obst0_x + 1, x)
end

emu.speedmode("maximum")

-- 0. title
local function rowText(row, col, n)   -- non-blank tiles in a nametable row segment
  local k = 0
  for i = 0, n - 1 do if ppu.readbyte(0x2000 + row * 32 + col + i) ~= 0 then k = k + 1 end end
  return k
end
local function titleShown() return rowText(10, 14, 4) == 4 and rowText(13, 11, 11) == 10 end
hold({}, 30)
local y0, legs0 = dinoY(), rd("legs")
hold({}, 30)
note("title      screen=%d text=%s score shown=%s y=%d", rd("screen"), tostring(titleShown()),
  tostring(shownScore()), dinoY())
expect(rd("screen") == 0, "screen %d after reset, want 0 (title)", rd("screen"))
expect(titleShown(), "title text missing from nametable rows 10 / 13")
expect(shownScore() == nil, "score drawn on the title screen")
expect(dinoY() == 176 and y0 == 176 and rd("legs") == legs0, "dino moved on the title screen")
gui.savescreenshotas(out .. "title.png")
hold({ A = true }, 1)
hold({}, 10)
expect(rd("screen") == 0, "A left the title screen")
hold({ start = true }, 1)
hold({}, 5)
note("start      screen=%d title text left=%s", rd("screen"), tostring(rowText(10, 14, 4) + rowText(13, 11, 11)))
expect(rd("screen") == 1, "Start: screen %d, want 1 (game)", rd("screen"))
expect(rowText(10, 0, 32) + rowText(13, 0, 32) == 0, "title text still on the game screen")

-- 1. game start
hold({}, 25)
note("boot       state=%d y=%d on_ground=%d score=%d", state(), dinoY(), onGround(), score())
expect(state() == 0, "state %d after boot, want 0 (playing)", state())
expect(dinoY() == 176 and onGround() == 1, "dino at y=%d on_ground=%d, want 176 / 1", dinoY(), onGround())
local groundTile, aboveTile = ppu.readbyte(0x2000 + 24 * 32 + 5), ppu.readbyte(0x2000 + 23 * 32 + 5)
expect(shownScore() == score(), "score on screen %s after boot, RAM %d", tostring(shownScore()), score())
expect(groundTile == 11 and aboveTile == 0, "nametable rows 23/24 = %d/%d, want 0/11", aboveTile, groundTile)

-- 2. jumps, against the same physics in Lua (raw 8.8): vy = -5.5, +0.35/frame, cap 6,
-- land at 176; once A is let go, vy is cut to -2.0 if it is rising faster.
-- `held` = frames A is down, counting the press frame (which runs the first step).
local function model(held)
  local y, vy, peak, steps = 176 * 256, -1408, 176 * 256, 0
  repeat
    steps = steps + 1
    if steps > held and vy < -512 then vy = -512 end
    vy = math.min(vy + 90, 6 * 256)
    y = y + vy
    peak = math.min(peak, y)
  until y >= 176 * 256
  return math.floor(peak / 256), steps
end

-- keep cacti away while testing jumps
local function holdNoCacti(buttons, n)
  for i = 1, n do
    memory.writebyte(syms.spawn_timer, 200)
    joypad.set(1, buttons)
    emu.frameadvance()
  end
end

-- from the ground: A down for `held` frames, then up. Air time = frames that end
-- above the ground + the landing frame (independent of which frame runs step 1).
local function jump(held)
  holdNoCacti({}, 2)
  local minY, above, f = 255, 0, 0
  repeat
    f = f + 1
    holdNoCacti({ A = f <= held }, 1)
    minY = math.min(minY, dinoY())
    if dinoY() < 176 then above = above + 1 end
  until (onGround() == 1 and above > 0) or f > 200
  return minY, above + 1
end

local lastPeak = 255
for _, held in ipairs({ 1, 4, 8, 12, 40 }) do
  local peak, air = jump(held)
  local wantPeak, wantAir = model(held)
  note("jump A x%-2d peak y=%d (model %d), %d frames in the air (model %d)", held, peak, wantPeak, air, wantAir)
  expect(peak == wantPeak and air == wantAir, "A held %d: peak %d / air %d, model says %d / %d", held, peak, air, wantPeak, wantAir)
  expect(peak <= lastPeak, "A held %d jumps lower (y=%d) than a shorter hold (y=%d)", held, peak, lastPeak)
  expect(dinoY() == 176, "landed at y=%d, want 176", dinoY())
  lastPeak = peak
end

-- holding A does not jump again on landing
holdNoCacti({}, 2)
local takeoffs, was = 0, onGround()
for i = 1, 120 do
  holdNoCacti({ A = true }, 1)
  if was == 1 and onGround() == 0 then takeoffs = takeoffs + 1 end
  was = onGround()
end
holdNoCacti({}, 1)
note("hold A     120 frames: %d takeoff(s)", takeoffs)
expect(takeoffs == 1, "holding A for 120 frames took off %d times, want 1", takeoffs)

-- 2a. jetpack. Model: as above, then while B is held, fuel is left, the dino top is at
-- y >= 36 and vy >= -2.0: vy -= 0.6 (raw 154), floored at -2.0, one unit of fuel burnt.
local function modelJet(heldA, heldB, fuel)
  local y, vy, peak, steps = 176 * 256, -1408, 176 * 256, 0
  repeat
    steps = steps + 1
    if steps > heldA and vy < -512 then vy = -512 end
    vy = math.min(vy + 90, 6 * 256)
    if steps <= heldB and fuel > 0 and math.floor(y / 256) >= 36 and vy >= -512 then
      vy = math.max(vy - 154, -512)
      fuel = fuel - 1
    end
    y = y + vy
    peak = math.min(peak, y)
  until y >= 176 * 256
  return math.floor(peak / 256), steps, fuel
end

local function meterTiles()
  local t = {}
  for i = 0, 3 do t[i + 1] = ppu.readbyte(0x2000 + 2 * 32 + 7 + i) end
  return t
end
local function flameShown() return memory.readbyte(0x200 + 17 * 4) < 0xEF end

-- refuel first (on the ground, no input)
holdNoCacti({}, 140)
local fullTiles = meterTiles()
note("fuel       %d, meter tiles %d %d %d %d, FUEL label %s", rd("fuel"), fullTiles[1], fullTiles[2],
  fullTiles[3], fullTiles[4], tostring(rowText(2, 1, 4) == 4))
expect(rd("fuel") == 64, "fuel %d after 140 frames on the ground, want 64", rd("fuel"))
expect(rowText(2, 1, 4) == 4, "FUEL label missing")
expect(fullTiles[1] ~= 0 and fullTiles[1] == fullTiles[4], "full meter: tiles %d..%d, want 4 equal", fullTiles[1], fullTiles[4])

-- B on the ground: nothing
local wasFlame = false
for i = 1, 30 do holdNoCacti({ B = true }, 1); wasFlame = wasFlame or flameShown() end
expect(onGround() == 1 and rd("fuel") == 64 and not wasFlame,
  "B on the ground: on_ground=%d fuel=%d flame=%s", onGround(), rd("fuel"), tostring(wasFlame))
holdNoCacti({}, 2)

-- jumps with thrust: A held heldA frames, B held heldB frames (both from the press frame)
local shotJet = false
local function jet(heldA, heldB)
  holdNoCacti({}, 140)                       -- full tank
  local fuel0 = rd("fuel")
  local minY, above, f, minFuel, flames, emptyTiles = 255, 0, 0, fuel0, 0, nil
  repeat
    f = f + 1
    holdNoCacti({ A = f <= heldA, B = f <= heldB }, 1)
    minY = math.min(minY, dinoY())
    if dinoY() < 176 then above = above + 1 end
    if flameShown() then flames = flames + 1 end
    if rd("fuel") == 0 and minFuel == 0 then emptyTiles = meterTiles() end  -- empty for 2+ frames
    minFuel = math.min(minFuel, rd("fuel"))
    if not shotJet and flameShown() and dinoY() < 120 then
      gui.savescreenshotas(out .. "jetpack.png"); shotJet = true
    end
  until (onGround() == 1 and above > 0) or f > 400
  local wantPeak, wantAir, wantFuel = modelJet(heldA, heldB, fuel0)
  note("jet A x%-2d B x%-3d peak y=%d (model %d), %d frames in the air (model %d), fuel %d -> %d (model %d), flame %d frames",
    heldA, heldB, minY, wantPeak, above + 1, wantAir, fuel0, minFuel, wantFuel, flames)
  expect(minY == wantPeak and above + 1 == wantAir and minFuel == wantFuel,
    "jet A %d B %d: peak %d / air %d / fuel %d, model says %d / %d / %d",
    heldA, heldB, minY, above + 1, minFuel, wantPeak, wantAir, wantFuel)
  expect(flames == fuel0 - minFuel, "flame shown %d frames, fuel burnt %d", flames, fuel0 - minFuel)
  return minFuel, emptyTiles
end
jet(1, 20)                                   -- short burn
jet(1, 400)                                  -- tap A, burn the whole tank
jet(40, 400)                                 -- full jump, then the whole tank
local lastFuel, emptyTiles = jet(12, 400)
expect(lastFuel == 0 and emptyTiles and emptyTiles[1] == emptyTiles[4] and emptyTiles[1] ~= fullTiles[1],
  "empty meter: tiles %s, full was %d", emptyTiles and (emptyTiles[1] .. ".." .. emptyTiles[4]) or "never seen", fullTiles[1])
expect(not flameShown(), "flame still shown after landing")

-- refill: one unit every 2 frames on the ground
local f0 = rd("fuel")
holdNoCacti({}, 40)
local f40 = rd("fuel")
holdNoCacti({}, 100)
note("refill     fuel %d -> %d after 40 frames -> %d after 140", f0, f40, rd("fuel"))
expect(f40 - f0 >= 19 and f40 - f0 <= 21 and rd("fuel") == 64, "refill: %d -> %d -> %d", f0, f40, rd("fuel"))

-- 2b. snakes. Jump with A held for `held` frames; once falling, keep a snake planted under
-- the dino until it is stomped. Returns: stomped, score gained, peak y after the stomp.
local function stomp(held)
  holdNoCacti({}, 2)
  local before, stomped, peak, f = score(), false, 255, 0
  repeat
    f = f + 1
    local falling = rd("dino_vy", 1) < 128 and onGround() == 0
    local planted = not stomped and falling
    if planted then plantSnake(30) end
    holdNoCacti({ A = f <= held }, 1)
    if planted and rd("obst0_on") == 0 then stomped = true end
    if stomped then peak = math.min(peak, dinoY()) end
  until (stomped and onGround() == 1) or state() ~= 0 or f > 300
  return stomped, score() - before, peak
end
local okHi, gainHi, peakHi = stomp(200)     -- A held through the bounce
local okLo, gainLo, peakLo = stomp(20)      -- A let go before landing on the snake
note("stomp      A held: stomped=%s +%d peak y=%d; A let go: stomped=%s +%d peak y=%d",
  tostring(okHi), gainHi, peakHi, tostring(okLo), gainLo, peakLo)
expect(okHi and okLo and state() == 0, "stomp: stomped %s / %s, state %d", tostring(okHi), tostring(okLo), state())
expect(gainHi >= 50 and gainHi <= 60 and gainLo >= 50 and gainLo <= 60,
  "stomp gained %d / %d points, want 50 + a few", gainHi, gainLo)
expect(peakHi < 140 and peakLo > peakHi, "bounce peaks y=%d (A held) / %d (let go)", peakHi, peakLo)

-- walk into a snake: bitten
holdNoCacti({}, 2)
plantSnake(80)
local bitten = 0
while state() == 0 and bitten < 100 do
  if bitten == 10 then gui.savescreenshotas(out .. "snake.png") end
  hold({}, 1); bitten = bitten + 1
end
local biteX = cacti()[1] or 255
note("bite       after %d frames, snake x=%d", bitten, biteX)
expect(state() == 1 and biteX >= 21 and biteX < 44, "walking into a snake: state %d, x=%d (want 1, 21..43)", state(), biteX)
hold({}, 2); hold({ A = true }, 1); hold({}, 2)
expect(state() == 0, "A did not restart after the bite")

-- 3. no input until an obstacle ends the run
local waited = 0
while state() == 0 and waited < 600 do hold({}, 1); waited = waited + 1 end
local xs = cacti()
local hitX, hitSnake = 255, false
for _, o in ipairs(obstacles()) do
  if o.x < hitX then hitX, hitSnake = o.x, o.snake end
end
note("hit        after %d frames, %s x=%d, score=%d", waited, hitSnake and "snake" or "cactus", hitX, score())
expect(state() == 1, "no hit within 600 frames (state %d)", state())
local lo = hitSnake and 21 or 28
expect(hitX >= lo and hitX < 44, "hit with nearest obstacle at x=%d, want %d..43", hitX, lo)
local deadScore, deadX = score(), hitX
hold({}, 60)
expect(score() == deadScore and cacti()[1] == xs[1], "game kept running after the hit")
note("on screen  %s", tostring(shownScore()))
expect(shownScore() == deadScore, "score on screen %s after the hit, want %d", tostring(shownScore()), deadScore)

-- 4. restart on A (an edge: released first)
hold({}, 2)
hold({ A = true }, 1)
hold({}, 2)
note("restart    state=%d score=%d obstacles=%d", state(), score(), #cacti())
expect(shownScore() == 0, "score on screen %s after restart, want 0", tostring(shownScore()))
expect(state() == 0 and score() == 0 and #cacti() == 0, "restart: state=%d score=%d obstacles=%d", state(), score(), #cacti())

-- 5. bot: press A when a cactus is within reach of a full jump at the current speed, and
-- hold it until landing (letting go early would cut the jump). A must come up between jumps.
local shot = false
local played = 0
local lastA = false
local snakes, stomps, lastScore = 0, 0, 0
local seen = {}
while state() == 0 and played < 6000 do
  for i = 0, 2 do                  -- count snakes spawned (each slot's off -> on as a snake)
    local on = rd("obst" .. i .. "_on") ~= 0
    if on and not seen[i] and rd("obst" .. i .. "_kind") == 1 then snakes = snakes + 1 end
    seen[i] = on
  end
  if score() - lastScore >= 50 then stomps = stomps + 1 end
  lastScore = score()
  local s, close = speed(), false
  for _, x in ipairs(cacti()) do
    local d = x - 32
    if d > 0 and d <= s * 16 + 8 then close = true end
  end
  local a = onGround() == 0 or (close and not lastA)
  hold({ A = a }, 1)
  lastA = a
  played = played + 1
  -- screenshot once: dino high, cactus right below
  if not shot and played > 200 and dinoY() < 150 then
    for _, x in ipairs(cacti()) do
      if x >= 30 and x <= 40 then gui.savescreenshotas(out .. "dino.png"); shot = true end
    end
  end
end
note("bot        %d frames, state=%d, score=%d, speed=%.3f px/frame, %d snakes, %d stomped",
  played, state(), score(), speed(), snakes, stomps)
expect(snakes >= 10, "only %d snakes in 6000 frames", snakes)
-- the NMI draws the score the main loop left a frame earlier: at most one point behind
local shown = shownScore()
note("on screen  %s (RAM %d)", tostring(shown), score())
expect(shown and (shown == score() or shown == score() - 1), "score on screen %s, RAM %d", tostring(shown), score())
expect(state() == 0, "bot died after %d frames (score %d, speed %.3f)", played, score(), speed())
expect(speed() == 4, "speed %.3f after 6000 frames, want capped at 4", speed())
expect(score() >= 900, "score %d after 6000 frames, want ~1000", score())
expect(shot, "never caught the dino mid-jump over a cactus")

local f = io.open(out .. "dino.txt", "w")
f:write((#fails == 0 and "PASS" or "FAIL") .. "\n")
for _, m in ipairs(log) do f:write("  " .. m .. "\n") end
for _, m in ipairs(fails) do f:write("  FAIL: " .. m .. "\n") end
f:close()
if not shot then gui.savescreenshotas(out .. "dino.png") end
emu.frameadvance()
emu.exit()
