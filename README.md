# Bombe – a software Turing/Welchman Bombe in C# (.NET 10)

A standalone console program that does what the Bletchley Park Bombe did: given a **crib** (guessed plaintext
for part of an intercepted message) it searches all rotor orders and rotor positions of a 3-rotor Army/Luftwaffe
Enigma and reports the settings that are logically consistent with the crib, together with the plugboard pairs
it could deduce on the way.

It does **not** need the daily key or a codebook – the key is what it finds.

Files: `Program.cs` (CLI), `BombeEngine.cs` (menu + search), `Enigma.cs` (wiring + full reference Enigma),
`TestCase.cs` (synthetic traffic for the self-test), `Bombe.csproj`, `example-key.json` (key file for the worked
example), `Test-Bombe.ps1` (runs the README commands and checks the results).

## Build and run

Requires the .NET 10 SDK.

```
dotnet build -c Release
dotnet run -c Release -- help
dotnet run -c Release -- selftest          # generates random traffic, checks the true key is recovered
dotnet run -c Release -- bench             # timings, incl. comparison with the 1940s machine
```

### Worked example

Make a test message with the reference Enigma (rotors II-IV-I, rings BCD, start QMX, 10 plugboard pairs):

```
dotnet run -c Release -- encrypt --rotors 2,4,1 --reflector B --rings BCD --pos QMX \
    --plugs "AB CD EF GH IJ KL MN OP QR ST" \
    --text HIERFUNKSTELLEPAULAEMILRICHARDXMELDEGEHORSAMSTXANXOBERKOMMANDOXKEINEBESONDERENEREIGNISSEXWETTERLAGEUNVERAENDERT
```
```
FDGABFZOYYSCVJWCAJXDSWDPFBOHGLBUCMIMBFDQULDHJIQORRFFDSNWZZLDOIIXMDGQNHLTPUWUADCCOHSIJFRMGIBWLTYUVWMHDKDWZVPJLFJ
```

Now pretend you only know the ciphertext and guess that it contains `KEINEBESONDERENEREIGNISSE`:

```
dotnet run -c Release -- run --cipher FDGABFZOYYSCVJWCAJXDSWDPFBOHGLBUCMIMBFDQULDHJIQORRFFDSNWZZLDOIIXMDGQNHLTPUWUADCCOHSIJFRMGIBWLTYUVWMHDKDWZVPJLFJ \
    --crib KEINEBESONDERENEREIGNISSE --scan-middle-step
```

This tries every legal crib position, all 60 rotor orders and every possible middle-rotor turnover
(a few minutes on 2 cores; add `--rotors 1,2,3,4` or `--align 63` to narrow it down). Result:

```
STOP 1: rotors II-IV-I (left-middle-right), core positions at crib start PMG, crib at index 63, middle-step 8
  plugboard deduced (10 pair(s)): AB CD EF GH IJ KL MN OP QR ST   unplugged: UWX   undetermined: VYZ
  preview: ...IOFLyRRJRQLEQOCKyIDvEzTXANXOBERKOmmANDOX[KEINEBESONDERENEREIGNISSE]XWETTErLaLJyOIJMjzXXNvI
```

### Pipes, files and a key file

Text can come from `--text` (`--cipher` for `run`), from a file with `--in FILE`, or from a pipe. `encrypt` writes to
stdout, or to a file with `--out FILE`. `decrypt` is the same operation as `encrypt` (Enigma is reciprocal), so
it is just there to make scripts read better.

The settings can live in a JSON file passed with `--config`. Its keys are the option names without `--`; arrays are
joined with commas, `true` turns a flag on, and `//` comments are allowed. Options typed on the command line win
over the file. [`example-key.json`](example-key.json) holds the key of the worked example:

```json
{
  "rotors": [2, 4, 1],
  "reflector": "B",
  "rings": "BCD",
  "pos": "QMX",
  "plugs": "AB CD EF GH IJ KL MN OP QR ST"
}
```

