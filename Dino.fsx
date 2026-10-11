// Dino.fsx — the Chrome offline dinosaur game, crude step 1.
//
// Title screen first: the dino stands on the ground; press Start to play.
// A 16x16 dino runs in place on a static ground line; cacti (8x16 sprites) and rattlesnakes
// (16x16) slide in from the right, faster over time. Press A to jump; let go early for a lower
// jump (jump cut, like the Chrome game). Touch a cactus, or a snake from the side, and the game
// freezes; press A to restart. Land on a snake to bounce off it for bonus points (hold A for a
// high bounce). In the air, hold B to fire the jetpack: it climbs slowly while the fuel
// lasts (meter top left); fuel refills on the ground. The score shows top right.
//
// Verify: timeout 30 ../fceux-win64/fceux64.exe -lua "$PWD/dino.lua" output/dino.nes

#load "Std.fsx"
open Nes
open Cpu6502
open Std
open System.IO

// ---------------------------------------------------------------- CHR

let dinoBody =      // rows 0-11, the same in every frame
    [ "........1111111."
      ".......11.111111"
      ".......111111111"
      ".......11111...."
      ".......1111111.."
      "1.....11111....."
      "1....11111111..."
      "11..1111111.1..."
      "111111111111...."
      ".11111111111...."
      "..111111111....."
      "...1111111......" ]
let legsStand = [ "....111.11......"; "....11...1......"; "....1....1......"; "....11...11....." ]
let legsRunA  = [ "....111.11......"; "....11...11....."; "....1..........."; "....11.........." ]
let legsRunB  = [ "....111.11......"; "....11...1......"; ".........1......"; ".........11....." ]

let dinoTiles legs = tiles (dinoBody @ legs)      // TL, TR, BL, BR
let stand, runA, runB = dinoTiles legsStand, dinoTiles legsRunA, dinoTiles legsRunB

let cactus =
    tiles [ "...11..."; "..1111.."; "..1111.1"; "1.1111.1"; "1.1111.1"; "1.111111"; "1.11111."; "111111.."
            "..1111.."; "..1111.."; "..1111.."; "..1111.."; "..1111.."; "..1111.."; "..1111.."; "..1111.." ]

// coiled rattlesnake facing left, head up, rattle raised at the back.
// 1 brown, 2 tan, 3 black (eye). The rattle shakes: two top-right tiles.
let snake =
    tiles [ "................"
            "................"
            "................"
            "..1111.........."
            ".132221........."
            "12222221........"
            ".1111221.....1.."
            "11...1221...121."
            "......121...121."
            "......121....121"
            ".....1221....11."
            "...111221...121."
            "..12222211.1221."
            ".1221221221221.."
            "1222222222221..."
            ".111111111111..." ]
let rattleShaken =
    tile [ "........"; "........"; "........"; "........"; "........"; "......1."; ".....121"; "1....121" ]

// jetpack, on the dino's back (sprite palette 0: 2 silver, 3 black). Drawn behind the dino,
// so the tail overlaps it: strapped on.
let tank =
    tile [ ".3333..."; "322223.."; "322223.."; "322223.."; "322223.."; "322223.."; ".3333..."; "..33...." ]
// thrust flame under the tank, 2 frames (sprite palette 3: 1 red, 2 orange, 3 yellow)
let flameA =
    tile [ "..33...."; ".2332..."; ".2332..."; "..22...."; "..21...."; "..1....."; "........"; "........" ]
let flameB =
    tile [ "..33...."; ".2332..."; "123321.."; ".2332..."; "..22...."; ".1221..."; "..11...."; "...1...." ]

/// Fuel bar tile with `n` (0-8) columns filled: dark gray border, black fill.
let barTile n =
    tile [ yield "11111111"
           for _ in 1 .. 5 -> String.replicate n "3" + String.replicate (8 - n) "."
           yield "11111111"
           yield "........" ]

// bar ends: a 1 px edge on the side touching the bar
let barCapL = tile [ for r in 0 .. 7 -> if r = 7 then "........" else ".......1" ]
let barCapR = tile [ for r in 0 .. 7 -> if r = 7 then "........" else "1......." ]

