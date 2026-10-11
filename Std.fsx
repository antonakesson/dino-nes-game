// Std.fsx — reusable code snippets for NES programs.
//
// Each snippet is a function that puts its instructions into the ROM when called. It is
// named for what it does and documents what it leaves in the registers. Snippets
// fall through into whatever is put next. Labels made inside one are fresh on
// every call.

#load "Cpu6502.fsx"
open Nes
open Cpu6502

// ---------------------------------------------------------------- startup

/// IRQs off, decimal off, stack at $01FF, APU frame + DMC IRQs off, NMI + rendering off.
/// Leaves x = 0.
let initCpu () =
    SEI ()
    CLD ()
    LDX (Imm 0x40)
    STX (Abs APU_FRAME)             // no APU frame IRQ
    LDX (Imm 0xFF)
    TXS ()                          // stack at $01FF
    INX ()                          // x = 0
    STX (Abs PPUCTRL)               // NMI off
    STX (Abs PPUMASK)               // rendering off
    STX (Abs DMC_FREQ)              // no DMC IRQ

/// Spin until the next vblank starts. Call twice after reset: the PPU needs two to warm up.
/// Registers untouched.
let waitVblank () =
    let loop = here "wait_vblank"
    BIT (Abs PPUSTATUS)
    BPL loop

/// Pad to $FFFA and put the NMI, reset and IRQ vectors: the end of the PRG bank.
let vectors (nmi: Label) (reset: Label) (irq: Label) =
    padTo 0xFFFA
    word nmi
    word reset
    word irq

// ---------------------------------------------------------------- PPU

/// Reset the PPUADDR / PPUSCROLL write latch. Clobbers a.
let resetPpuLatch () =
    LDA (Abs PPUSTATUS)

/// PPUADDR <- constant VRAM address. Leaves a = low byte of `addr`.
let setPpuAddr (addr: int) =
    LDA (Imm (addr >>> 8))
    STA (Abs PPUADDR)
    LDA (Imm (addr &&& 0xFF))
    STA (Abs PPUADDR)

/// PPUDATA <- `len` (1-255) bytes from ROM at `src`. Clobbers a, x.
let copyToPpu (src: Label) (len: int) =
    if len < 1 || len > 255 then failwithf "copyToPpu: len %d not in 1-255" len
    LDX (Imm 0)
    let loop = here "copy_to_ppu"
    LDA (AbsX src)
    STA (Abs PPUDATA)
    INX ()
    CPX (Imm len)
    BNE loop

/// All 32 palette bytes (4 background + 4 sprite) from ROM at `src`. Clobbers a, x.
let loadPalette (src: Label) =
    resetPpuLatch ()
    setPpuAddr 0x3F00
    copyToPpu src 32

/// Nametable 0 and its attributes ($2000-$23FF) filled with tile 0, palette 0.
/// Leaves a = 0, x = 0, y = 0.
let clearNametable0 () =
    setPpuAddr 0x2000               // leaves a = 0
    LDY (Imm 4)                     // 4 pages
    LDX (Imm 0)                     // of 256 bytes
    let loop = here "clear_nametable"
    STA (Abs PPUDATA)
    DEX ()
    BNE loop
    DEY ()
    BNE loop

/// Nametable 0 row `row` (0-29) <- 32 x `tile`. Rendering must be off. Clobbers a, x.
let fillNametableRow row tile =
    if row < 0 || row > 29 then failwithf "fillNametableRow: row %d not in 0-29" row
    resetPpuLatch ()
    setPpuAddr (0x2000 + row * 32)
    LDA (Imm tile)
    LDX (Imm 32)
    let loop = here "fill_row"
    STA (Abs PPUDATA)
    DEX ()
    BNE loop

/// Nametable 0 at (`row`, `col`) <- `len` bytes (tile numbers) from ROM at `src`, left to right.
/// Rendering must be off. Clobbers a, x.
let copyToNametable row col (src: Label) len =
    if row < 0 || row > 29 || col < 0 || col + len > 32 then
        failwithf "copyToNametable: %d tiles at row %d col %d leave the screen" len row col
    resetPpuLatch ()
    setPpuAddr (0x2000 + row * 32 + col)
    copyToPpu src len

