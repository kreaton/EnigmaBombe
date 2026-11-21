using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BombeSim;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Commands.Usage();
    return 0;
}

try
{
    var a = new Args(args, 1);
    switch (args[0].ToLowerInvariant())
    {
        case "run": return Commands.Run(a);
        case "selftest": return Commands.SelfTest(a);
        case "bench": return Commands.Bench(a);
        case "encrypt":
        case "decrypt": return Commands.Encrypt(a);   // Enigma is reciprocal: the same operation both ways
        default:
            Console.Error.WriteLine($"Unknown command '{args[0]}'. Try: bombe help");
            return 2;
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}

sealed class Args
{
    readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] argv, int start)
    {
        for (int i = start; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) throw new ArgumentException($"Unexpected argument '{argv[i]}'.");
            string key = argv[i][2..];
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) values[key] = argv[++i];
            else flags.Add(key);
        }
        if (values.TryGetValue("config", out var path)) LoadConfig(path);
    }

    /// <summary>
    /// Reads a JSON object whose keys are option names without "--" (e.g. "rotors", "pos", "scan-middle-step").
    /// Strings and numbers become values, arrays are joined with commas, true becomes a flag, false/null are ignored.
    /// Options given on the command line win over the file.
    /// </summary>
    void LoadConfig(string path)
    {
        if (!File.Exists(path)) throw new ArgumentException($"Config file '{path}' not found.");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Config file '{path}' is not valid JSON: {ex.Message}");
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"Config file '{path}' must contain a JSON object.");
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                string key = p.Name;
                if (key.Equals("config", StringComparison.OrdinalIgnoreCase) || values.ContainsKey(key) || flags.Contains(key)) continue;
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.String: values[key] = p.Value.GetString()!; break;
                    case JsonValueKind.Number: values[key] = p.Value.GetRawText(); break;
                    case JsonValueKind.True: flags.Add(key); break;
                    case JsonValueKind.False:
                    case JsonValueKind.Null: break;
                    case JsonValueKind.Array:
                        values[key] = string.Join(",", p.Value.EnumerateArray()
                            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()));
                        break;
                    default: throw new ArgumentException($"Config file '{path}': unsupported value for \"{key}\".");
                }
            }
        }
    }

    public bool Has(string key) => values.ContainsKey(key) || flags.Contains(key);
    public string Str(string key, string? def = null) =>
        values.TryGetValue(key, out var v) ? v : def ?? throw new ArgumentException($"Missing --{key}.");
    public int Int(string key, int def) =>
        values.TryGetValue(key, out var v)
            ? (int.TryParse(v, out int n) ? n : throw new ArgumentException($"--{key} must be a number."))
            : def;
}

static class Commands
{
    public static void Usage() => Console.WriteLine("""
        bombe - a software Turing/Welchman Bombe for 3-rotor Army/Luftwaffe Enigma

        COMMANDS
          run       Search for the key given a ciphertext and a crib (guessed plaintext)
          selftest  Generate random Enigma traffic, run the Bombe on it, check the true key is found
          bench     Time the search (1 rotor order, 60 orders, 1 thread vs all threads) vs the 1940s machine
          encrypt   Reference Enigma, handy for making your own test messages
          decrypt   Same as encrypt (Enigma is reciprocal)

        common options
          --config FILE        JSON file with options, e.g. {"rotors": "2,4,1", "rings": "BCD", "pos": "QMX"}
                               (keys = option names without "--"; the command line wins over the file)

        run options
          --cipher TEXT        ciphertext (letters only; spaces etc. ignored)
          --in FILE            read the ciphertext from a file instead (or pipe it in)
          --crib TEXT          suspected plaintext, 1-26 letters
          --align N            0-based position of the crib in the ciphertext (default: try every legal one)
          --reflector B|C      default B
          --rotors 1,2,3,4,5   rotors available, default all five (60 orders); use e.g. 1,2,3 for 6 orders
          --order 3,1,2        search exactly one order (left,middle,right)
          --middle-step K      middle rotor steps once just before crib letter K (1..len-1); default: it does not
          --scan-middle-step   try "no step" and every K - needed when the crib may span a turnover
          --min-loops N        skip alignments whose best menu component has fewer loops (default 3)
          --max-survivors N    a setting is rejected if more than N of 26 hypotheses survive (default 3)
          --max-plugs N        reject stops needing more than N cables (default 10)
          --threads N          default: all cores
          --max-print N        stops to print (default 25)
          --force              run even for weak menus (expect a flood of false stops)

        encrypt / decrypt options
          --rotors 2,1,3 --reflector B --rings AAA --pos AAA --plugs "AB CD EF"
          --text TEXT          text to process; or --in FILE; or pipe it in
          --out FILE           write the result to a file instead of stdout

        EXAMPLE
          dotnet run -c Release -- selftest
          dotnet run -c Release -- run --cipher "<ciphertext>" --crib KEINEBESONDERENEREIGNISSE --scan-middle-step
          Get-Content plain.txt | bombe encrypt --config key.json | bombe decrypt --config key.json
        """);