let ground = tile [ "11111111"; "........"; ".1....1."; "........"; "....1..."; "........"; "........"; "........" ]
let blank = tile [ for _ in 1 .. 8 -> "........" ]

// font: digits first (tile = tDigit0 + digit), then the letters the title needs
let font =
    [ [ ".1111..."; "11..11.."; "11.111.."; "111111.."; "111.11.."; "11..11.."; ".1111..."; "........" ]
      [ "..11...."; ".111...."; "..11...."; "..11...."; "..11...."; "..11...."; ".1111..."; "........" ]
      [ ".1111..."; "11..11.."; "....11.."; "...11..."; "..11...."; ".11....."; "111111.."; "........" ]
      [ ".1111..."; "11..11.."; "....11.."; "..111..."; "....11.."; "11..11.."; ".1111..."; "........" ]
      [ "...111.."; "..1111.."; ".11.11.."; "11..11.."; "111111.."; "....11.."; "....11.."; "........" ]
      [ "111111.."; "11......"; "11111..."; "....11.."; "....11.."; "11..11.."; ".1111..."; "........" ]
      [ "..111..."; ".11....."; "11......"; "11111..."; "11..11.."; "11..11.."; ".1111..."; "........" ]
      [ "111111.."; "....11.."; "...11..."; "..11...."; ".11....."; ".11....."; ".11....."; "........" ]
      [ ".1111..."; "11..11.."; "11..11.."; ".1111..."; "11..11.."; "11..11.."; ".1111..."; "........" ]
      [ ".1111..."; "11..11.."; "11..11.."; ".11111.."; "....11.."; "...11..."; ".111...."; "........" ] ]
    |> List.mapi (fun i rows -> char (int '0' + i), rows)
    |> fun digits -> digits @ [
      'A', [ "..11...."; ".1111..."; "11..11.."; "11..11.."; "111111.."; "11..11.."; "11..11.."; "........" ]
      'F', [ "111111.."; "11......"; "11......"; "11111..."; "11......"; "11......"; "11......"; "........" ]
      'L', [ "11......"; "11......"; "11......"; "11......"; "11......"; "11......"; "111111.."; "........" ]
      'U', [ "11..11.."; "11..11.."; "11..11.."; "11..11.."; "11..11.."; "11..11.."; ".1111..."; "........" ]
      'D', [ "11111..."; "11..11.."; "11..11.."; "11..11.."; "11..11.."; "11..11.."; "11111..."; "........" ]
      'E', [ "111111.."; "11......"; "11......"; "11111..."; "11......"; "11......"; "111111.."; "........" ]
      'I', [ "111111.."; "..11...."; "..11...."; "..11...."; "..11...."; "..11...."; "111111.."; "........" ]
      'N', [ "11..11.."; "111.11.."; "111111.."; "111111.."; "11.111.."; "11..11.."; "11..11.."; "........" ]
      'O', [ ".1111..."; "11..11.."; "11..11.."; "11..11.."; "11..11.."; "11..11.."; ".1111..."; "........" ]
      'P', [ "11111..."; "11..11.."; "11..11.."; "11111..."; "11......"; "11......"; "11......"; "........" ]
      'R', [ "11111..."; "11..11.."; "11..11.."; "11111..."; "11.11..."; "11..11.."; "11..11.."; "........" ]
      'S', [ ".1111..."; "11..11.."; "11......"; ".1111..."; "....11.."; "11..11.."; ".1111..."; "........" ]
      'T', [ "111111.."; "..11...."; "..11...."; "..11...."; "..11...."; "..11...."; "..11...."; "........" ] ]

// tile numbers = position in this list
let chr =
    [ blank                                     // 0
      stand.[0]; stand.[1]                      // 1, 2   head (all frames)
      stand.[2]; stand.[3]                      // 3, 4   legs standing / in the air
      runA.[2]; runA.[3]                        // 5, 6
      runB.[2]; runB.[3]                        // 7, 8
      cactus.[0]; cactus.[1]                    // 9, 10
      ground ]                                  // 11
    @ (font |> List.map (snd >> tile))          // 12-21  0-9, then letters
    @ [ snake.[0]; snake.[1]; snake.[2]; snake.[3] // TL, TR (rattle still), BL, BR
        rattleShaken                            // TR, rattle shaken
        tank; flameA; flameB ]
    @ [ for n in 0 .. 8 -> barTile n ]
    @ [ barCapL; barCapR ]
