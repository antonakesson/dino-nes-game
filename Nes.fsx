// Nes.fsx — write an iNES ROM imperatively, byte by byte, top to bottom.
//
//   startRom ()
//   put (inesHeader 0 1 1 Mirroring.Horizontal)
//   org 0xC000                    // CPU address of the next byte
//   place reset; initCpu (); ...  // code: each helper puts its bytes right away
//   put (chrBank tiles)
//   finishRom () |> write "out.nes"
//
// The file is the stream. Instruction sizes are fixed by their opcode, so a use
// of a label that is not placed yet puts a placeholder and records a fixup;
// `finishRom` backpatches them all. Plain F# runs between puts: compute sizes,
// assert, printfn.
//
// You still write the opcode byte yourself; the helpers only decide how the
// operand after it is resolved (literal, absolute, zero page, branch offset).

open System.IO
open System.Collections.Generic

// ---------------------------------------------------------------- byte helpers

let u8  (v: int) = [| byte v |]
let u16 (v: int) = [| byte v; byte (v >>> 8) |]   // little-endian, like the 6502
let cat = Array.concat
let padWith (fill: byte) n (b: byte[]) =
    if b.Length > n then failwithf "%d bytes do not fit in %d" b.Length n
    Array.append b (Array.create (n - b.Length) fill)

// ---------------------------------------------------------------- labels

type LabelKind =
    | Code              // address is wherever `place` puts it
    | Fixed of int      // equate: hardware register, VRAM address, ...
    | ZeroPage of int   // RAM variable of n bytes; the linker picks the address
    | Offset of Label * int   // `l + n`: n bytes past another label

/// A symbol. Labels compare by identity, so two with the same name never clash;
/// the name is only for error messages and the FCEUX debugger.
and [<Sealed>] Label(name: string, kind: LabelKind) =
    member _.Name = name
    member _.Kind = kind
    override _.ToString() = name
    /// `frames + 1`, `oam + 3`: n bytes past `l`. Within a zero-page variable, n must be < its size.
    static member (+) (l: Label, n: int) =
        match l.Kind with
        | ZeroPage size when n < 0 || n >= size -> failwithf "%s + %d: %s is only %d bytes" l.Name n l.Name size
        | _ -> Label(sprintf "%s+%d" l.Name n, Offset(l, n))

/// Code/data label: declare it, `place` it, use it anywhere (also before it is placed).
let label name = Label(name, Code)
/// Name for a fixed address.
let equ name addr = Label(name, Fixed addr)
/// Zero-page variable of `size` bytes, allocated from $00 in order of first use.
let zpVar name size = Label(name, ZeroPage size)

// ---------------------------------------------------------------- hardware

let PPUCTRL   = equ "PPUCTRL"   0x2000
let PPUMASK   = equ "PPUMASK"   0x2001
let PPUSTATUS = equ "PPUSTATUS" 0x2002
let OAMADDR   = equ "OAMADDR"   0x2003
let OAMDATA   = equ "OAMDATA"   0x2004
let PPUSCROLL = equ "PPUSCROLL" 0x2005
let PPUADDR   = equ "PPUADDR"   0x2006
let PPUDATA   = equ "PPUDATA"   0x2007
let DMC_FREQ  = equ "DMC_FREQ"  0x4010
let OAMDMA    = equ "OAMDMA"    0x4014
let JOY1      = equ "JOY1"      0x4016
let JOY2      = equ "JOY2"      0x4017
let APU_FRAME = equ "APU_FRAME" 0x4017   // same address as JOY2: write = APU, read = pad 2

// ---------------------------------------------------------------- the stream

type FixKind = Abs16 | Zp8 | Lo8 | Hi8 | Rel8

/// A placeholder to backpatch: `At` = file offset of the operand, `Pc` = CPU address
/// of the instruction it belongs to (branches count from there).
type Fixup = { At: int; Pc: int; Target: Label; Kind: FixKind }

type Writer =
    { Out: ResizeArray<byte>
      mutable Org: (int * int) option            // (file offset, CPU address) of the last `org`
      Placed: Dictionary<Label, int>            // code labels placed so far, and zero-page variables
      mutable NextZp: int
      Used: HashSet<Label>                       // equates referenced, for the symbol files
      Fixups: ResizeArray<Fixup>
      Called: HashSet<Label>                     // subroutines called so far
      Pending: Queue<Label * (unit -> unit)> }   // ... whose bodies are not put yet

let private newWriter () =
    { Out = ResizeArray(); Org = None
      Placed = Dictionary(HashIdentity.Reference); NextZp = 0
      Used = HashSet(HashIdentity.Reference); Fixups = ResizeArray()
      Called = HashSet(HashIdentity.Reference); Pending = Queue() }

let mutable private w = newWriter ()

/// Start a new ROM; everything `put` from here on goes into it.
let startRom () = w <- newWriter ()

/// Append bytes to the file.
let put (b: byte[]) = w.Out.AddRange b

/// File offset of the next byte.
let offset () = w.Out.Count

/// The next byte appears at CPU address `addr`.
let org addr = w.Org <- Some (w.Out.Count, addr)