PowerShell examples (`bombe` = `bin\Release\net10.0\bombe.exe`):

```powershell
# text > encrypt > text
"HIER FUNKSTELLE PAULA-EMIL-RICHARD" | bombe encrypt --config example-key.json

# file > encrypt > file, then back again
bombe encrypt --config example-key.json --in plain.txt --out cipher.txt
bombe decrypt --config example-key.json --in cipher.txt

# round trip in one pipeline
Get-Content plain.txt | bombe encrypt --config example-key.json | bombe decrypt --config example-key.json

# same key, other start position
"WETTERBERICHT" | bombe encrypt --config example-key.json --pos AAA

# the Bombe can read the intercept from a file or pipe as well
Get-Content cipher.txt | bombe run --crib KEINEBESONDERENEREIGNISSE --order 2,4,1 --scan-middle-step
```

Spaces, punctuation and line breaks in the input are dropped, and the output is upper case A–Z only (as on the
real machine).

### `run` options

| Option | Meaning |
|---|---|
| `--cipher TEXT` | Ciphertext (A–Z; anything else is ignored) |
| `--in FILE` | Read the ciphertext from a file. Without `--cipher` or `--in`, it is read from a pipe |
| `--config FILE` | JSON file with any of these options (see above) |
| `--crib TEXT` | Suspected plaintext, 1–26 letters |
| `--align N` | 0-based position of the crib in the ciphertext. Default: every legal position |
| `--reflector B\|C` | Default `B` |
| `--rotors 1,2,3,4,5` | Rotors available (default all five → 60 orders). `1,2,3` → 6 orders |
| `--order 3,1,2` | Test exactly one order (left,middle,right) |
| `--middle-step K` | The middle rotor steps once, just before crib letter K (1…len-1). Default: it does not move |
| `--scan-middle-step` | Try "no step" and every K. Use this unless you know the crib does not cross a turnover |
| `--min-loops N` | Skip crib positions whose menu has fewer than N loops (default 3) |
| `--max-survivors N` | Reject a setting if more than N of the 26 hypotheses survive (default 3) |
| `--max-plugs N` | Reject stops that already need more than N cables (default 10) |
| `--threads N` | Default: all cores |
| `--max-print N` | Stops to print (default 25) |
| `--force` | Run weak menus too (expect a flood of false stops) |

## How it works

1. **Alignment.** Enigma never maps a letter to itself, so crib positions where a crib letter equals the cipher
   letter above it are impossible and skipped.
2. **Menu.** Letters are nodes; crib position *i* adds an edge between `plain[i]` and `cipher[i]`.
   Because Enigma is reciprocal, each edge says `S(c_i) = P_i(S(p_i))`, where `S` is the unknown plugboard and `P_i` the
   rotor/reflector permutation at step *i*. Loops in the menu are what give the method its power.