let tHeadL, tLegsStand, tLegsRunA, tLegsRunB, tCactus, tGround, tDigit0 = 1, 3, 5, 7, 9, 11, 12
let tSnake = tDigit0 + font.Length
let tRattleShaken = tSnake + 4
let tTank, tFlameA, tFlameB = tSnake + 5, tSnake + 6, tSnake + 7
let tBar0 = tSnake + 8                          // + 0..8 filled columns
let tBarCapL, tBarCapR = tBar0 + 9, tBar0 + 10

/// Tile numbers spelling `s` (' ' = blank tile 0).
let text (s: string) =
    [| for c in s ->
        if c = ' ' then 0uy
        else
            match font |> List.tryFindIndex (fun (ch, _) -> ch = c) with
            | Some i -> byte (tDigit0 + i)
            | None -> failwithf "no tile for '%c'" c |]


// background: white, ground dark gray. sprites: palette 0 dino gray, 1 cactus green, 2 snake, 3 fire.
let palette =
    [| 0x30uy; 0x2Duy; 0x00uy; 0x0Fuy;  0x30uy; 0x2Duy; 0x00uy; 0x0Fuy
       0x30uy; 0x2Duy; 0x00uy; 0x0Fuy;  0x30uy; 0x2Duy; 0x00uy; 0x0Fuy
       0x30uy; 0x2Duy; 0x10uy; 0x0Fuy;  0x30uy; 0x1Auy; 0x0Auy; 0x0Fuy
       0x30uy; 0x17uy; 0x37uy; 0x0Fuy;  0x30uy; 0x16uy; 0x27uy; 0x38uy |]

// ---------------------------------------------------------------- tuning (build time)

let groundRow = 24                  // nametable row of the ground line (y = 192)
let groundY = 176.0                 // dino / obstacle top when standing on it
let dinoX = 32
let jumpSpeed, gravity, maxFall = -5.5, 0.35, 6.0
let cutSpeed = -2.0                 // A let go while rising faster than this: rise only this fast
let startSpeed, speedUp, topSpeed = -2.0, -0.125, -4.0     // obstacle px/frame; speedUp every 256 frames
let spawnX = 240.0
let minGap = 40                     // frames between obstacles: minGap + random 0..63
let snakeOdds = 4                   // 1 obstacle in snakeOdds is a snake (a power of 2)
let bounceSpeed = -5.0              // landing on a snake: like a jump, a bit lower
let bounceBonus = 50
let fuelMax = 64                    // frames of thrust in a full tank
let thrustAccel = -0.6              // per frame while B is held (gravity +0.35): net -0.25
let riseCap = -2.0                  // thrust never climbs faster than this
let ceilingY = 36                   // no thrust above this (dino top, px); the climb coasts ~7 px
                                    // further, so the dino stays under the HUD (row 2)
let refillEvery = 2                 // frames per unit of fuel, on the ground (a power of 2)
let meterRow, meterCol, meterTiles = 2, 7, 4
if fuelMax / 2 <> meterTiles * 8 then failwith "meter: one column per 2 units of fuel"
let fuelText = Array.concat [ text "FUEL "; [| byte tBarCapL |]; Array.zeroCreate meterTiles; [| byte tBarCapR |] ]
let scoreEvery = 6                  // frames per point
let scoreRow, scoreCol = 2, 25      // top right, below the NTSC overscan
let titleText, titleRow = text "DINO", 10
let pressText, pressRow = text "PRESS START", 13

// hit boxes, a bit smaller than the art
let hitLeft, hitRight = dinoX + 3 - 7, dinoX + 12  // cactus x in [hitLeft, hitRight) overlaps the dino
let hitFeetY = int groundY - 14                    // dino top >= this: low enough to touch a cactus
let snakeLeft, snakeRight = dinoX + 3 - 14, dinoX + 12  // snake art spans x + 1 .. x + 14
// dino top >= this: feet in the snake (its head is 4 px below the obstacle top). The zone is
// 10 px tall, more than maxFall, so a falling dino always spends a frame in it before landing.
let snakeFeetY = int groundY + 4 - 14
if int groundY - snakeFeetY <= int maxFall then failwith "snake stomp zone thinner than maxFall"

