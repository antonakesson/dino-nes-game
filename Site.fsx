// Site.fsx — docs/data.js for the web page: the ROM, its symbols, the sources, and which
// line of Dino.fsx / Std.fsx put each byte.
//
//   NES_TRACE=1 dotnet fsi --tailcalls- Dino.fsx   (writes output/dino.nes.trace too)
//   dotnet fsi Site.fsx

open System
open System.IO
open System.Text.Json

let dir = __SOURCE_DIRECTORY__
let rom = Path.Combine(dir, "output", "dino.nes")
if not (File.Exists(rom + ".trace")) then failwith "no output/dino.nes.trace: build with NES_TRACE=1 dotnet fsi --tailcalls- Dino.fsx first"

/// "Dino.fsx:256;Std.fsx:374" -> the Dino.fsx lines (innermost first), the innermost Std.fsx line
let entry (line: string) =
    let parts = line.Split(' ', 3)
    let frames =
        if parts.Length < 3 then [||]
        else parts.[2].Split(';') |> Array.map (fun f -> let i = f.LastIndexOf ':' in f.[.. i - 1], int f.[i + 1 ..])
    let lines file = frames |> Array.filter (fst >> (=) file) |> Array.map snd
    box [| box (int parts.[0]); box (int parts.[1]); box (lines "Dino.fsx")
           (match lines "Std.fsx" with [||] -> null | l -> box l.[0]) |]

/// "$C000#reset#" -> [0xC000, "reset"]
let symbols path =
    File.ReadAllLines path
    |> Array.map (fun l -> let p = l.Split('#') in box [| box (Convert.ToInt32(p.[0].TrimStart('$'), 16)); box p.[1] |])

let data =
    dict [
        "rom", box (Convert.ToBase64String(File.ReadAllBytes rom))
        "entries", box (File.ReadAllLines(rom + ".trace") |> Array.map entry)
        "code", box (symbols (rom + ".0.nl"))
        "ram", box (symbols (rom + ".ram.nl"))
        "src", box (dict [ for f in [ "Dino.fsx"; "Std.fsx" ] -> f, File.ReadAllText(Path.Combine(dir, f)).Replace("\r\n", "\n") ]) ]

let out = Path.Combine(dir, "docs", "data.js")
File.WriteAllText(out, "window.DINO = " + JsonSerializer.Serialize data + ";\n")
printfn "wrote %s (%d bytes)" out (FileInfo(out).Length)