/// Nametable 0 at (`row`, `col`) <- `len` tile numbers from zero page at `src` (a tile buffer
/// the game fills each frame). Only in vblank (NMI) or with rendering off; reset the scroll
/// after. ~7 cycles a tile. Clobbers a.
let copyZpToNametable (src: Label) len row col =
    if row < 0 || row > 29 || col < 0 || col + len > 32 then
        failwithf "copyZpToNametable: %d tiles at row %d col %d leave the screen" len row col
    resetPpuLatch ()
    setPpuAddr (0x2000 + row * 32 + col)
    for i in 0 .. len - 1 do
        LDA (Zp (src + i))
        STA (Abs PPUDATA)

/// Scroll to 0,0. Leaves a = 0.
let resetScroll () =
    LDA (Imm 0)
    STA (Abs PPUSCROLL)             // x
    STA (Abs PPUSCROLL)             // y

/// NMI on, background + sprites on (incl. left 8 px), 8x8 sprites from pattern table 0. Clobbers a.
let enableNmiAndRendering () =
    LDA (Imm 0b10000000)
    STA (Abs PPUCTRL)
    LDA (Imm 0b00011110)
    STA (Abs PPUMASK)

/// NMI and rendering off: VRAM is free to write at any time. Leaves a = 0.
let disableNmiAndRendering () =
    LDA (Imm 0)
    STA (Abs PPUCTRL)
    STA (Abs PPUMASK)

/// Run `body` (which may write VRAM freely: a new screen) with NMI and rendering off, then
/// wait for vblank and turn both back on with the scroll at 0,0. The screen is blank for
/// a frame or two. Clobbers a (and whatever `body` does).
let whileRenderingOff (body: unit -> unit) =
    disableNmiAndRendering ()
    body ()
    waitVblank ()
    resetScroll ()
    enableNmiAndRendering ()

// ---------------------------------------------------------------- RAM

/// Whole zero page <- 0 (RAM is random at power-on). Clobbers a, x; leaves x = 0.
let clearZeroPage () =
    LDA (Imm 0)
    TAX ()
    let loop = here "clear_zp"
    STA (ZpX (equ "zero_page" 0))
    INX ()
    BNE loop

// ---------------------------------------------------------------- sprites

/// Shadow OAM: 64 sprites x 4 bytes (y, tile, attributes, x), copied to the PPU every NMI.
let shadowOam = equ "oam" 0x0200

/// Every sprite off screen (y = $FF). Clobbers a, x; leaves x = 0.
let hideAllSprites () =
    LDA (Imm 0xFF)
    LDX (Imm 0)
    let loop = here "hide_sprites"
    STA (AbsX shadowOam)
    INX ()
    BNE loop

/// Shadow OAM -> PPU by DMA (513 cycles; call early in NMI). Clobbers a.
let copySpritesToPpu () =
    LDA (Imm 0)
    STA (Abs OAMADDR)
    LDA (ImmHi shadowOam)
    STA (Abs OAMDMA)

/// a <- operand + d (d folded in at build time for an `Imm`).
let private loadPlus (o: Operand) d =
    match o with
    | Imm v -> LDA (Imm ((v + d) &&& 0xFF))
    | _ ->
        LDA o
        if d <> 0 then
            CLC ()
            ADC (Imm (d &&& 0xFF))

/// Shadow OAM sprite `slot` (0-63) <- `tile` at (x + dx, y + dy), top-left in screen pixels.
/// x, y, tile are LDA operands: `Imm 32`, `Zp dinoY.Hi`. Handles OAM's y - 1. Clobbers a.
let putSprite slot (x: Operand) (y: Operand) dx dy (tile: Operand) (attr: int) =
    if slot < 0 || slot > 63 then failwithf "putSprite: slot %d not in 0-63" slot
    let o = shadowOam + slot * 4
    loadPlus y (dy - 1)
    STA (Abs o)
    LDA tile
    STA (Abs (o + 1))
    LDA (Imm attr)
    STA (Abs (o + 2))
    loadPlus x dx
    STA (Abs (o + 3))

