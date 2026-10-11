// rommap.js: "inside the cartridge". The file as a strip of sections (header, code, data, free,
// vectors, CHR), each clickable for what is in it and which F# line put it. The code section
// shows which bytes the CPU ran this frame. Needs core.js. R.romMap(el) returns an onFrame.
(function () {
  var hex = R.hex, rom = R.rom, L = R.src["Dino.fsx"];
  // FCEUX's default NES palette
  var NESPAL = ("747474 24188C 0000A8 44009C 8C0074 A80010 A40000 7C0800 402C00 004400 005000 003C14 183C5C 000000 000000 000000 " +
    "BCBCBC 0070EC 2038EC 8000F0 BC00BC E40058 D82800 C84C0C 887000 009400 00A800 009038 008088 000000 000000 000000 " +
    "FCFCFC 3CBCFC 5C94FC CC88FC F478FC FC74B4 FC7460 FC9838 F0BC3C 80D010 4CDC48 58F898 00E8D8 787878 000000 000000 " +
    "FCFCFC A8E4FC C4D4FC D4C8FC FCC4FC FCC4D8 FCBCB0 FCD8A8 FCE4A0 E0FCA0 A8F0BC B0FCCC 9CFCF0 C4C4C4 000000 000000").split(" ");

  function isPad(e) { return e.len > 16 && rom[e.off] === 0xFF && rom[e.off + 1] === 0xFF; }
  function kind(o) {
    if (o < 16) return "hdr";
    if (o >= R.CHR) return "chr";
    var e = R.entries[R.entryAt[o]];
    if (!e || isPad(e)) return "free";
    if (R.addrOf(o) >= 0xFFFA) return "vec";
    return R.isCode({ off: o, entries: [e] }) ? "code" : "data";
  }
  var secs = [], last = null;
  for (var o = 0; o < rom.length; o++) {
    var k = kind(o);
    if (!last || last.k !== k) secs.push(last = { k: k, from: o, to: o + 1 }); else last.to = o + 1;
  }
  var NAME = { hdr: "Header", code: "Code", data: "Data", free: "Free", vec: "Vectors", chr: "Tiles" };
  var SUB = { hdr: "iNES", code: "6502", data: "palette, text", free: "$FF", vec: "NMI · RESET · IRQ", chr: "CHR ROM" };
  var FLEX = { hdr: 7, code: 30, data: 8, free: 20, vec: 9, chr: 26 };   // readable, not to scale

  function lineHtml(n) {
    return '<div class="fs"><span class="n">' + n + "</span>" + R.highlight(L[n - 1].trim()) + "</div>";
  }
  function where(s) {
    var a = R.addrOf(s.from), b = R.addrOf(s.to - 1);
    if (s.k === "hdr") return "file +0000–000F · not mapped";
    if (s.k === "chr") return "PPU $0000–$1FFF";
    return "CPU $" + hex(a, 4) + "–$" + hex(b, 4);
  }
  function tile(t, scale, shades) { var c = R.tileCanvas(t, scale, shades); c.className = "tile"; return c; }
  var gray = ["transparent", "#7b8190", "#b9bec9", "#ffffff"];

  // what each section says when picked
  var detail = {
    hdr: function () {
      var f = [[0, 4, 'magic: "NES" + $1A'], [4, 1, "PRG ROM: 1 × 16 KB"], [5, 1, "CHR ROM: 1 × 8 KB"], [6, 1, "mapper 0, horizontal mirroring"], [7, 1, "flags 7"], [8, 8, "unused"]];
      return { text: "Sixteen bytes that tell the emulator what board this is. Mapper 0 (NROM) is the simplest cartridge: no bank switching, code at $C000, tiles wired straight to the PPU.",
        lines: [L.findIndex(function (t) { return /inesHeader 0 1 1/.test(t); }) + 1],
        right: '<div class="hrows">' + f.map(function (x) {
          return '<div class="hrow"><span class="by">' + Array.from(rom.slice(x[0], x[0] + x[1])).map(function (b) { return hex(b, 2); }).join(" ") + "</span><span>" + x[2] + "</span></div>";
        }).join("") + "</div>" };
    },
    code: function (s) {
      var names = {};
      [R.src["Dino.fsx"], R.src["Std.fsx"]].forEach(function (src) {
        src.forEach(function (t) { var m = /subroutine "(\w+)"/.exec(t); if (m) names[m[1]] = 1; });
      });
      names.nmi = 1;
      var subs = R.codeSyms.filter(function (x) { return names[x[1]]; }).sort(function (a, b) { return a[0] - b[0]; });
      var bounds = [[0xC000, "main program"]].concat(subs.map(function (x) { return [x[0], x[1]]; }));
      var end = R.addrOf(s.to);
      var bar = bounds.map(function (b, i) {
        var n = (i + 1 < bounds.length ? bounds[i + 1][0] : end) - b[0];
        return '<i style="flex:' + n + '" title="' + b[1] + " · " + n + ' bytes"></i>';
      }).join("");
      return { text: "Every instruction was put by a function call in F#. <code>LDA (Imm 0x40)</code> appends <code>A9 40</code>; snippets like <code>jump ()</code> call many of those. The game loop, " + (bounds.length - 2) + " subroutines and the NMI handler. <b class='hot'>Orange</b>: running this frame.",
        lines: [L.findIndex(function (t) { return /^let mainLoop/.test(t); }) + 1, L.findIndex(function (t) { return /^emitSubroutines/.test(t); }) + 1],
        right: '<div class="subbar">' + bar + '</div><div class="subcap">main program, then each subroutine in the order it was first called (hover for names)</div><canvas class="heat" id="heatBig" width="460" height="56"></canvas><div class="subcap" id="cpuPct">CPU</div>' };
    },
    data: function (s) {
      var items = [], rows = "";
      R.runs(R.entries.filter(function (e) { return e.off >= s.from && e.off < s.to; })).forEach(function (r) {
        r.entries.forEach(function (e) {
          var a = R.addrOf(e.off), lab = R.sym[a] || "";
          items.push({ lab: lab, e: e });
        });
      });
      items.forEach(function (it) {
        var e = it.e, b = Array.from(rom.slice(e.off, e.off + e.len)), vis = "";
        if (it.lab === "palette") {
          vis = '<div class="pal">' + b.map(function (c, i) { return '<i style="background:#' + NESPAL[c & 63] + '" title="$' + hex(c, 2) + '"></i>' + (i % 4 === 3 ? "<s></s>" : ""); }).join("") + "</div>";
        } else {
          vis = '<div class="txt">' + b.map(function (t) { return t ? tile(t, 2, gray).outerHTML.replace("<canvas", '<canvas data-t="' + t + '"') : '<span class="sp"></span>'; }).join("") + "</div>";
        }
        rows += '<div class="ditem"><span class="lab">' + it.lab + " <span style='color:var(--dim)'>" + e.len + "</span></span>" + vis + "</div>";
      });
      return { text: "Tables the code reads: 32 palette entries (NES colour numbers, not RGB) and three strings, already turned into tile numbers by an F# function at build time.",
        lines: items.map(function (it) { return it.e.dino[0]; }), right: rows };
    },
    free: function (s) {
      return { text: "Unused PRG ROM, filled with $FF by <code>padTo 0xFFFA</code> inside <code>vectors</code>. The build prints this number every time, and fails if the code ever grows past it.",
        lines: [L.findIndex(function (t) { return /^vectors nmi/.test(t); }) + 1, L.findIndex(function (t) { return /bytes free before the vectors/.test(t); }) + 1],
        right: '<div class="big">' + (s.to - s.from).toLocaleString() + ' <span>bytes free</span></div><div class="subcap">' + Math.round((s.to - s.from) / 16384 * 100) + "% of the 16 KB PRG bank is unused.</div>" };
    },
    vec: function (s) {
      var v = ["NMI", "RESET", "IRQ"], rows = "";
      for (var i = 0; i < 3; i++) {
        var o = s.from + i * 2, a = rom[o] | (rom[o + 1] << 8);
        rows += '<div class="hrow"><span class="by">' + hex(rom[o], 2) + " " + hex(rom[o + 1], 2) + '</span><span><b>' + v[i] + "</b> → $" + hex(a, 4) + " " + (R.sym[a] || "") + "</span></div>";
      }
      return { text: "The last six bytes of the CPU's address space. On power-on the 6502 jumps to RESET; every vblank the PPU fires NMI. Addresses are little-endian: the low byte first.",
        lines: [L.findIndex(function (t) { return /^vectors nmi/.test(t); }) + 1], right: '<div class="hrows">' + rows + "</div>" };
    },
    chr: function (s) {
      var used = 0, html = "";
      for (var t = 0; t < 512; t++) {
        var o = R.CHR + t * 16, any = false;
        for (var k = 0; k < 16; k++) if (rom[o + k]) { any = true; break; }
        if (any || t === 0) { used = t + 1; }
      }
      for (var t2 = 0; t2 < used; t2++) html += '<canvas data-t="' + t2 + '"></canvas>';
      return { text: used + " of 512 tiles used. Each is 16 bytes: two bit planes, so 4 colours. They are drawn in Dino.fsx as ASCII art and the tile number is the position in an F# list.",
        lines: [L.findIndex(function (t) { return /^let dinoBody/.test(t); }) + 1, L.findIndex(function (t) { return /chrBank chr/.test(t); }) + 1],
        right: '<div class="chrgrid">' + html + "</div>" };
    }
  };

  R.romMap = function (root) {
    var strip = "", scale = "";
    secs.forEach(function (s, i) {
      var n = s.to - s.from;
      strip += '<button class="seg ' + s.k + '" data-i="' + i + '" style="flex:' + FLEX[s.k] + '"><span class="nm">' + NAME[s.k] + '</span><span class="sz">' +
        n.toLocaleString() + " B</span>" + (s.k === "code" ? '<canvas class="heat" id="heatStrip" width="300" height="10"></canvas>' : "") + "</button>";
      scale += '<i class="' + s.k + '" style="flex:' + n + '"></i>';
    });
    root.innerHTML =
      '<div class="romhead"><span><b>dino.nes</b> · ' + rom.length.toLocaleString() + " bytes · iNES · mapper 0</span><span>click a section</span></div>" +
      '<div class="strip">' + strip + "</div>" +
      '<div class="toscale">' + scale + '</div><div class="toscale-cap">to scale</div>' +
      '<div class="rdetail"><div class="rl"><h3 id="rTitle"></h3><p id="rText"></p><div class="rlines" id="rLines"></div></div><div class="rr" id="rRight"></div></div>';

    function pick(i) {
      var s = secs[i], d = detail[s.k](s);
      root.querySelectorAll(".seg").forEach(function (b) { b.setAttribute("aria-pressed", +b.dataset.i === i); });
      root.querySelector("#rTitle").innerHTML = NAME[s.k] + ' <span>' + (s.to - s.from).toLocaleString() + " bytes · " + where(s) + "</span>";
      root.querySelector("#rText").innerHTML = d.text;
      var seen = {};
      root.querySelector("#rLines").innerHTML = '<div class="cap">put by</div>' + d.lines.filter(function (n) { if (seen[n] || !n) return false; return (seen[n] = 1); }).map(lineHtml).join("");
      var rr = root.querySelector("#rRight");
      rr.innerHTML = d.right;
      rr.querySelectorAll("canvas[data-t]").forEach(function (c) {
        var t = tile(+c.dataset.t, c.closest(".chrgrid") ? 3 : 2, gray); t.title = "tile " + c.dataset.t; c.replaceWith(t);
      });
      current = s;
    }
    var current = null;
    root.querySelector(".strip").addEventListener("click", function (e) { var b = e.target.closest(".seg"); if (b) pick(+b.dataset.i); });
    var codeSec = secs.filter(function (s) { return s.k === "code"; })[0];
    pick(secs.indexOf(codeSec));

    // live: bytes of the code section that ran this frame, decaying
    var from = R.addrOf(codeSec.from), to = R.addrOf(codeSec.to), heat = new Float32Array(to - from), fr = 0;
    var lens = new Uint8Array(to - from);
    for (var o2 = codeSec.from; o2 < codeSec.to;) { var d = R.decode(o2); lens[o2 - codeSec.from] = d.len; o2 += d.len; }
    var hot = getComputedStyle(document.documentElement).getPropertyValue("--hot").trim() || "#ff6a3d";
    function paint(c, rows) {
      if (!c) return;
      var g = c.getContext("2d"), W = c.width, H = c.height, per = Math.ceil(heat.length / rows), cw = W / per, rh = H / rows;
      g.clearRect(0, 0, W, H);
      g.fillStyle = "rgba(127,127,127,.18)"; g.fillRect(0, 0, W, H);
      for (var i = 0; i < heat.length; i++) {
        var h = heat[i]; if (h < 0.02) continue;
        g.globalAlpha = Math.min(1, h); g.fillStyle = hot;
        g.fillRect((i % per) * cw, Math.floor(i / per) * rh, Math.max(1, cw), rh);
      }
      g.globalAlpha = 1;
    }
    return function (cyc) {
      var used = 0, idle = 0;
      for (var i = 0; i < heat.length; i++) heat[i] *= 0.7;
      for (var a = from; a < to; a++) if (cyc[a]) {
        var n = lens[a - from] || 1;
        for (var k = 0; k < n; k++) heat[a - from + k] = 1;
      }
      if (++fr % 2) return;
      paint(document.getElementById("heatStrip"), 1);
      if (current && current.k === "code") {
        paint(document.getElementById("heatBig"), 4);
        var ran = 0; for (var j = 0; j < heat.length; j++) if (heat[j] > 0.99) ran++;
        var el = document.getElementById("cpuPct"); if (el) el.textContent = ran + " of " + heat.length + " code bytes ran this frame (4 rows, start to end)";
      }
    };
  };
})();