// ---------------------------------------------------------------- state

let screen   = zpU8 "screen"        // which loop runs: titleScreen or gameScreen
let titleScreen, gameScreen = 0, 1
let state    = zpU8 "state"         // game screen: 0 playing, 1 dead
let dinoY    = zpUFix "dino_y"
let dinoVy   = zpSFix "dino_vy"
let onGround = zpU8 "on_ground"
let legs     = zpU8 "legs"          // bottom-left tile; bottom-right is legs + 1
let legsR    = zpU8 "legs_r"
let speed    = zpSFix "speed"       // negative: obstacles move left
let speedTick = zpU8 "speed_tick"
let spawnTimer = zpU8 "spawn_timer"
let scoreTick = zpU8 "score_tick"
let score    = zpDigits "score" 5
let seed     = zpU16 "seed"
let frames   = zpU16 "frames"
let rattle   = zpU8 "rattle"        // the snakes' top-right tile this frame
let fuel     = zpU8 "fuel"          // 0..fuelMax
let thrusting = zpU8 "thrusting"    // 1 if the jetpack fired this frame
let flame    = zpU8 "flame"         // the flame's tile this frame
let meter    = zpVar "meter" meterTiles  // fuel bar tiles, copied to the nametable in NMI

let kindCactus, kindSnake = 0, 1
/// A cactus or a snake; Slot is its first of 4 sprites.
type Obstacle = { On: U8; X: UFix; Kind: U8; Slot: int }
let obstacles =
    [ for i in 0 .. 2 ->
        { On = zpU8 (sprintf "obst%d_on" i); X = zpUFix (sprintf "obst%d_x" i)
          Kind = zpU8 (sprintf "obst%d_kind" i); Slot = 4 + 4 * i } ]

let reset = label "reset"
let nmi   = label "nmi"
let paletteData = label "palette"
let titleData, pressData = label "title_text", label "press_text"
let fuelData = label "fuel_text"

// ---------------------------------------------------------------- snippets

/// Everything back to the start of a run (the random seed keeps going). Clobbers a, x.
let newGame =
    subroutine "new_game" (fun () ->
        set8 state 0
        setUFix dinoY groundY
        setSFix dinoVy 0.0
        set8 onGround 1
        setSFix speed startSpeed
        set8 speedTick 0
        set8 spawnTimer 60
        set8 scoreTick scoreEvery
        clearDigits score
        set8 fuel fuelMax
        set8 thrusting 0
        for o in obstacles do set8 o.On 0)

/// On the ground and A pressed (an edge: holding A does not jump again): take off. Clobbers a.
let jump () =
    whenPressed 1 Button.A (fun () ->
        ifNonZero onGround.Addr (fun () ->
            setSFix dinoVy jumpSpeed
            set8 onGround 0))

/// In the air: jump cut if A is let go, gravity, move, land on the ground. Clobbers a.
/// B held, fuel left, below the ceiling and not already climbing faster than riseCap:
/// accelerate up (at most riseCap), burn a unit of fuel, thrusting <- 1. Clobbers a.
let thrust =
    subroutine "thrust" (fun () ->
        whenHeld 1 Button.B (fun () ->
            ifNonZero fuel.Addr (fun () ->
                ifAtLeast dinoY.Hi ceilingY (fun () ->
                    ifSFixAtLeast dinoVy riseCap (fun () ->
                        addSFix dinoVy thrustAccel
                        floorSFix dinoVy riseCap
                        sub8 fuel 1
                        set8 thrusting 1)))))

/// On the ground: a unit of fuel every refillEvery frames, up to fuelMax. Clobbers a.
let refill =
    subroutine "refill" (fun () ->
        ifNonZero onGround.Addr (fun () ->
            let skip = label "refill_skip"
            LDA (Zp frames.Lo)
            AND (Imm (refillEvery - 1))
            BNE skip
            add8Max fuel 1 fuelMax
            place skip))