/// Shadow OAM sprite `slot` off screen. Clobbers a.
let hideSprite slot =
    if slot < 0 || slot > 63 then failwithf "hideSprite: slot %d not in 0-63" slot
    LDA (Imm 0xFF)
    STA (Abs (shadowOam + slot * 4))

// ---------------------------------------------------------------- frame sync

/// Set by the NMI once per frame; the main loop waits for it.
let nmiDone = zpVar "nmi_done" 1

/// Spin until the NMI has run, then clear the flag. Clobbers a.
let waitForNmi () =
    let loop = here "wait_nmi"
    LDA (Zp nmiDone)
    BEQ loop
    LDA (Imm 0)
    STA (Zp nmiDone)

/// Tell the main loop a frame passed (put in the NMI). Clobbers a.
let signalNmiDone () =
    LDA (Imm 1)
    STA (Zp nmiDone)

// ---------------------------------------------------------------- input

/// Bits of a pad byte, as `readControllerInputForPlayer` leaves it.
type Button =
    | A = 0x80 | B = 0x40 | Select = 0x20 | Start = 0x10
    | Up = 0x08 | Down = 0x04 | Left = 0x02 | Right = 0x01

/// Pad state, one byte per player (only allocated if some code reads a pad).
let pads = zpVar "pads" 2

/// pads+(player-1) <- buttons held now. Strobes both pads. Clobbers a.
let readControllerInputForPlayer player =
    let port, pad =
        match player with
        | 1 -> JOY1, pads + 0
        | 2 -> JOY2, pads + 1
        | p -> failwithf "readControllerInputForPlayer: no player %d (1 or 2)" p
    LDA (Imm 1)
    STA (Abs JOY1)                  // strobe on: latch both pads
    STA (Zp pad)                    // a 1 in bit 0 that reaches carry after 8 shifts
    LSR Acc                         // a = 0
    STA (Abs JOY1)                  // strobe off
    let loop = here "read_pad"
    LDA (Abs port)
    LSR Acc                         // button -> carry
    ROL (Zp pad)                    // carry -> pad, old bit 7 -> carry
    BCC loop                        // until the marker 1 falls out

/// Run `body` only while `button` is held by `player`. Clobbers a (before `body`).
/// The build fails if `body` is too long to branch over (> 127 bytes).
let whenHeld player (button: Button) (body: unit -> unit) =
    let skip = label "not_held"
    LDA (Zp (pads + (player - 1)))
    AND (Imm (int button))
    BEQ skip
    body ()
    place skip

/// Buttons held at the previous read, and buttons that went down since (one byte per player).
let prevPads = zpVar "prev_pads" 2
let pressedPads = zpVar "pressed" 2

/// Like `readControllerInputForPlayer`, plus pressed+(player-1) <- buttons that went down
/// since the last call (one frame only). Clobbers a.
let readControllerEdgesForPlayer player =
    let i = player - 1
    LDA (Zp (pads + i))
    STA (Zp (prevPads + i))
    readControllerInputForPlayer player
    LDA (Zp (prevPads + i))
    EOR (Imm 0xFF)
    AND (Zp (pads + i))
    STA (Zp (pressedPads + i))

/// Run `body` on the frame `button` goes down (needs `readControllerEdgesForPlayer`).
/// Clobbers a (before `body`).
let whenPressed player (button: Button) (body: unit -> unit) =
    let skip = label "not_pressed"
    LDA (Zp (pressedPads + (player - 1)))
    AND (Imm (int button))
    BEQ skip
    body ()
    place skip

/// Run `body` while `button` is NOT held by `player` (the level, not the release edge:
/// repeats every frame). Clobbers a (before `body`).
let whenNotHeld player (button: Button) (body: unit -> unit) =
    let skip = label "held"
    LDA (Zp (pads + (player - 1)))
    AND (Imm (int button))
    BNE skip
    body ()
    place skip

// ---------------------------------------------------------------- arithmetic