/// CPU address of the next byte.
let pc () =
    match w.Org with
    | Some (f, a) -> a + (w.Out.Count - f)
    | None -> failwith "pc: no org yet"

/// Give `l` the current CPU address.
let place (l: Label) =
    if l.Kind <> Code then failwithf "%s is not a code label" l.Name
    if w.Placed.ContainsKey l then failwithf "label %s is placed twice" l.Name
    w.Placed.[l] <- pc ()

/// Declare and place a label in one go (for backward references: loops).
let here name =
    let l = label name
    place l
    l

/// Fill with $FF up to CPU address `addr`; fails if the code already passed it.
let padTo addr =
    let p = pc ()
    if addr < p then failwithf "padTo $%04X, but code already reaches $%04X" addr p
    put (Array.create (addr - p) 0xFFuy)

/// Bytes `f` puts, measured in a scratch stream (nothing reaches the ROM).
let sizeOf (f: unit -> unit) =
    let saved = w
    w <- { newWriter () with Org = Some (0, (match saved.Org with Some _ -> pc () | None -> 0)) }
    try f (); w.Out.Count
    finally w <- saved

// ---------------------------------------------------------------- instructions

/// Address of `l` if it is known now: an equate, a placed label, or a zero-page
/// variable (allocated on first use). None for a label not placed yet.
let rec private tryResolve (l: Label) =
    match w.Placed.TryGetValue l, l.Kind with
    | (true, a), _ -> Some a
    | _, Offset (b, n) -> tryResolve b |> Option.map ((+) n)
    | _, Fixed a ->
        w.Used.Add l |> ignore
        Some a
    | _, ZeroPage n ->
        let a = w.NextZp
        w.NextZp <- a + n
        if w.NextZp > 0x100 then failwithf "zero page full at %s" l.Name
        w.Placed.[l] <- a
        Some a
    | _, Code -> None

/// Operand bytes for `kind` pointing at `a`, for the instruction at `pc`.
let private encode kind (l: Label) pc a =
    match kind with
    | Abs16 -> u16 a
    | Lo8 -> u8 a
    | Hi8 -> u8 (a >>> 8)
    | Zp8 ->
        if a > 0xFF then failwithf "%s = $%04X is not zero page" l.Name a
        u8 a
    | Rel8 ->
        let d = a - (pc + 2)
        if d < -128 || d > 127 then failwithf "branch at $%04X to %s out of range (%d)" pc l.Name d
        u8 d

/// Operand of `size` bytes: computed right here if `l` is known, else a placeholder
/// that `finishRom` backpatches (forward references only).
let private operand kind (l: Label) p size =
    match tryResolve l with
    | Some a -> put (encode kind l p a)
    | None ->
        w.Fixups.Add { At = w.Out.Count; Pc = p; Target = l; Kind = kind }
        put (Array.zeroCreate size)

let private fixup kind opcode (l: Label) size =
    let p = pc ()
    put [| byte opcode |]
    operand kind l p size

/// Literal bytes: an opcode plus any literal operand.  `op [0xA9; 0x00]  // lda #$00`
let op (bs: int list) = put (bs |> List.map byte |> Array.ofList)

/// Opcode + 16-bit absolute address of `l`.
let abs opcode l = fixup Abs16 opcode l 2
/// Opcode + zero-page address of `l` (must be below $100).
let zp opcode l = fixup Zp8 opcode l 1
/// Opcode + immediate low / high byte of `l`.
let lo opcode l = fixup Lo8 opcode l 1
let hi opcode l = fixup Hi8 opcode l 1
/// Branch: opcode + signed offset from the next instruction to `l`.
let rel opcode l = fixup Rel8 opcode l 1

/// 16-bit address of `l` as data (vectors, jump tables).
let word (l: Label) = operand Abs16 l (pc ()) 2

// ---------------------------------------------------------------- subroutines

/// Turn a snippet into a subroutine. Calling the result puts `jsr name`; the body
/// (plus `rts`) is put once, at the next `emitSubroutines ()`, and only if something
/// called it. Call sites look the same as for an inline snippet.
let subroutine name (body: unit -> unit) : unit -> unit =
    let l = label name
    fun () ->
        if w.Called.Add l then w.Pending.Enqueue((l, body))
        abs 0x20 l                      // jsr name

/// Put the bodies of all subroutines called so far (and those they call).
let emitSubroutines () =
    while w.Pending.Count > 0 do
        let l, body = w.Pending.Dequeue()
        place l
        body ()
        op [ 0x60 ]                     // rts

// ---------------------------------------------------------------- CHR

/// One 8x8 tile from 8 rows of art: '.' = colour 0, '#' or '1' = 1, '2', '3'.
let tile (rows: string list) =
    if rows.Length <> 8 || rows |> List.exists (fun r -> r.Length <> 8) then
        failwithf "tile needs 8 rows of 8 pixels: %A" rows
    let px (r: string) c =
        match r.[c] with
        | '.' -> 0 | '#' | '1' -> 1 | '2' -> 2 | '3' -> 3
        | ch -> failwithf "bad pixel '%c'" ch
    // a tile is two bit planes: low bits of all 8 rows, then high bits
    let plane bit =
        [| for r in rows -> byte (Seq.fold (fun acc c -> (acc <<< 1) ||| ((px r c >>> bit) &&& 1)) 0 [ 0 .. 7 ]) |]
    Array.append (plane 0) (plane 1)