let fall =
    subroutine "fall" (fun () ->
        set8 thrusting 0
        ifZero onGround.Addr (fun () ->
            whenNotHeld 1 Button.A (fun () ->
                ifSFixBelow dinoVy cutSpeed (fun () -> setSFix dinoVy cutSpeed))
            addSFix dinoVy gravity
            capSFix dinoVy maxFall
            thrust ()
            addVel dinoY dinoVy
            ifUFixAtLeast dinoY groundY (fun () ->
                setUFix dinoY groundY
                setSFix dinoVy 0.0
                set8 onGround 1)))

/// Active obstacles slide left; one that wrapped past x = 0 goes inactive. Clobbers a.
let moveObstacles =
    subroutine "move_obstacles" (fun () ->
        for c in obstacles do
            ifNonZero c.On.Addr (fun () ->
                addVel c.X speed
                ifUFixAtLeast c.X 248.0 (fun () -> set8 c.On 0)))

/// Count down; at 0, start the first inactive obstacle at the right edge (a snake 1 time in
/// snakeOdds, else a cactus) and wait minGap + random 0..63 frames for the next. Clobbers a, x.
let spawnObstacles =
    subroutine "spawn_obstacles" (fun () ->
        let ready = label "spawn_ready"
        DEC (Zp spawnTimer.Addr)
        BEQ ready
        RTS ()
        place ready
        random8 seed
        AND (Imm 63)
        CLC ()
        ADC (Imm minGap)
        STA (Zp spawnTimer.Addr)
        let kindPicked = label "kind_picked"
        random8 seed
        LDX (Imm kindCactus)
        AND (Imm (snakeOdds - 1))
        BNE kindPicked
        LDX (Imm kindSnake)
        place kindPicked                // x = kind
        for c in obstacles do
            let next = label "spawn_next"
            LDA (Zp c.On.Addr)
            BNE next
            STX (Zp c.Kind.Addr)
            set8 c.On 1
            setUFix c.X spawnX
            RTS ()
            place next)

/// Bounce off a snake: up again, with bonus points. Clobbers a.
let bounce =
    subroutine "bounce" (fun () ->
        setSFix dinoVy bounceSpeed
        addDigits score bounceBonus)

/// Touching snake `o`: falling onto it removes it and bounces; otherwise it bites. Clobbers a.
let stompOrBitten (o: Obstacle) =
    let bitten, finished = label "bitten", label "stomp_done"
    LDA (Zp onGround.Addr)
    BNE bitten                      // walked into it
    LDA (Zp dinoVy.Hi)
    BMI bitten                      // jumped up into it
    set8 o.On 0
    bounce ()
    JMP (Abs finished)
    place bitten
    set8 state 1
    place finished

/// Any active cactus overlapping the dino ends the run; so does a snake, unless the dino lands
/// on it. Clobbers a.
let checkHits =
    subroutine "check_hits" (fun () ->
        for o in obstacles do
            let isOn, isSnake, next = label "hit_on", label "hit_snake", label "hit_next"
            LDA (Zp o.On.Addr)
            BNE isOn
            JMP (Abs next)
            place isOn
            LDA (Zp o.Kind.Addr)
            BNE isSnake
            ifAtLeast dinoY.Hi hitFeetY (fun () ->
                ifAtLeast o.X.Hi hitLeft (fun () ->
                    ifBelow o.X.Hi hitRight (fun () -> set8 state 1)))
            JMP (Abs next)
            place isSnake
            ifAtLeast dinoY.Hi snakeFeetY (fun () ->
                ifAtLeast o.X.Hi snakeLeft (fun () ->
                    ifBelow o.X.Hi snakeRight (fun () -> stompOrBitten o)))
            place next)

/// A point every few frames; faster obstacles every 256 frames. Clobbers a, x.
let scoreAndSpeed =
    subroutine "score_and_speed" (fun () ->
        let noPoint = label "no_point"
        DEC (Zp scoreTick.Addr)
        BNE noPoint
        set8 scoreTick scoreEvery
        addDigits score 1
        place noPoint
        add8 speedTick 1
        onCarry (fun () ->
            addSFix speed speedUp
            floorSFix speed topSpeed))