/// 16-bit var += 1 (little-endian, 2 bytes). Registers untouched.
let increment16 (var: Label) =
    let done' = label "inc16_done"
    INC (Zp var)
    BNE done'
    INC (Zp (var + 1))
    place done'

// ---------------------------------------------------------------- typed variables
//
// A thin type layer over zero-page labels: F# refuses to mix them up at build time,
// and the snippet picks the right instructions and flags for the type. Every snippet
// is still exact bytes; there are no expressions, only named operations.
//
//   let dinoY  = zpUFix "dino_y"         // unsigned 8.8: position
//   let dinoVy = zpSFix "dino_vy"        // signed 8.8: velocity
//   addSFix dinoVy 0.35                  // gravity
//   addVel dinoY dinoVy                  // move
//   LDA (Zp dinoY.Hi)                    // raw access: whole pixels
//
// Overflow is chosen by the snippet name: plain ones wrap and leave the flag
// (`onCarry` / `onNoCarry` react to it), `...Max` / `cap...` clamp, `addDigits` saturates.

/// Unsigned byte, 0..255.
type U8 =
    | U8 of Label
    member v.Addr = match v with U8 l -> l

/// Unsigned 16-bit, 0..65535, little-endian.
type U16 =
    | U16 of Label
    member v.Lo = match v with U16 l -> l
    member v.Hi = match v with U16 l -> l + 1

/// Unsigned 8.8 fixed point, 0 .. 255.996: a position. Hi = whole pixels, Lo = 1/256 px.
type UFix =
    | UFix of Label
    member v.Lo = match v with UFix l -> l
    member v.Hi = match v with UFix l -> l + 1

/// Signed 8.8 fixed point, -128 .. 127.996 (two's complement): a velocity or acceleration.
type SFix =
    | SFix of Label
    member v.Lo = match v with SFix l -> l
    member v.Hi = match v with SFix l -> l + 1

/// Decimal digits, one per byte (0-9), most significant first: a score. Digit i is at Addr + i.
type Digits =
    | Digits of Label * int
    member v.Addr = match v with Digits (l, _) -> l
    member v.Count = match v with Digits (_, n) -> n

let zpU8 name = U8 (zpVar name 1)
let zpU16 name = U16 (zpVar name 2)
let zpUFix name = UFix (zpVar name 2)
let zpSFix name = SFix (zpVar name 2)
let zpDigits name count = Digits (zpVar name count, count)

/// Raw 16-bit value of an unsigned 8.8 constant, rounded to 1/256. Fails outside 0 .. 255.996.
let ufix (v: float) =
    let r = int (System.Math.Round (v * 256.0))
    if r < 0 || r > 0xFFFF then failwithf "ufix %g: not in 0 .. 255.996" v
    r

/// Raw 16-bit value (two's complement) of a signed 8.8 constant. Fails outside -128 .. 127.996.
let sfix (v: float) =
    let r = int (System.Math.Round (v * 256.0))
    if r < -0x8000 || r > 0x7FFF then failwithf "sfix %g: not in -128 .. 127.996" v
    r &&& 0xFFFF

let private store16 (lo: Label) (hi: Label) (k: int) =
    LDA (Imm (k &&& 0xFF))
    STA (Zp lo)
    LDA (Imm (k >>> 8))
    STA (Zp hi)

/// lo:hi += k (16-bit constant). C = carried out of bit 15, V = signed overflow. Clobbers a.
let private add16k (lo: Label) (hi: Label) (k: int) =
    CLC ()
    LDA (Zp lo)
    ADC (Imm (k &&& 0xFF))
    STA (Zp lo)
    LDA (Zp hi)
    ADC (Imm (k >>> 8))
    STA (Zp hi)

// ---- reacting to flags

/// Run `body` if the carry flag is set (an unsigned add wrapped, a compare found >=).
let onCarry (body: unit -> unit) =
    let skip = label "no_carry"
    BCC skip
    body ()
    place skip

/// Run `body` if the carry flag is clear (an unsigned subtract went below 0).
let onNoCarry (body: unit -> unit) =
    let skip = label "carry"
    BCS skip
    body ()
    place skip

