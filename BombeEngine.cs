using System.Collections.Concurrent;
using System.Text;

namespace BombeSim;

/// <summary>A connected part of the crib graph (letters are nodes, crib positions are edges).</summary>
public sealed class Component
{
    public int[] Nodes = Array.Empty<int>();
    public int Edges;
    public int Loops;       // independent cycles = edges - nodes + 1
    public int TestNode;    // node the 26 plugboard hypotheses are made on
}

/// <summary>
/// The "menu": for crib position i, plain letter p[i] and cipher letter c[i] are joined by an edge labelled i.
/// Because Enigma is reciprocal, the edge says  S(c[i]) = P_i(S(p[i]))  where S is the (unknown) plugboard
/// and P_i the rotor/reflector permutation at step i.
/// </summary>
public sealed class Menu
{
    public const int MaxCrib = 26;

    public int Length;
    public int[] P = Array.Empty<int>();
    public int[] C = Array.Empty<int>();
    public int[] AdjStart = new int[27];
    public int[] AdjTo = Array.Empty<int>();
    public int[] AdjStep = Array.Empty<int>();
    public List<Component> Components = new();   // most loops first
    public Component Primary => Components[0];

    /// <summary>Enigma never encrypts a letter to itself, so an alignment with a self-match is impossible.</summary>
    public static bool LegalAlignment(string cipher, string crib, int a)
    {
        if (a < 0 || a + crib.Length > cipher.Length) return false;
        for (int j = 0; j < crib.Length; j++)
            if (cipher[a + j] == crib[j]) return false;
        return true;
    }

    public static Menu Build(string crib, string cipherSegment)
    {
        int n = crib.Length;
        if (n == 0 || n > MaxCrib) throw new ArgumentException($"Crib must be 1..{MaxCrib} letters.");
        if (cipherSegment.Length != n) throw new ArgumentException("Cipher segment and crib must have equal length.");

        var m = new Menu { Length = n, P = new int[n], C = new int[n] };
        var deg = new int[26];
        for (int i = 0; i < n; i++)
        {
            m.P[i] = crib[i] - 'A';
            m.C[i] = cipherSegment[i] - 'A';
            deg[m.P[i]]++;
            deg[m.C[i]]++;
        }

        for (int v = 0; v < 26; v++) m.AdjStart[v + 1] = m.AdjStart[v] + deg[v];
        m.AdjTo = new int[2 * n];
        m.AdjStep = new int[2 * n];
        var fill = (int[])m.AdjStart.Clone();
        for (int i = 0; i < n; i++)
        {
            int a = m.P[i], b = m.C[i];
            m.AdjTo[fill[a]] = b; m.AdjStep[fill[a]++] = i;
            m.AdjTo[fill[b]] = a; m.AdjStep[fill[b]++] = i;
        }

        // connected components via union-find
        var parent = Enumerable.Range(0, 26).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < n; i++) parent[Find(m.P[i])] = Find(m.C[i]);

        var groups = new Dictionary<int, List<int>>();
        for (int v = 0; v < 26; v++)
        {
            if (deg[v] == 0) continue;
            int r = Find(v);
            if (!groups.TryGetValue(r, out var list)) groups[r] = list = new List<int>();
            list.Add(v);
        }
        var edgeCount = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(m.P[i]);
            edgeCount[r] = edgeCount.GetValueOrDefault(r) + 1;
        }
        foreach (var (r, nodes) in groups)
        {
            int best = nodes[0];
            foreach (int v in nodes) if (deg[v] > deg[best]) best = v;
            m.Components.Add(new Component
            {
                Nodes = nodes.ToArray(),
                Edges = edgeCount[r],
                Loops = edgeCount[r] - nodes.Count + 1,
                TestNode = best,
            });
        }
        m.Components.Sort((x, y) =>
        {
            int c = y.Loops.CompareTo(x.Loops);
            return c != 0 ? c : y.Nodes.Length.CompareTo(x.Nodes.Length);
        });
        return m;
    }
}

public sealed record SearchOptions
{
    /// <summary>Rotor indices (0..4 = I..V) available for the left/middle/right slots.</summary>
    public int[] Rotors { get; init; } = { 0, 1, 2, 3, 4 };
    /// <summary>If set: [left, middle, right] rotor indices; only this order is searched.</summary>
    public int[]? FixedOrder { get; init; }
    public char Reflector { get; init; } = 'B';
    /// <summary>0 = middle rotor does not move during the crib; k>=1 = it steps once, just before crib letter k.</summary>
    public int MiddleStep { get; init; }
    /// <summary>Reject a setting if more than this many of the 26 hypotheses on the test letter survive.</summary>
    public int MaxSurvivors { get; init; } = 3;
    /// <summary>Reject stops whose deduced plugboard already needs more than this many cables.</summary>
    public int MaxPlugs { get; init; } = 10;
    public int Threads { get; init; } = Environment.ProcessorCount;
}