/// legs, legs_r <- running frame (alternates every 8 frames), or standing in the air. Clobbers a, x.
let animateLegs =
    subroutine "animate_legs" (fun () ->
        let pick, store = label "legs_pick", label "legs_store"
        LDA (Imm tLegsStand)
        LDX (Zp onGround.Addr)
        BEQ store                   // in the air
        LDA (Zp state.Addr)
        BNE store                   // dead: stand still
        LDA (Zp frames.Lo)
        AND (Imm 8)
        BEQ pick
        LDA (Imm tLegsRunB)
        BNE store                   // always taken
        place pick
        LDA (Imm tLegsRunA)
        place store
        STA (Zp legs.Addr)
        CLC ()
        ADC (Imm 1)
        STA (Zp legsR.Addr))

/// v <- `off` while frame-counter bit `bit` (a mask) is clear, else `on`. Clobbers a.
let alternate (v: U8) (bit: int) (off: int) (on: int) =
    if on = 0 then failwith "alternate: `on` tile 0 breaks the always-taken BNE"
    let isOff, store = label "alt_off", label "alt_store"
    LDA (Zp frames.Lo)
    AND (Imm bit)
    BEQ isOff
    LDA (Imm on)
    BNE store                   // always taken
    place isOff
    LDA (Imm off)
    place store
    STA (Zp v.Addr)

/// rattle <- the snakes' top-right tile: the rattle shakes every 4 frames;
/// flame <- the jetpack flame's tile, flickering every 2 frames. Clobbers a.
let animateRattleAndFlame =
    subroutine "animate_rattle_flame" (fun () ->
        alternate rattle 4 (tSnake + 1) tRattleShaken
        alternate flame 2 tFlameA tFlameB)

/// meter <- fuel bar tiles: fuel / 2 columns filled, left to right. Clobbers a, x.
let fuelMeterTiles =
    subroutine "fuel_meter_tiles" (fun () ->
        LDA (Zp fuel.Addr)
        LSR Acc                     // a = filled columns, 0..32
        for i in 0 .. meterTiles - 1 do
            let full, store = label "meter_full", label "meter_store"
            LDX (Imm (tBar0 + 8))
            CMP (Imm 8)
            BCS full
            ADC (Imm tBar0)         // partial (C clear): tile = tBar0 + columns, the rest empty
            TAX ()
            LDA (Imm 0)
            BEQ store               // always taken
            place full
            SBC (Imm 8)             // C set by the CMP
            place store
            STX (Zp (meter + i)))

/// Shadow OAM <- dino (sprites 0-3) and obstacles (4 sprites each: a cactus uses 2; unused
/// ones hidden). Clobbers a.
let drawAll =
    subroutine "draw_all" (fun () ->
        let x, y = Imm dinoX, Zp dinoY.Hi
        putSprite 0 x y 0 0 (Imm tHeadL) 0
        putSprite 1 x y 8 0 (Imm (tHeadL + 1)) 0
        putSprite 2 x y 0 8 (Zp legs.Addr) 0
        putSprite 3 x y 8 8 (Zp legsR.Addr) 0
        // jetpack: behind the dino (a higher slot), flame under it while thrusting.
        // Slots 16-17 come after the obstacles: on a crowded scanline they drop out first.
        putSprite 16 x y (-3) 2 (Imm tTank) 0
        let noFlame, flameDone = label "no_flame", label "flame_done"
        LDA (Zp thrusting.Addr)
        BEQ noFlame
        putSprite 17 x y (-3) 10 (Zp flame.Addr) 3
        JMP (Abs flameDone)
        place noFlame
        hideSprite 17
        place flameDone
        let y = Imm (int groundY)
        for o in obstacles do
            let isOn, isSnake, off, next = label "draw_on", label "draw_snake", label "draw_off", label "draw_next"
            let s, x = o.Slot, Zp o.X.Hi
            LDA (Zp o.On.Addr)
            BNE isOn
            JMP (Abs off)
            place isOn
            LDA (Zp o.Kind.Addr)
            BNE isSnake
            putSprite s x y 0 0 (Imm tCactus) 1
            putSprite (s + 1) x y 0 8 (Imm (tCactus + 1)) 1
            hideSprite (s + 2)
            hideSprite (s + 3)
            JMP (Abs next)
            place isSnake
            putSprite s x y 0 0 (Imm tSnake) 2
            putSprite (s + 1) x y 8 0 (Zp rattle.Addr) 2
            putSprite (s + 2) x y 0 8 (Imm (tSnake + 2)) 2
            putSprite (s + 3) x y 8 8 (Imm (tSnake + 3)) 2
            JMP (Abs next)
            place off
            for k in 0 .. 3 do hideSprite (s + k)
            place next)