// ---- U8

/// v <- k. Clobbers a.
let set8 (v: U8) (k: int) =
    LDA (Imm k)
    STA (Zp v.Addr)

/// v += k, wraps. C = wrapped past 255. Clobbers a.
let add8 (v: U8) (k: int) =
    CLC ()
    LDA (Zp v.Addr)
    ADC (Imm k)
    STA (Zp v.Addr)

/// v -= k, wraps. C clear = went below 0. Clobbers a.
let sub8 (v: U8) (k: int) =
    SEC ()
    LDA (Zp v.Addr)
    SBC (Imm k)
    STA (Zp v.Addr)

/// v <- min (v + k) max, never wraps. Leaves a = v.
let add8Max (v: U8) (k: int) (max: int) =
    let clamp, store = label "add8_clamp", label "add8_store"
    CLC ()
    LDA (Zp v.Addr)
    ADC (Imm k)
    BCS clamp                       // past 255
    CMP (Imm max)
    BCC store                       // below max
    place clamp
    LDA (Imm max)
    place store
    STA (Zp v.Addr)

/// Run `body` if the byte at `l` (zero page) >= k, unsigned. Clobbers a.
let ifAtLeast (l: Label) (k: int) (body: unit -> unit) =
    LDA (Zp l)
    CMP (Imm k)
    onCarry body

/// Run `body` if the byte at `l` (zero page) < k, unsigned. Clobbers a.
let ifBelow (l: Label) (k: int) (body: unit -> unit) =
    LDA (Zp l)
    CMP (Imm k)
    onNoCarry body

/// Run `body` if the byte at `l` (zero page) is 0 / is not 0. Clobbers a.
let ifZero (l: Label) (body: unit -> unit) =
    let skip = label "not_zero"
    LDA (Zp l)
    BNE skip
    body ()
    place skip

let ifNonZero (l: Label) (body: unit -> unit) =
    let skip = label "zero"
    LDA (Zp l)
    BEQ skip
    body ()
    place skip

// ---- U16

/// v <- k (0..65535). Clobbers a.
let set16 (v: U16) (k: int) =
    if k < 0 || k > 0xFFFF then failwithf "set16 %d: not in 0..65535" k
    store16 v.Lo v.Hi k

/// v += 1, wraps. Registers untouched.
let inc16 (v: U16) = increment16 v.Lo

// ---- fixed point

/// v <- k. Clobbers a.
let setUFix (v: UFix) (k: float) = store16 v.Lo v.Hi (ufix k)

/// v <- k. Clobbers a.
let setSFix (v: SFix) (k: float) = store16 v.Lo v.Hi (sfix k)

/// v += k (signed constant: acceleration), wraps. V = left -128..127.996. Clobbers a.
let addSFix (v: SFix) (k: float) = add16k v.Lo v.Hi (sfix k)

/// pos += vel: move a position by a signed velocity, wraps. Clobbers a.
let addVel (pos: UFix) (vel: SFix) =
    CLC ()
    LDA (Zp pos.Lo)
    ADC (Zp vel.Lo)
    STA (Zp pos.Lo)
    LDA (Zp pos.Hi)
    ADC (Zp vel.Hi)
    STA (Zp pos.Hi)

/// N flag <- v < k (signed 16-bit), corrected for overflow in v - k. Clobbers a.
let private compareSFix (v: SFix) (k: int) =
    let noV = label "sfix_no_v"
    LDA (Zp v.Lo)
    CMP (Imm (k &&& 0xFF))
    LDA (Zp v.Hi)
    SBC (Imm (k >>> 8))             // sign of v - k, wrong if V
    BVC noV
    EOR (Imm 0x80)
    place noV

/// Run `body` if v < k (signed). Clobbers a.
let ifSFixBelow (v: SFix) (k: float) (body: unit -> unit) =
    let skip = label "sfix_not_below"
    compareSFix v (sfix k)
    BPL skip
    body ()
    place skip

