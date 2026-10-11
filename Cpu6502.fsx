// Cpu6502.fsx — 6502 mnemonics that put instructions: `LDA (Imm 0x40)`, `STA (Abs PPUADDR)`, `BNE loop`.
//
// Each mnemonic looks up the opcode for the addressing mode you give it, so the
// opcode byte always matches the mnemonic. A mode the instruction does not have
// fails the build. You pick the mode (Zp vs Abs), so you still pick every byte.
// (Not "6502.fsx": a script's module name cannot start with a digit.)

#load "Nes.fsx"
open Nes

/// Addressing mode + operand.
type Operand =
    | Imm of int        // #$nn
    | ImmLo of Label    // #<label
    | ImmHi of Label    // #>label
    | Zp of Label       // $nn
    | ZpX of Label      // $nn,x
    | ZpY of Label      // $nn,y
    | Abs of Label      // $nnnn
    | AbsX of Label     // $nnnn,x
    | AbsY of Label     // $nnnn,y
    | Ind of Label      // ($nnnn)       JMP only
    | IndX of Label     // ($nn,x)
    | IndY of Label     // ($nn),y
    | Acc               // a             shifts and rotates

type Mode = MImm | MZp | MZpX | MZpY | MAbs | MAbsX | MAbsY | MInd | MIndX | MIndY | MAcc

let private modeOf = function
    | Imm _ | ImmLo _ | ImmHi _ -> MImm
    | Zp _ -> MZp | ZpX _ -> MZpX | ZpY _ -> MZpY
    | Abs _ -> MAbs | AbsX _ -> MAbsX | AbsY _ -> MAbsY
    | Ind _ -> MInd | IndX _ -> MIndX | IndY _ -> MIndY
    | Acc -> MAcc

/// Every (mnemonic, mode) -> opcode, filled in as the instructions below are defined.
let opcodes = System.Collections.Generic.Dictionary<string * Mode, int>()

let private instr name (table: (Mode * int) list) =
    for m, c in table do opcodes.[(name, m)] <- c
    fun (o: Operand) ->
        let opc =
            match List.tryFind (fst >> (=) (modeOf o)) table with
            | Some (_, c) -> c
            | None -> failwithf "%s has no %A mode (it has %A)" name (modeOf o) (List.map fst table)
        match o with
        | Imm v ->
            if v < 0 || v > 0xFF then failwithf "%s #%d: immediate must be 0-255" name v
            op [ opc; v ]
        | ImmLo l -> lo opc l
        | ImmHi l -> hi opc l
        | Zp l | ZpX l | ZpY l | IndX l | IndY l -> zp opc l
        | Abs l | AbsX l | AbsY l | Ind l -> abs opc l
        | Acc -> op [ opc ]

let private implied name opc =
    opcodes.[(name, MAcc)] <- opc
    fun () -> op [ opc ]

let private branch name opc : Label -> unit =
    opcodes.[(name, MAbs)] <- opc
    fun (l: Label) -> rel opc l

// The eight ALU instructions share one layout: opcode = base + offset per mode.
let private alu name b =
    instr name [ MImm, b + 0x08; MZp, b + 0x04; MZpX, b + 0x14; MAbs, b + 0x0C
                 MAbsX, b + 0x1C; MAbsY, b + 0x18; MIndX, b; MIndY, b + 0x10 ]

// So do the four shifts and rotates.
let private shift name b =
    instr name [ MAcc, b + 0x0A; MZp, b + 0x06; MZpX, b + 0x16; MAbs, b + 0x0E; MAbsX, b + 0x1E ]

// ---------------------------------------------------------------- load / store

let LDA = alu "LDA" 0xA1
let LDX = instr "LDX" [ MImm, 0xA2; MZp, 0xA6; MZpY, 0xB6; MAbs, 0xAE; MAbsY, 0xBE ]
let LDY = instr "LDY" [ MImm, 0xA0; MZp, 0xA4; MZpX, 0xB4; MAbs, 0xAC; MAbsX, 0xBC ]
let STA = instr "STA" [ MZp, 0x85; MZpX, 0x95; MAbs, 0x8D; MAbsX, 0x9D; MAbsY, 0x99; MIndX, 0x81; MIndY, 0x91 ]
let STX = instr "STX" [ MZp, 0x86; MZpY, 0x96; MAbs, 0x8E ]
let STY = instr "STY" [ MZp, 0x84; MZpX, 0x94; MAbs, 0x8C ]

// ---------------------------------------------------------------- arithmetic / logic

let ORA = alu "ORA" 0x01
let AND = alu "AND" 0x21
let EOR = alu "EOR" 0x41
let ADC = alu "ADC" 0x61
let CMP = alu "CMP" 0xC1
let SBC = alu "SBC" 0xE1
let CPX = instr "CPX" [ MImm, 0xE0; MZp, 0xE4; MAbs, 0xEC ]
let CPY = instr "CPY" [ MImm, 0xC0; MZp, 0xC4; MAbs, 0xCC ]
let BIT = instr "BIT" [ MZp, 0x24; MAbs, 0x2C ]
let INC = instr "INC" [ MZp, 0xE6; MZpX, 0xF6; MAbs, 0xEE; MAbsX, 0xFE ]
let DEC = instr "DEC" [ MZp, 0xC6; MZpX, 0xD6; MAbs, 0xCE; MAbsX, 0xDE ]
let ASL = shift "ASL" 0x00
let ROL = shift "ROL" 0x20
let LSR = shift "LSR" 0x40
let ROR = shift "ROR" 0x60

// ---------------------------------------------------------------- implied: `INX ()`

let INX = implied "INX" 0xE8
let INY = implied "INY" 0xC8
let DEX = implied "DEX" 0xCA
let DEY = implied "DEY" 0x88
let TAX = implied "TAX" 0xAA
let TAY = implied "TAY" 0xA8
let TXA = implied "TXA" 0x8A
let TYA = implied "TYA" 0x98
let TSX = implied "TSX" 0xBA
let TXS = implied "TXS" 0x9A
let PHA = implied "PHA" 0x48
let PHP = implied "PHP" 0x08
let PLA = implied "PLA" 0x68
let PLP = implied "PLP" 0x28
let CLC = implied "CLC" 0x18
let SEC = implied "SEC" 0x38
let CLI = implied "CLI" 0x58
let SEI = implied "SEI" 0x78
let CLD = implied "CLD" 0xD8
let SED = implied "SED" 0xF8
let CLV = implied "CLV" 0xB8
let NOP = implied "NOP" 0xEA
let BRK = implied "BRK" 0x00
let RTS = implied "RTS" 0x60
let RTI = implied "RTI" 0x40

// ---------------------------------------------------------------- control flow

let BPL = branch "BPL" 0x10
let BMI = branch "BMI" 0x30
let BVC = branch "BVC" 0x50
let BVS = branch "BVS" 0x70
let BCC = branch "BCC" 0x90
let BCS = branch "BCS" 0xB0
let BNE = branch "BNE" 0xD0
let BEQ = branch "BEQ" 0xF0
let JMP = instr "JMP" [ MAbs, 0x4C; MInd, 0x6C ]
let JSR = instr "JSR" [ (MAbs, 0x20) ]