    // ---------------------------------------------------------------- run
    public static int Run(Args a)
    {
        string cipher = Util.Clean(ReadInput(a, "cipher"));
        string crib = Util.Clean(a.Str("crib"));
        if (crib.Length == 0 || crib.Length > Menu.MaxCrib) throw new ArgumentException($"Crib must be 1..{Menu.MaxCrib} letters.");
        if (cipher.Length < crib.Length) throw new ArgumentException("Ciphertext is shorter than the crib.");

        var opts = ParseOptions(a);
        int minLoops = a.Int("min-loops", 3);
        bool force = a.Has("force");
        int maxPrint = a.Int("max-print", 25);

        var aligns = a.Has("align")
            ? new List<int> { a.Int("align", 0) }
            : Enumerable.Range(0, cipher.Length - crib.Length + 1).Where(x => Menu.LegalAlignment(cipher, crib, x)).ToList();
        if (a.Has("align") && !Menu.LegalAlignment(cipher, crib, aligns[0]))
            throw new ArgumentException("That alignment is impossible: a crib letter equals the cipher letter above it (Enigma never maps a letter to itself).");

        Console.WriteLine($"Cipher {cipher.Length} letters, crib {crib.Length} letters, {aligns.Count} legal alignment(s), reflector {opts.Reflector}, " +
                          $"{BombeSearch.Orders(opts).Count} rotor order(s), {opts.Threads} thread(s).");

        var all = new List<Stop>();
        int skipped = 0;
        var sw = Stopwatch.StartNew();
        foreach (int al in aligns)
        {
            var menu = Menu.Build(crib, cipher.Substring(al, crib.Length));
            var p = menu.Primary;
            if (!force && p.Loops < minLoops) { skipped++; continue; }
            if (p.Loops < 1 && !force) { skipped++; continue; }

            var ks = a.Has("scan-middle-step") ? Enumerable.Range(0, crib.Length).ToList() : new List<int> { opts.MiddleStep };
            var t0 = sw.Elapsed;
            int found = 0;
            long settings = 0;
            foreach (int k in ks)
            {
                var o = opts with { MiddleStep = k };
                var stops = BombeSearch.Run(menu, o, al, out long tested);
                settings += tested;
                found += stops.Count;
                all.AddRange(stops);
            }
            Console.WriteLine($"  align {al,4}: menu {p.Nodes.Length} letters / {p.Loops} loops, " +
                              $"middle-step {(ks.Count > 1 ? "scan 0.." + (ks.Count - 1) : ks[0].ToString())}: " +
                              $"{found} stop(s)  [{(sw.Elapsed - t0).TotalSeconds:F2}s, {settings:N0} settings]");
        }
        sw.Stop();

        if (skipped > 0)
            Console.WriteLine($"Skipped {skipped} alignment(s) whose menu has fewer than {minLoops} loops (see --min-loops / --force).");
        Console.WriteLine($"Total: {all.Count} stop(s) in {sw.Elapsed.TotalSeconds:F2}s.");
        if (all.Count == 0)
        {
            Console.WriteLine("No stops. The crib may be wrong, misplaced, or the model's assumptions are violated (see README: limitations).");
            return 1;
        }

        for (int i = 0; i < Math.Min(maxPrint, all.Count); i++) PrintStop(all[i], i + 1, cipher, crib, opts.Reflector);
        if (all.Count > maxPrint) Console.WriteLine($"... {all.Count - maxPrint} more stop(s) not shown (--max-print).");
        return 0;
    }

    static void PrintStop(Stop s, int index, string cipher, string crib, char reflector)
    {
        Console.WriteLine();
        Console.WriteLine($"STOP {index}: rotors {s.OrderName} (left-middle-right), core positions at crib start {s.CorePos}, " +
                          $"crib at index {s.Alignment}, middle-step {s.K}");
        Console.WriteLine($"  plugboard deduced ({s.PlugCount} pair(s)): {s.PlugText()}");
        string plain = BombeSearch.Decrypt(cipher, s, reflector);
        int from = Math.Max(0, s.Alignment - 40), to = Math.Min(plain.Length, s.Alignment + crib.Length + 40);
        var sb = new StringBuilder();
        if (from > 0) sb.Append("...");
        sb.Append(plain, from, s.Alignment - from);
        sb.Append('[').Append(plain, s.Alignment, crib.Length).Append(']');
        sb.Append(plain, s.Alignment + crib.Length, to - s.Alignment - crib.Length);
        if (to < plain.Length) sb.Append("...");
        Console.WriteLine($"  preview: {sb}   (lower case = depends on an undetermined plug letter)");
    }

