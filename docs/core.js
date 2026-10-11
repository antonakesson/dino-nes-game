// core.js: reads window.DINO (data.js, written by Site.fsx from a build with NES_TRACE=1, where
// every put() recorded its F# call stack) and offers:
//   R.rom, R.entries, R.entriesThrough(line)   the ROM and the byte -> source map
//   R.disasm(off, end), R.decode(off)          6502 listing of a file range
//   R.highlight(line)                          F# line -> HTML with token spans
//   R.startNes(container, onFrame)             jsnes + a per-instruction cycle counter
(function () {
  var D = window.DINO, R = {};
  var bin = atob(D.rom), rom = new Uint8Array(bin.length);
  for (var i = 0; i < bin.length; i++) rom[i] = bin.charCodeAt(i);
  R.rom = rom;
  R.PRG = 16; R.CHR = 16 + 0x4000; R.END = rom.length;
  R.src = { "Dino.fsx": D.src["Dino.fsx"].split("\n"), "Std.fsx": D.src["Std.fsx"].split("\n") };

  // entries: [fileOffset, length, dinoStack (innermost first), stdLine]
  R.entries = D.entries.map(function (e, i) { return { i: i, off: e[0], len: e[1], dino: e[2], std: e[3] }; });
  R.entryAt = new Int32Array(rom.length).fill(-1);
  R.entries.forEach(function (e) { for (var k = 0; k < e.len; k++) R.entryAt[e.off + k] = e.i; });

  // CPU address <-> file offset (NROM-128: $8000 and $C000 are the same 16 KB)
  R.offOf = function (a) { return a >= 0x8000 ? R.PRG + (a & 0x3FFF) : -1; };
  R.addrOf = function (o) { return o >= R.PRG && o < R.CHR ? 0xC000 + o - R.PRG : -1; };

  // symbols
  R.sym = {}; R.codeSyms = D.code;
  D.code.forEach(function (s) { if (!(s[0] in R.sym)) R.sym[s[0]] = s[1]; });
  R.ramSym = {};
  D.ram.forEach(function (s) { if (!(s[0] in R.ramSym)) R.ramSym[s[0]] = s[1]; });
  /// entries whose Dino call stack passes through line n (what that line put, inlined calls included)
  R.entriesThrough = function (n) { return R.entries.filter(function (e) { return e.dino.indexOf(n) >= 0; }); };

  // ---------------------------------------------------------------- 6502
  var OPS = {}, T = {
    ADC: "69i 65z 75zx 6Da 7Dax 79ay 61ix 71iy", AND: "29i 25z 35zx 2Da 3Dax 39ay 21ix 31iy",
    ASL: "0Aac 06z 16zx 0Ea 1Eax", BIT: "24z 2Ca", CMP: "C9i C5z D5zx CDa DDax D9ay C1ix D1iy",
    CPX: "E0i E4z ECa", CPY: "C0i C4z CCa", DEC: "C6z D6zx CEa DEax", EOR: "49i 45z 55zx 4Da 5Dax 59ay 41ix 51iy",
    INC: "E6z F6zx EEa FEax", JMP: "4Ca 6Cn", JSR: "20a", LDA: "A9i A5z B5zx ADa BDax B9ay A1ix B1iy",
    LDX: "A2i A6z B6zy AEa BEay", LDY: "A0i A4z B4zx ACa BCax", LSR: "4Aac 46z 56zx 4Ea 5Eax",
    ORA: "09i 05z 15zx 0Da 1Dax 19ay 01ix 11iy", ROL: "2Aac 26z 36zx 2Ea 3Eax", ROR: "6Aac 66z 76zx 6Ea 7Eax",
    SBC: "E9i E5z F5zx EDa FDax F9ay E1ix F1iy", STA: "85z 95zx 8Da 9Dax 99ay 81ix 91iy",
    STX: "86z 96zy 8Ea", STY: "84z 94zx 8Ca",
    BCC: "90r", BCS: "B0r", BEQ: "F0r", BMI: "30r", BNE: "D0r", BPL: "10r", BVC: "50r", BVS: "70r",
    BRK: "00-", CLC: "18-", CLD: "D8-", CLI: "58-", CLV: "B8-", DEX: "CA-", DEY: "88-", INX: "E8-", INY: "C8-",
    NOP: "EA-", PHA: "48-", PHP: "08-", PLA: "68-", PLP: "28-", RTI: "40-", RTS: "60-", SEC: "38-", SED: "F8-",
    SEI: "78-", TAX: "AA-", TAY: "A8-", TSX: "BA-", TXA: "8A-", TXS: "9A-", TYA: "98-"
  };
  var SIZE = { "-": 1, ac: 1, i: 2, z: 2, zx: 2, zy: 2, ix: 2, iy: 2, r: 2, a: 3, ax: 3, ay: 3, n: 3 };
  Object.keys(T).forEach(function (m) {
    T[m].split(" ").forEach(function (c) { OPS[parseInt(c.slice(0, 2), 16)] = { m: m, mode: c.slice(2) }; });
  });
  function hex(n, w) { return n.toString(16).toUpperCase().padStart(w, "0"); }
  R.hex = hex;
  function name(a, zp) {
    if (!zp && a >= 0x8000) return R.sym[a] || "$" + hex(a, 4);
    if (R.ramSym[a]) return R.ramSym[a];
    var back = a >= 0x200 && a < 0x300 ? 255 : 3;     // inside a variable, or shadow OAM
    for (var k = 1; k <= back && a - k >= 0; k++) if (R.ramSym[a - k]) return R.ramSym[a - k] + "+" + k;
    return "$" + hex(a, zp ? 2 : 4);
  }
  /// one instruction at file offset o: {off, addr, len, bytes, m, arg}
  R.decode = function (o) {
    var op = OPS[rom[o]], a = R.addrOf(o);
    if (!op) return { off: o, addr: a, len: 1, m: ".byte", arg: "$" + hex(rom[o], 2), data: true };
    var n = SIZE[op.mode], b1 = rom[o + 1], w = b1 | (rom[o + 2] << 8), arg = "";
    switch (op.mode) {
      case "ac": arg = "a"; break;
      case "i": arg = "#$" + hex(b1, 2); break;
      case "z": arg = name(b1, true); break;
      case "zx": arg = name(b1, true) + ",x"; break;
      case "zy": arg = name(b1, true) + ",y"; break;
      case "ix": arg = "(" + name(b1, true) + ",x)"; break;
      case "iy": arg = "(" + name(b1, true) + "),y"; break;
      case "a": arg = name(w); break;
      case "ax": arg = name(w) + ",x"; break;
      case "ay": arg = name(w) + ",y"; break;
      case "n": arg = "(" + name(w) + ")"; break;
      case "r": var t = a + 2 + (b1 < 128 ? b1 : b1 - 256); arg = R.sym[t] || "$" + hex(t, 4); break;
    }
    return { off: o, addr: a, len: n, m: op.m.toLowerCase(), arg: arg, bytes: Array.from(rom.slice(o, o + n)) };
  };
  /// instructions in [off, end); data entries come back as .byte rows
  R.disasm = function (off, end, asData) {
    var out = [];
    for (var o = off; o < end;) {
      var d = asData ? { off: o, addr: R.addrOf(o), len: Math.min(8, end - o), m: ".byte", data: true } : R.decode(o);
      if (d.data) d.arg = Array.from(rom.slice(o, o + d.len)).map(function (b) { return "$" + hex(b, 2); }).join(",");
      d.bytes = Array.from(rom.slice(o, o + d.len));
      out.push(d); o += d.len;
    }
    return out;
  };
  /// entries -> contiguous runs [{off, end, entries}]
  R.runs = function (es) {
    var runs = [];
    es.slice().sort(function (a, b) { return a.off - b.off; }).forEach(function (e) {
      var r = runs[runs.length - 1];
      if (r && r.end === e.off) { r.end += e.len; r.entries.push(e); }
      else runs.push({ off: e.off, end: e.off + e.len, entries: [e] });
    });
    return runs;
  };
  /// is this run code? (code lives in PRG and was put by a Cpu6502 mnemonic, i.e. has a Std or
  /// mnemonic line; data is put by `put palette`, `put (chrBank ..)`, text...)
  R.isCode = function (run) {
    if (run.off < R.PRG || run.off >= R.CHR) return false;
    if (R.addrOf(run.off) >= 0xFFFA) return false;
    var line = R.src["Dino.fsx"][run.entries[0].dino[0] - 1] || "";
    return !/^\s*put\b/.test(line);
  };

  // ---------------------------------------------------------------- F# highlighting
  var KW = /^(let|fun|for|in|do|if|then|else|match|with|type|open|yield|rec|and|private|member|static|failwith|failwithf|when|not|true|false|mutable)$/;
  var MN = /^(ADC|AND|ASL|BCC|BCS|BEQ|BIT|BMI|BNE|BPL|BRK|BVC|BVS|CLC|CLD|CLI|CLV|CMP|CPX|CPY|DEC|DEX|DEY|EOR|INC|INX|INY|JMP|JSR|LDA|LDX|LDY|LSR|NOP|ORA|PHA|PHP|PLA|PLP|ROL|ROR|RTI|RTS|SBC|SEC|SED|SEI|STA|STX|STY|TAX|TAY|TSX|TXA|TXS|TYA)$/;
  var MODE = /^(Imm|ImmLo|ImmHi|Zp|ZpX|ZpY|Abs|AbsX|AbsY|Ind|IndX|IndY|Acc)$/;
  function esc(s) { return s.replace(/&/g, "&amp;").replace(/</g, "&lt;"); }
  R.esc = esc;
  R.highlight = function (line) {
    var out = "", re = /(\/\/\/?.*$)|("(?:[^"\\]|\\.)*"B?)|('.')|(0x[0-9A-Fa-f]+|\d+\.?\d*(?:uy)?)|([A-Za-z_][\w']*)|(\s+)|(.)/g, m;
    while ((m = re.exec(line))) {
      if (m[1]) out += '<span class="t-com">' + esc(m[1]) + "</span>";
      else if (m[2] || m[3]) out += '<span class="t-str">' + esc(m[2] || m[3]) + "</span>";
      else if (m[4]) out += '<span class="t-num">' + m[4] + "</span>";
      else if (m[5]) {
        var w = m[5];
        out += KW.test(w) ? '<span class="t-kw">' + w + "</span>"
          : MN.test(w) ? '<span class="t-mn">' + w + "</span>"
          : MODE.test(w) ? '<span class="t-mode">' + w + "</span>"
          : '<span class="t-id">' + w + "</span>";
      } else out += esc(m[6] || m[7]);
    }
    return out;
  };

  // ---------------------------------------------------------------- emulator
  /// jsnes in `container`; onFrame(cycles) after every frame, cycles[addr] = CPU cycles spent on
  /// the instruction at addr during that frame. Returns the jsnes Browser.
  R.startNes = function (container, onFrame) {
    var C = jsnes.Controller;
    var browser = new jsnes.Browser({ container: container, onError: function (e) { console.error(e); } });
    browser.keyboard.keys = {
      13: [1, C.BUTTON_START, "Enter"], 32: [1, C.BUTTON_A, "Space"], 88: [1, C.BUTTON_A, "X"],
      38: [1, C.BUTTON_A, "Up"], 90: [1, C.BUTTON_B, "Z"], 89: [1, C.BUTTON_B, "Y"], 16: [1, C.BUTTON_B, "Shift"]
    };
    var b = Uint8Array.from(rom), s = "";
    for (var i = 0; i < b.length; i++) s += String.fromCharCode(b[i]);
    browser.loadROM(s);     // (re)creates the CPU: hook it after
    var nes = browser.nes, cpu = nes.cpu, cycles = new Uint32Array(0x10000);
    // jsnes 2.1.0: REG_PC is one less than the next instruction's address
    var emulate = cpu.emulate;
    cpu.emulate = function () { var a = (cpu.REG_PC + 1) & 0xFFFF, c = emulate.call(cpu); cycles[a] += c; return c; };
    var frame = nes.frame;
    nes.frame = function () { cycles.fill(0); var r = frame.apply(nes, arguments); if (onFrame) onFrame(cycles); return r; };
    document.addEventListener("visibilitychange", function () {
      var timer = browser._frameTimer, audio = browser._speakers.audioCtx;
      if (document.hidden) { timer.stop(); if (audio && audio.state === "running") audio.suspend(); }
      else { if (!timer.running) timer.start(); if (audio && audio.state === "suspended") audio.resume(); }
    });
    R.browser = browser;
    return browser;
  };
  /// touch / click buttons: elements with data-btn="A|B|START"
  R.bindPad = function (root) {
    var C = jsnes.Controller;
    root.querySelectorAll("[data-btn]").forEach(function (b) {
      var btn = C["BUTTON_" + b.dataset.btn];
      b.addEventListener("pointerdown", function (e) { e.preventDefault(); R.browser.nes.buttonDown(1, btn); });
      ["pointerup", "pointercancel", "pointerleave"].forEach(function (t) {
        b.addEventListener(t, function () { R.browser.nes.buttonUp(1, btn); });
      });
    });
  };

  /// CHR tile t (0-511) -> canvas, 4 shades
  R.tileCanvas = function (t, scale, shades) {
    shades = shades || ["transparent", "#8a8a8a", "#d0d0d0", "#ffffff"];
    var c = document.createElement("canvas"); c.width = c.height = 8 * scale;
    var g = c.getContext("2d"), o = R.CHR + t * 16;
    for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++) {
      var v = ((rom[o + y] >> (7 - x)) & 1) | (((rom[o + 8 + y] >> (7 - x)) & 1) << 1);
      if (v) { g.fillStyle = shades[v]; g.fillRect(x * scale, y * scale, scale, scale); }
    }
    return c;
  };

  window.R = R;
})();