3. **Test per setting.** For each rotor order and each of the 26³ core positions: hypothesise a plugboard partner
   for one letter (all 26 are tried), propagate through the menu, and insist that the plugboard stays a symmetric
   mapping (Welchman's *diagonal board*). A contradiction rejects the hypothesis. If some hypothesis survives – a
   **stop** – the setting is kept. Other menu components that contain loops must also be satisfiable.
4. **Speed trick.** Everything left of the right rotor plus the reflector is the same for all right-rotor positions, so
   it is folded into 26 precomputed tables per (order, left, middle) – each `P_i` is then a single table lookup.

Ring settings play no role in the search: the Bombe finds **core positions** (window position − ring setting).

## Reading the output

* **Core positions at crib start** = window letters − ring settings, *after* the key press for the first crib letter.
* **Middle-step K.** If the middle rotor turned over inside the crib, K tells you where. It also tells you the right ring
  setting: `ring_right ≡ notch_right − (R + K − 1) (mod 26)` where `R` is the right core position shown. Example above:
  rotor I (notch Q=16), R=G=6, K=8 gives ring D – correct. Two adjacent K values can both stop for the same setting:
  that happens when the crib edge at the turnover is not part of any loop and so does not constrain the answer.
* **Plugboard.** Only letters connected to a loop are deduced. Others are shown as *undetermined*; a real operator
  would fix them by reading the decrypt. This program does not do that automatically.
* **Preview.** The text is decrypted with the stop's model. Lower-case = depends on an undetermined plug letter.
  Only the stretch of text around the crib is reliable (see limitation 3), and the rest of the message needs the real ring
  settings to decrypt.
* Ring settings, and the left/middle ring settings in particular, are **not** recovered.

## Limitations

1. **A crib is required**, and it must be correct and correctly placed (or all alignments must be tried). One wrong letter
   means no stop.
2. **Crib length 1–26 letters** (so the right rotor passes its notch at most once).
3. **Rotor movement is modelled in a simplified way, like the original Bombe:** only the right rotor moves, except for
   one optional middle-rotor step (`--middle-step`). Not modelled: a *double step* inside the crib (the middle rotor
   stepping twice, taking the left rotor with it) – a crib that triggers it is missed (a few percent of random cribs).
   Text far from the crib, beyond the next turnover, will not decrypt with the stop's model.
4. **Machine scope:** 3-rotor Enigma I with rotors I–V and reflectors B and C. No 4-rotor Naval M4, no rotors VI–VIII,
   no Uhr, no UKW-D, no rotor-wiring variants.
5. **Menu quality matters.** Weak menus (few loops) give huge numbers of false stops, so alignments with fewer than
   `--min-loops` loops are skipped by default. A setting where more than `--max-survivors` hypotheses survive is rejected,
   which in rare cases can hide the true key.
6. **Stop rule differs slightly from the machine.** The real Bombe let current flow and stopped when the test register
   was not fully live. This program does the same logic one hypothesis at a time and stops when at most
   `--max-survivors` hypotheses are consistent. Same idea, stricter test.
7. **Not an automatic solver.** No scoring of plaintext, no hill climbing to finish the plugboard, no checking machine.
   False stops are listed for you to judge.
8. The self-test checks the model against a separate full Enigma implementation (rings, double-stepping, plugboard);
   it draws keys that fit limitation 3, so it can't catch cribs the model would miss.

## Timings vs the 1940s machine

Measured with `bench` (rotors IV-V-I, 25-letter crib, a 19-letter / 5-loop menu, middle-step known) on a 2-vCPU
Intel Xeon @ 2.1 GHz cloud VM, .NET 10:

| Run | Settings | Time | Settings/s | vs one Bombe |
|---|---:|---:|---:|---:|
| 1 rotor order, 1 thread | 17,576 | 26.7 ms | 657,000 | ~23,000× |
| 60 rotor orders, 1 thread | 1,054,560 | 1.58 s | 667,000 | ~24,000× |
| 60 rotor orders, 2 threads | 1,054,560 | 0.82 s | 1,286,000 | ~46,000× |

With `--scan-middle-step` (25 possible turnover points) the cost is 25× more per crib position – about 18 s for all 60
orders on this VM.

**The original:** a 3-wheel Bombe was designed for 65 carries per minute, so a full uninterrupted run through all
17,576 drum positions of one rotor order took 26×26/65 = **10.4 minutes**. That is about 28 positions per second, and
setting up (menu plugging, loading drums) could take up to 30 minutes. All 60 orders on a single machine would take
about 10.4 hours before counting stops. The Bombe tested all 26 plugboard hypotheses at once electrically; this program
tries them one after another and is still tens of thousands of times faster. Many machines were built (TNMOC mentions 73 early three-wheel ones) and ran different jobs in
parallel, so practical wall-clock time was shorter than for a single machine.

Sources: TNMOC Bombe pages by John Jackson – <https://www.tnmoc.org/bh-11-bombe-speed> and
<https://www.tnmoc.org/bh-9-bombe-rebuild-machine>.