/// Tiles cut from a picture whose sides are multiples of 8, left to right, top to bottom
/// (16x16 -> top-left, top-right, bottom-left, bottom-right). Same pixels as `tile`.
let tiles (rows: string list) =
    let h = rows.Length
    let w = if h = 0 then 0 else rows.Head.Length
    if h = 0 || h % 8 <> 0 || w % 8 <> 0 || rows |> List.exists (fun r -> r.Length <> w) then
        failwithf "tiles needs rows of equal length, both sides multiples of 8: %A" rows
    [ for ty in 0 .. h / 8 - 1 do
        for tx in 0 .. w / 8 - 1 ->
            tile [ for r in 0 .. 7 -> rows.[ty * 8 + r].Substring(tx * 8, 8) ] ]

/// 8 KB CHR bank from tiles, in order (tile 0 first), zero-filled.
let chrBank (tiles: byte[] list) = cat tiles |> padWith 0uy 0x2000

// ---------------------------------------------------------------- iNES header

/// How the cartridge wires the PPU's four nametable slots onto its 2 KB of VRAM.
/// Horizontal: $2000=$2400 and $2800=$2C00 (screens stacked, for vertical scrolling).
/// Vertical:   $2000=$2800 and $2400=$2C00 (screens side by side, for horizontal scrolling).
type Mirroring = Horizontal = 0 | Vertical = 1

/// 16-byte iNES header. `finishRom` checks the file matches the bank counts.
let inesHeader (mapper: int) (prgBanks: int) (chrBanks: int) (mirroring: Mirroring) =
    cat [
        "NES\026"B                             // magic
        u8 prgBanks                            // PRG banks (16 KB)
        u8 chrBanks                            // CHR banks (8 KB)
        u8 (((mapper &&& 0x0F) <<< 4) ||| int mirroring)   // flags 6: mapper low nibble, mirroring
        u8 (mapper &&& 0xF0)                   // flags 7: mapper high nibble
        Array.zeroCreate 8
    ]

// ---------------------------------------------------------------- finish

type Symbol = { Name: string; Addr: int; IsCode: bool }

type Image =
    { Bytes: byte[]
      Symbols: Symbol list }          // every label placed or used, sorted by address

/// Backpatch the forward references and check the file against its iNES header.
let finishRom () : Image =
    let out = w.Out.ToArray()
    for f in w.Fixups do
        match tryResolve f.Target with
        | Some a ->
            let b = encode f.Kind f.Target f.Pc a
            Array.blit b 0 out f.At b.Length
        | None when w.Called.Contains f.Target ->
            failwithf "subroutine %s is called but never put: add emitSubroutines ()" f.Target.Name
        | None -> failwithf "label %s is used but never placed" f.Target.Name

    if out.Length >= 16 && out.[0..3] = "NES\026"B then
        let want = 16 + int out.[4] * 0x4000 + int out.[5] * 0x2000
        if out.Length <> want then
            failwithf "header promises %d bytes (%d PRG + %d CHR banks), file has %d" want out.[4] out.[5] out.Length

    // names for the debugger; labels sharing a name get _2, _3, ...
    let seen = Dictionary<string, int>()
    let addrs = Dictionary<Label, int>(w.Placed, HashIdentity.Reference)
    for l in w.Used do
        match l.Kind with Fixed a -> addrs.[l] <- a | _ -> ()
    let symbols =
        [ for KeyValue(l, a) in addrs ->
            let n = match seen.TryGetValue l.Name with | true, k -> k + 1 | _ -> 1
            seen.[l.Name] <- n
            { Name = (if n = 1 then l.Name else sprintf "%s_%d" l.Name n)
              Addr = a
              IsCode = (l.Kind = Code) } ]
        |> List.sortBy (fun s -> s.Addr)
    { Bytes = out; Symbols = symbols }

// ---------------------------------------------------------------- output

/// Writes the ROM plus FCEUX symbol files next to it, so the debugger shows names:
/// `<rom>.0.nl` for PRG labels and `<rom>.ram.nl` for other symbols below $8000.
let write (path: string) (img: Image) =
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
    File.WriteAllBytes(path, img.Bytes)
    let nl pick = [ for s in img.Symbols do if pick s then yield sprintf "$%04X#%s#" s.Addr s.Name ]
    File.WriteAllLines(path + ".0.nl",   nl (fun s -> s.IsCode))
    File.WriteAllLines(path + ".ram.nl", nl (fun s -> not s.IsCode && s.Addr < 0x8000))

    printfn "wrote %s (%d bytes)" path img.Bytes.Length
    for s in img.Symbols do
        printfn "  sym %-16s $%04X" s.Name s.Addr