    static SearchOptions ParseOptions(Args a)
    {
        var o = new SearchOptions
        {
            Reflector = a.Str("reflector", "B")[0],
            MiddleStep = a.Int("middle-step", 0),
            MaxSurvivors = a.Int("max-survivors", 3),
            MaxPlugs = a.Int("max-plugs", 10),
            Threads = a.Int("threads", Environment.ProcessorCount),
        };
        if (a.Has("rotors")) o = o with { Rotors = ParseRotors(a.Str("rotors"), 1, 5) };
        if (a.Has("order")) o = o with { FixedOrder = ParseRotors(a.Str("order"), 3, 3) };
        Wiring.Reflector(o.Reflector);   // validates
        if (o.MaxSurvivors < 1 || o.MaxSurvivors > 26) throw new ArgumentException("--max-survivors must be 1..26.");
        return o;
    }

    static int[] ParseRotors(string s, int min, int max)
    {
        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var r = new List<int>();
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out int n) || n < 1 || n > 5) throw new ArgumentException("Rotors are numbers 1..5.");
            r.Add(n - 1);
        }
        if (r.Distinct().Count() != r.Count) throw new ArgumentException("Rotors must be distinct.");
        if (r.Count < min || r.Count > max) throw new ArgumentException($"Expected {min}..{max} rotors.");
        if (min == 1 && r.Count < 3) throw new ArgumentException("--rotors needs at least 3 distinct rotors.");
        return r.ToArray();
    }

    // ---------------------------------------------------------------- selftest
    public static int SelfTest(Args a)
    {
        int trials = a.Int("trials", 5);
        int seed = a.Int("seed", 1);
        string crib = a.Str("crib", "KEINEBESONDERENEREIGNISSE");
        var rng = new Random(seed);
        int threads = a.Int("threads", Environment.ProcessorCount);
        bool scan = a.Has("scan-middle-step");
        int pass = 0;

        for (int t = 1; t <= trials; t++)
        {
            var c = TestCase.Generate(rng, crib);
            Console.WriteLine($"Trial {t}: {c.KeyText}");
            Console.WriteLine($"  true core positions at crib start {(char)('A' + c.L)}{(char)('A' + c.M)}{(char)('A' + c.R)}, " +
                              $"crib at index {c.Align}, middle steps inside crib at {c.K}; " +
                              $"menu {c.Menu.Primary.Nodes.Length} letters / {c.Menu.Primary.Loops} loops (+{c.Menu.Components.Count - 1} other component(s))");

            var sw = Stopwatch.StartNew();
            var stops = new List<Stop>();
            long tested = 0;
            var ks = scan ? Enumerable.Range(0, crib.Length).ToList() : new List<int> { c.K };
            foreach (int k in ks)
            {
                var o = new SearchOptions { Reflector = 'B', MiddleStep = k, Threads = threads };
                stops.AddRange(BombeSearch.Run(c.Menu, o, c.Align, out long n));
                tested += n;
            }
            sw.Stop();

            var truth = stops.FirstOrDefault(s => c.IsTruth(s) && s.K == c.K);
            int falseStops = stops.Count - (truth != null ? 1 : 0);
            bool ok = truth != null;
            if (ok) pass++;
            Console.WriteLine($"  {(ok ? "FOUND" : "MISSED")} true key among {stops.Count} stop(s) ({falseStops} false) " +
                              $"after {tested:N0} settings in {sw.Elapsed.TotalSeconds:F2}s");
            if (ok)
            {
                Console.WriteLine($"  deduced plugboard: {truth!.PlugText()}");
                string plain = BombeSearch.Decrypt(c.Cipher, truth, 'B');
                int from = c.Align, to = c.Align + crib.Length;
                Console.WriteLine($"  crib region decrypts to: {plain[from..to]}  (matches crib: {string.Equals(plain[from..to], crib, StringComparison.OrdinalIgnoreCase)})");
            }
        }
        Console.WriteLine($"\n{pass}/{trials} trials recovered the true key.");
        return pass == trials ? 0 : 1;
    }

    // ---------------------------------------------------------------- bench
    public static int Bench(Args a)
    {
        int seed = a.Int("seed", 7);
        int threads = a.Int("threads", Environment.ProcessorCount);
        var c = TestCase.Generate(new Random(seed), "KEINEBESONDERENEREIGNISSE");
        var p = c.Menu.Primary;
        Console.WriteLine($"Benchmark case: {c.KeyText}");
        Console.WriteLine($"Menu: {c.Crib.Length}-letter crib, primary component {p.Nodes.Length} letters / {p.Loops} loops.");
        Console.WriteLine($"Runtime: {Environment.Version}, {Environment.ProcessorCount} logical core(s), {threads} thread(s) in parallel runs.\n");

        var one = new SearchOptions { FixedOrder = c.Order, MiddleStep = c.K, Threads = 1 };
        var all1 = new SearchOptions { MiddleStep = c.K, Threads = 1 };
        var allN = new SearchOptions { MiddleStep = c.K, Threads = threads };

        BombeSearch.Run(c.Menu, one, c.Align, out _);     // JIT warm-up

        double Best(SearchOptions o, int reps, out long settings, out int stops)
        {
            double best = double.MaxValue;
            settings = 0; stops = 0;
            for (int i = 0; i < reps; i++)
            {
                var sw = Stopwatch.StartNew();
                var r = BombeSearch.Run(c.Menu, o, c.Align, out settings);
                sw.Stop();
                stops = r.Count;
                best = Math.Min(best, sw.Elapsed.TotalSeconds);
            }
            return best;
        }

        const double BombeOrderSeconds = 26.0 * 26.0 / 65.0 * 60.0;    // 10.4 min per rotor order, no stops
        double tOne = Best(one, 5, out long sOne, out int stOne);
        double tAll1 = Best(all1, 2, out long sAll1, out int stAll1);
        double tAllN = Best(allN, 3, out long sAllN, out int stAllN);

        Console.WriteLine($"{"run",-34}{"settings",12}{"time",12}{"settings/s",14}{"stops",7}{"vs one Bombe",16}");
        Row("1 rotor order, 1 thread", sOne, tOne, stOne, BombeOrderSeconds);
        Row("60 rotor orders, 1 thread", sAll1, tAll1, stAll1, BombeOrderSeconds * 60);
        Row($"60 rotor orders, {threads} threads", sAllN, tAllN, stAllN, BombeOrderSeconds * 60);
        Console.WriteLine();
        Console.WriteLine("1940s reference (TNMOC, 3-wheel Bombe at 65 carries/min): 26x26/65 = 10.4 min per rotor order, i.e. about 28 drum");
        Console.WriteLine("positions per second per machine (excluding stops and the up-to-30-minute set-up), so ~10.4 hours for all 60 orders.");
        return 0;

        static void Row(string label, long settings, double sec, int stops, double bombeSec)
        {
            Console.WriteLine($"{label,-34}{settings,12:N0}{Fmt(sec),12}{settings / sec,14:N0}{stops,7}{bombeSec / sec,15:N0}x");
        }
    }

    static string Fmt(double s) => s < 1 ? $"{s * 1000:F1} ms" : $"{s:F2} s";

    // ---------------------------------------------------------------- encrypt
    public static int Encrypt(Args a)
    {
        var rotors = ParseRotors(a.Str("rotors", "1,2,3"), 3, 3);
        string rings = Util.Clean(a.Str("rings", "AAA"));
        string pos = Util.Clean(a.Str("pos", "AAA"));
        if (rings.Length != 3 || pos.Length != 3) throw new ArgumentException("--rings and --pos need three letters.");
        var e = new Enigma(rotors, a.Str("reflector", "B")[0],
            rings.Select(ch => ch - 'A').ToArray(), pos.Select(ch => ch - 'A').ToArray(), a.Str("plugs", ""));
        string result = e.Process(ReadInput(a, "text"));
        if (a.Has("out")) File.WriteAllText(a.Str("out"), result + Environment.NewLine);
        else Console.WriteLine(result);
        return 0;
    }

    /// <summary>Text from --KEY, else from the file in --in, else from stdin when it is redirected (a pipe or "&lt; file").</summary>
    static string ReadInput(Args a, string key)
    {
        string text;
        if (a.Has(key)) text = a.Str(key);
        else if (a.Has("in"))
        {
            string path = a.Str("in");
            if (!File.Exists(path)) throw new ArgumentException($"Input file '{path}' not found.");
            text = File.ReadAllText(path);
        }
        else if (Console.IsInputRedirected) text = Console.In.ReadToEnd();
        else throw new ArgumentException($"Missing --{key} (or --in FILE, or pipe the text in).");

        if (Util.Clean(text).Length == 0) throw new ArgumentException($"No letters A-Z in the input for --{key}.");
        return text;
    }
}
