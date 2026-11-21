using System.Text;

namespace BombeSim;

/// <summary>
/// A synthetic intercept: random key, random plugboard, filler text around a crib, enciphered by the
/// full reference Enigma. The truth is kept so the Bombe's answer can be checked.
/// </summary>
public sealed class TestCase
{
    public int[] Order = Array.Empty<int>();
    public int[] Ring = Array.Empty<int>();
    public int[] Pos = Array.Empty<int>();
    public string Plugs = "";
    public sbyte[] TruePlug = new sbyte[26];
    public string Plain = "";
    public string Cipher = "";
    public string Crib = "";
    public int Align;
    public int L, M, R;     // true core positions at crib letter 0
    public int K;           // true middle-step index inside the crib (0 = none)
    public Menu Menu = null!;

    public string KeyText =>
        $"rotors {string.Join("-", Order.Select(i => Wiring.RotorNames[i]))}, rings {Letters(Ring)}, " +
        $"start {Letters(Pos)}, plugs {Plugs}";

    static string Letters(int[] v) => new string(v.Select(i => (char)('A' + i)).ToArray());

    /// <summary>
    /// Draws random keys until one is found that the simplified Bombe model can handle
    /// (no double-step and at most one middle-rotor step inside the crib) and whose menu has enough loops.
    /// </summary>
    public static TestCase Generate(Random rng, string crib, int minLoops = 3)
    {
        crib = Util.Clean(crib);
        for (int attempt = 0; attempt < 5000; attempt++)
        {
            var order = Enumerable.Range(0, 5).OrderBy(_ => rng.Next()).Take(3).ToArray();
            var ring = Enumerable.Range(0, 3).Select(_ => rng.Next(26)).ToArray();
            var pos = Enumerable.Range(0, 3).Select(_ => rng.Next(26)).ToArray();

            var letters = Enumerable.Range(0, 26).OrderBy(_ => rng.Next()).ToArray();
            var truth = new sbyte[26];
            for (int i = 0; i < 26; i++) truth[i] = (sbyte)i;
            var plugParts = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                int a = letters[2 * i], b = letters[2 * i + 1];
                truth[a] = (sbyte)b;
                truth[b] = (sbyte)a;
                plugParts.Add($"{(char)('A' + a)}{(char)('A' + b)}");
            }
            string plugs = string.Join(" ", plugParts);

            int pre = rng.Next(0, 30), post = rng.Next(0, 20);
            string plain = Filler(rng, pre) + crib + Filler(rng, post);

            var e = new Enigma(order, 'B', ring, pos, plugs);
            var cipher = new StringBuilder(plain.Length);
            var cores = new int[plain.Length][];
            for (int t = 0; t < plain.Length; t++)
            {
                cipher.Append((char)('A' + e.Press(plain[t] - 'A')));
                cores[t] = e.Core();
            }

            int n = crib.Length, al = pre;
            bool ok = true;
            int k = 0;
            for (int j = 1; j < n; j++)
            {
                if (cores[al + j][0] != cores[al][0]) ok = false;                  // left rotor moved
                if (cores[al + j][1] != cores[al + j - 1][1])                       // middle rotor moved
                {
                    if (k != 0) ok = false; else k = j;
                }
            }
            if (!ok) continue;

            var menu = Menu.Build(crib, cipher.ToString().Substring(al, n));
            if (menu.Primary.Loops < minLoops) continue;

            return new TestCase
            {
                Order = order, Ring = ring, Pos = pos, Plugs = plugs, TruePlug = truth,
                Plain = plain, Cipher = cipher.ToString(), Crib = crib, Align = al,
                L = cores[al][0], M = cores[al][1], R = cores[al][2], K = k, Menu = menu,
            };
        }
        throw new InvalidOperationException("Could not generate a suitable test case.");
    }

    static string Filler(Random rng, int n)
    {
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.Append((char)('A' + rng.Next(26)));
        return sb.ToString();
    }

    /// <summary>True if the stop is the real key (rotor order, core positions, and no contradiction with the real plugboard).</summary>
    public bool IsTruth(Stop s)
    {
        if (!s.Order.SequenceEqual(Order)) return false;
        if (s.L != L || s.M != M || s.R != R) return false;
        for (int a = 0; a < 26; a++)
            if (s.Plug[a] >= 0 && s.Plug[a] != TruePlug[a]) return false;
        return true;
    }
}