/// <summary>A candidate solution: rotor order, core positions at the first crib letter, and deduced plugboard.</summary>
public sealed class Stop
{
    public int[] Order = Array.Empty<int>();   // left, middle, right
    public int L, M, R;                         // rotor core positions at crib letter 0 (after its step)
    public int K;                               // middle-step index used (0 = none)
    public int Alignment;                       // crib start index in the ciphertext
    public sbyte[] Plug = new sbyte[26];        // deduced plugboard, -1 = unknown

    public string OrderName => string.Join("-", Order.Select(i => Wiring.RotorNames[i]));
    public string CorePos => $"{(char)('A' + L)}{(char)('A' + M)}{(char)('A' + R)}";

    public int PlugCount
    {
        get { int n = 0; for (int a = 0; a < 26; a++) if (Plug[a] > a) n++; return n; }
    }

    public string PlugText()
    {
        var pairs = new List<string>();
        var self = new StringBuilder();
        var unknown = new StringBuilder();
        for (int a = 0; a < 26; a++)
        {
            if (Plug[a] > a) pairs.Add($"{(char)('A' + a)}{(char)('A' + Plug[a])}");
            else if (Plug[a] == a) self.Append((char)('A' + a));
            else if (Plug[a] < 0) unknown.Append((char)('A' + a));
        }
        var sb = new StringBuilder(pairs.Count > 0 ? string.Join(" ", pairs) : "(none)");
        if (self.Length > 0) sb.Append($"   unplugged: {self}");
        if (unknown.Length > 0) sb.Append($"   undetermined: {unknown}");
        return sb.ToString();
    }
}

public static class BombeSearch
{
    public static List<int[]> Orders(SearchOptions o)
    {
        if (o.FixedOrder != null) return new List<int[]> { o.FixedOrder };
        var list = new List<int[]>();
        foreach (int a in o.Rotors)
            foreach (int b in o.Rotors)
                foreach (int c in o.Rotors)
                    if (a != b && a != c && b != c) list.Add(new[] { a, b, c });
        return list;
    }