/// Nametable 0 <- blank with the ground line. Rendering must be off. Clobbers a, x, y.
let drawGround () =
    clearNametable0 ()
    fillNametableRow groundRow tGround

/// Title screen: ground, "DINO", "PRESS START", the dino standing. Clobbers a, x, y.
let showTitle =
    subroutine "show_title" (fun () ->
        whileRenderingOff (fun () ->
            drawGround ()
            copyToNametable titleRow (16 - titleText.Length / 2) titleData titleText.Length
            copyToNametable pressRow (16 - pressText.Length / 2) pressData pressText.Length
            newGame ()
            set8 legs tLegsStand
            set8 legsR (tLegsStand + 1)
            drawAll ()
            set8 screen titleScreen))

/// Game screen: ground only (the NMI draws the score), a fresh run. Clobbers a, x, y.
let showGame =
    subroutine "show_game" (fun () ->
        whileRenderingOff (fun () ->
            drawGround ()
            copyToNametable meterRow (meterCol - 6) fuelData fuelText.Length   // "FUEL [    ]"
            newGame ()
            set8 screen gameScreen))

let readPad1 = subroutine "read_pad1" (fun () -> readControllerEdgesForPlayer 1)

/// One frame of play.
let play =
    subroutine "play" (fun () ->
        jump ()
        fall ()
        refill ()
        moveObstacles ()
        spawnObstacles ()
        checkHits ()
        scoreAndSpeed ())

/// NMI: sprites (and the score and fuel bar, on the game screen) to the PPU, count the frame, wake the
/// main loop. Saves a.
let nmiHandler () =
    place nmi
    PHA ()
    copySpritesToPpu ()
    ifNonZero screen.Addr (fun () ->
        drawDigits score scoreRow scoreCol tDigit0
        copyZpToNametable meter meterTiles meterRow meterCol)
    resetScroll ()
    inc16 frames
    signalNmiDone ()
    PLA ()
    RTI ()

// ---------------------------------------------------------------- rom

startRom ()
put (inesHeader 0 1 1 Mirroring.Horizontal)

org 0xC000
place reset
initCpu ()
clearZeroPage ()
hideAllSprites ()
waitVblank ()
waitVblank ()
loadPalette paletteData
set16 seed 0x1234
showTitle ()

let mainLoop = here "main_loop"
waitForNmi ()
readPad1 ()
let game, dead, draw = label "game", label "dead", label "draw"
LDA (Zp screen.Addr)
BNE game
// title screen: wait for Start
whenPressed 1 Button.Start showGame
JMP (Abs mainLoop)
// game screen
place game
LDA (Zp state.Addr)
BNE dead
play ()
JMP (Abs draw)
place dead
whenPressed 1 Button.A newGame
place draw
animateLegs ()
animateRattleAndFlame ()
fuelMeterTiles ()
drawAll ()
JMP (Abs mainLoop)

emitSubroutines ()

printfn "NMI handler: %d bytes" (sizeOf nmiHandler)
nmiHandler ()

place paletteData
put palette
place titleData
put titleText
place pressData
put pressText
place fuelData
put fuelText

printfn "code + data end at $%04X, %d bytes free before the vectors" (pc ()) (0xFFFA - pc ())
vectors nmi reset nmi
put (chrBank chr)

finishRom ()
|> write (Path.Combine(__SOURCE_DIRECTORY__, "output", "dino.nes"))