/// Run `body` if v >= k (signed). Clobbers a.
let ifSFixAtLeast (v: SFix) (k: float) (body: unit -> unit) =
    let skip = label "sfix_below"
    compareSFix v (sfix k)
    BMI skip
    body ()
    place skip

/// v <- min v max (signed): a terminal velocity. Clobbers a.
let capSFix (v: SFix) (max: float) =
    ifSFixAtLeast v max (fun () -> store16 v.Lo v.Hi (sfix max))

/// v <- max v min (signed): a speed limit for negative velocities. Clobbers a.
let floorSFix (v: SFix) (min: float) =
    ifSFixBelow v min (fun () -> store16 v.Lo v.Hi (sfix min))

/// Run `body` if pos >= k (unsigned 16-bit). Clobbers a.
let ifUFixAtLeast (pos: UFix) (k: float) (body: unit -> unit) =
    let r = ufix k
    LDA (Zp pos.Lo)
    CMP (Imm (r &&& 0xFF))
    LDA (Zp pos.Hi)
    SBC (Imm (r >>> 8))             // C = pos >= k
    onCarry body

// ---- decimal digits

/// Every digit <- 0. Clobbers a, x; leaves x = $FF.
let clearDigits (d: Digits) =
    if d.Count > 128 then failwithf "clearDigits: %d digits (max 128)" d.Count
    LDA (Imm 0)
    LDX (Imm (d.Count - 1))
    let loop = here "clear_digits"
    STA (ZpX d.Addr)
    DEX ()
    BPL loop

/// d += k (a constant), digit by digit; sticks at 99..9 instead of wrapping. Clobbers a.
let addDigits (d: Digits) (k: int) =
    let n = d.Count
    if k < 0 || float k >= 10.0 ** float n then failwithf "addDigits %d: does not fit in %d digits" k n
    let fine = label "digits_fine"
    CLC ()
    for i in n - 1 .. -1 .. 0 do
        let digit = (k / pown 10 (n - 1 - i)) % 10
        let store = label "digit_store"
        LDA (Zp (d.Addr + i))
        ADC (Imm digit)
        CMP (Imm 10)                // C = carry into the next digit
        BCC store
        SBC (Imm 10)                // C stays set
        place store
        STA (Zp (d.Addr + i))
    BCC fine                        // carry out of the top digit: saturate
    LDA (Imm 9)
    for i in 0 .. n - 1 do STA (Zp (d.Addr + i))
    place fine

/// Nametable 0 at (`row`, `col`) <- the digits, left to right, as tiles `digit0` + 0..9.
/// Writes VRAM: only in vblank (NMI) or with rendering off; reset the scroll after.
/// ~10 cycles a digit. Clobbers a.
let drawDigits (d: Digits) row col (digit0: int) =
    if row < 0 || row > 29 || col < 0 || col + d.Count > 32 then
        failwithf "drawDigits: %d digits at row %d col %d leave the screen" d.Count row col
    if digit0 < 0 || digit0 + 9 > 255 then failwithf "drawDigits: digit tiles %d..%d" digit0 (digit0 + 9)
    resetPpuLatch ()
    setPpuAddr (0x2000 + row * 32 + col)
    for i in 0 .. d.Count - 1 do
        LDA (Zp (d.Addr + i))
        CLC ()
        ADC (Imm digit0)
        STA (Abs PPUDATA)

// ---- random

/// rnd <- (rnd rotated left + frames.Lo) xor frames.Hi xor player's pad: call once a frame.
/// Mask the bits you need (AND #3: 1 in 4); two values on the same frame take disjoint bits.
/// The add, not a xor, keeps it from being a plain function of frames.Lo (256 runs, lopsided).
/// Any value is fine, 0 included. Leaves a = rnd; clobbers C.
let stirRandom (rnd: U8) (frames: U16) player =
    LDA (Zp rnd.Addr)
    ASL Acc
    ADC (Imm 0)                     // bit 7 back into bit 0; C clear (at most $FE + 1)
    ADC (Zp frames.Lo)
    EOR (Zp frames.Hi)
    EOR (Zp (pads + (player - 1)))
    STA (Zp rnd.Addr)