    /// <summary>Tests every (rotor order, L, M, R) for one menu. Returns the stops.</summary>
    public static List<Stop> Run(Menu menu, SearchOptions o, int alignment, out long settingsTested)
    {
        var orders = Orders(o);
        var sink = new ConcurrentBag<Stop>();
        int units = orders.Count * 26;   // one unit = (order, left position); 26*26 inner settings each
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Threads) };
        Parallel.For(0, units, po, () => new Worker(menu, o),
            (u, _, w) =>
            {
                w.RunUnit(orders[u / 26], u % 26, alignment, sink);
                return w;
            },
            _ => { });
        settingsTested = (long)units * 26 * 26;

        var res = sink.ToList();
        res.Sort((x, y) =>
        {
            for (int i = 0; i < 3; i++)
            {
                int c = x.Order[i].CompareTo(y.Order[i]);
                if (c != 0) return c;
            }
            int d = x.L.CompareTo(y.L);
            if (d != 0) return d;
            d = x.M.CompareTo(y.M);
            return d != 0 ? d : x.R.CompareTo(y.R);
        });
        return res;
    }

    /// <summary>
    /// Decrypts the whole ciphertext with the stop's model. Plugboard gaps are treated as unplugged and any output
    /// letter that depended on such a gap is returned in lower case (= uncertain).
    /// </summary>
    public static string Decrypt(string cipher, Stop s, char reflector)
    {
        var refl = Wiring.Reflector(reflector);
        var sb = new StringBuilder(cipher.Length);
        for (int t = 0; t < cipher.Length; t++)
        {
            int j = t - s.Alignment;
            int cx = cipher[t] - 'A';
            int x = Map(s.Plug, cx);
            int sR = ((s.R + j) % 26 + 26) % 26;
            int m = (s.K > 0 && j >= s.K) ? (s.M + 1) % 26 : s.M;
            int[] ord = s.Order;
            int y = Wiring.Fw[(ord[2] * 26 + sR) * 26 + x];
            y = Wiring.Fw[(ord[1] * 26 + m) * 26 + y];
            y = Wiring.Fw[(ord[0] * 26 + s.L) * 26 + y];
            y = refl[y];
            y = Wiring.Bw[(ord[0] * 26 + s.L) * 26 + y];
            y = Wiring.Bw[(ord[1] * 26 + m) * 26 + y];
            y = Wiring.Bw[(ord[2] * 26 + sR) * 26 + y];
            bool uncertain = s.Plug[cx] < 0 || s.Plug[y] < 0;
            char outCh = (char)('A' + Map(s.Plug, y));
            sb.Append(uncertain ? char.ToLowerInvariant(outCh) : outCh);
        }
        return sb.ToString();
    }

    static int Map(sbyte[] p, int x) => p[x] >= 0 ? p[x] : x;

    /// <summary>Per-thread search state. Everything in the hot loop is array lookups.</summary>
    sealed class Worker
    {
        readonly Menu m;
        readonly SearchOptions o;
        readonly int K;
        readonly byte[] refl;
        readonly int[] adjStart, adjTo, adjStep;
        readonly int test;
        readonly sbyte[] S = new sbyte[26];
        readonly int[] stack = new int[26];
        int sp;
        readonly byte[] q1 = new byte[26 * 26];   // Q[s*26+x]: permutation with right rotor at core shift s
        readonly byte[] q2 = new byte[26 * 26];   // same, after the middle rotor stepped once
        int R;

        public Worker(Menu menu, SearchOptions opts)
        {
            m = menu;
            o = opts;
            K = opts.MiddleStep;
            refl = Wiring.Reflector(opts.Reflector);
            adjStart = menu.AdjStart;
            adjTo = menu.AdjTo;
            adjStep = menu.AdjStep;
            test = menu.Primary.TestNode;
        }

        public void RunUnit(int[] order, int L, int alignment, ConcurrentBag<Stop> sink)
        {
            for (int M = 0; M < 26; M++)
            {
                BuildQ(order, L, M, q1);
                if (K > 0) BuildQ(order, L, (M + 1) % 26, q2);
                for (R = 0; R < 26; R++) TestSetting(order, L, M, alignment, sink);
            }
        }

        // Left+middle+reflector part is the same for all right-rotor positions, so fold it into 26 tables.
        void BuildQ(int[] order, int L, int M, byte[] q)
        {
            Span<byte> X = stackalloc byte[26];
            int lb = (order[0] * 26 + L) * 26;
            int mb = (order[1] * 26 + M) * 26;
            for (int y = 0; y < 26; y++)
            {
                int t = Wiring.Fw[mb + y];
                t = Wiring.Fw[lb + t];
                t = refl[t];
                t = Wiring.Bw[lb + t];
                t = Wiring.Bw[mb + t];
                X[y] = (byte)t;
            }
            for (int s = 0; s < 26; s++)
            {
                int rb = (order[2] * 26 + s) * 26;
                for (int x = 0; x < 26; x++)
                    q[s * 26 + x] = Wiring.Bw[rb + X[Wiring.Fw[rb + x]]];
            }
        }

        bool Assign(int a, int x)
        {
            int sa = S[a];
            if (sa >= 0) { if (sa != x) return false; }
            else { S[a] = (sbyte)x; stack[sp++] = a; }
            int sx = S[x];
            if (sx >= 0) { if (sx != a) return false; }
            else { S[x] = (sbyte)a; stack[sp++] = x; }   // plugboard is symmetric: the "diagonal board"
            return true;
        }

        bool Drain()
        {
            while (sp > 0)
            {
                int a = stack[--sp];
                int x = S[a];
                for (int e = adjStart[a]; e < adjStart[a + 1]; e++)
                {
                    int j = adjStep[e];
                    int s = R + j;
                    if (s >= 26) s -= 26;
                    int y = ((K > 0 && j >= K) ? q2 : q1)[s * 26 + x];
                    if (!Assign(adjTo[e], y)) return false;
                }
            }
            return true;
        }

        void TestSetting(int[] order, int L, int M, int alignment, ConcurrentBag<Stop> sink)
        {
            int survivors = 0;
            sbyte[][]? sols = null;
            for (int x = 0; x < 26; x++)
            {
                Array.Fill(S, (sbyte)-1);
                sp = 0;
                if (!Assign(test, x) || !Drain()) continue;
                if (++survivors > o.MaxSurvivors) return;     // menu too weak at this setting
                sols ??= new sbyte[o.MaxSurvivors][];
                sols[survivors - 1] = (sbyte[])S.Clone();
            }
            if (survivors == 0) return;

            for (int i = 0; i < survivors; i++)
            {
                var sol = Stage2(sols![i]);
                if (sol == null) continue;
                var stop = new Stop
                {
                    Order = order, L = L, M = M, R = R, K = K, Alignment = alignment, Plug = sol,
                };
                if (stop.PlugCount > o.MaxPlugs) continue;
                sink.Add(stop);
            }
        }

        // Further crib components that contain loops must also be satisfiable under this setting.
        sbyte[]? Stage2(sbyte[] sol)
        {
            for (int ci = 1; ci < m.Components.Count; ci++)
            {
                var comp = m.Components[ci];
                if (comp.Loops < 1) continue;
                int found = 0;
                sbyte[]? keep = null;
                for (int x = 0; x < 26; x++)
                {
                    Array.Copy(sol, S, 26);
                    sp = 0;
                    if (!Assign(comp.TestNode, x) || !Drain()) continue;
                    found++;
                    keep = (sbyte[])S.Clone();
                }
                if (found == 0) return null;
                if (found == 1) sol = keep!;     // unique -> adopt the extra plugboard knowledge
            }
            return sol;
        }
    }
}
