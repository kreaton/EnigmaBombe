using System.Text;

namespace BombeSim;

/// <summary>Army/Luftwaffe Enigma I wiring (rotors I-V, reflectors B and C) plus per-position lookup tables.</summary>
public static class Wiring
{
    public static readonly string[] RotorWiring =
    {
        "EKMFLGDQVZNTOWYHXUSPAIBRCJ", // I
        "AJDKSIRUXBLHWTMCQGZNPYFVOE", // II
        "BDFHJLCPRTXVZNYEIWGAKMUSQO", // III
        "ESOVPZJAYQUIRHXLNFTGKDCMWB", // IV
        "VZBRGITYUPSDNHLXAWMJQOFECK", // V
    };

    public static readonly string[] RotorNames = { "I", "II", "III", "IV", "V" };

    /// <summary>Turnover notch (window letter at which the rotor carries to its left neighbour).</summary>
    public static readonly char[] Notch = { 'Q', 'E', 'V', 'J', 'Z' };

    public const string ReflectorB = "YRUHQSLDPXNGOKMIEBFZCWVJAT";
    public const string ReflectorC = "FVPJIAOYEDRZXWGCTKUQSBNMHL";

    /// <summary>Forward table: Fw[(rotor*26 + coreShift)*26 + x]. coreShift = windowPosition - ringSetting.</summary>
    public static readonly byte[] Fw = new byte[5 * 26 * 26];

    /// <summary>Backward (inverse) table, same indexing as <see cref="Fw"/>.</summary>
    public static readonly byte[] Bw = new byte[5 * 26 * 26];

    static Wiring()
    {
        for (int k = 0; k < 5; k++)
        {
            var w = new int[26];
            var inv = new int[26];
            for (int i = 0; i < 26; i++)
            {
                w[i] = RotorWiring[k][i] - 'A';
                inv[w[i]] = i;
            }
            for (int s = 0; s < 26; s++)
            {
                for (int x = 0; x < 26; x++)
                {
                    Fw[(k * 26 + s) * 26 + x] = (byte)((w[(x + s) % 26] - s + 26) % 26);
                    Bw[(k * 26 + s) * 26 + x] = (byte)((inv[(x + s) % 26] - s + 26) % 26);
                }
            }
        }
    }

    public static byte[] Reflector(char name)
    {
        string r = char.ToUpperInvariant(name) switch
        {
            'B' => ReflectorB,
            'C' => ReflectorC,
            _ => throw new ArgumentException("Reflector must be B or C."),
        };
        var t = new byte[26];
        for (int i = 0; i < 26; i++) t[i] = (byte)(r[i] - 'A');
        return t;
    }
}

public static class Util
{
    /// <summary>Upper-cases and keeps only A-Z.</summary>
    public static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            char u = char.ToUpperInvariant(ch);
            if (u >= 'A' && u <= 'Z') sb.Append(u);
        }
        return sb.ToString();
    }

    public static int[] Letters(string s) => Clean(s).Select(c => c - 'A').ToArray();
}

/// <summary>
/// Full reference Enigma (3 rotors, ring settings, double-stepping, plugboard).
/// Used for generating test traffic and the "encrypt" command; the Bombe itself uses a simplified core model.
/// </summary>
public sealed class Enigma
{
    readonly int[] rotor;           // [left, middle, right] -> rotor index 0..4
    readonly int[] ring = new int[3];
    readonly int[] pos = new int[3];
    readonly byte[] refl;
    readonly byte[] plug = new byte[26];

    public Enigma(int[] rotors, char reflector, int[] rings, int[] positions, string plugs)
    {
        rotor = rotors;
        refl = Wiring.Reflector(reflector);
        Array.Copy(rings, ring, 3);
        Array.Copy(positions, pos, 3);
        for (int i = 0; i < 26; i++) plug[i] = (byte)i;
        foreach (var pair in plugs.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            string p = Util.Clean(pair);
            if (p.Length != 2 || p[0] == p[1]) throw new ArgumentException($"Bad plug pair '{pair}'.");
            int a = p[0] - 'A', b = p[1] - 'A';
            if (plug[a] != a || plug[b] != b) throw new ArgumentException($"Letter used twice in plugboard: '{pair}'.");
            plug[a] = (byte)b;
            plug[b] = (byte)a;
        }
    }

    /// <summary>Core shifts (window position minus ring setting) of left, middle, right rotor.</summary>
    public int[] Core() => new[]
    {
        (pos[0] - ring[0] + 26) % 26,
        (pos[1] - ring[1] + 26) % 26,
        (pos[2] - ring[2] + 26) % 26,
    };

    void Step()
    {
        bool midAtNotch = pos[1] == Wiring.Notch[rotor[1]] - 'A';
        bool rightAtNotch = pos[2] == Wiring.Notch[rotor[2]] - 'A';
        if (midAtNotch)
        {
            pos[0] = (pos[0] + 1) % 26;
            pos[1] = (pos[1] + 1) % 26;
        }
        else if (rightAtNotch)
        {
            pos[1] = (pos[1] + 1) % 26;
        }
        pos[2] = (pos[2] + 1) % 26;
    }

    public int Press(int x)
    {
        Step();
        var c = Core();
        x = plug[x];
        for (int i = 2; i >= 0; i--) x = Wiring.Fw[(rotor[i] * 26 + c[i]) * 26 + x];
        x = refl[x];
        for (int i = 0; i < 3; i++) x = Wiring.Bw[(rotor[i] * 26 + c[i]) * 26 + x];
        return plug[x];
    }

    public string Process(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in Util.Clean(text)) sb.Append((char)('A' + Press(ch - 'A')));
        return sb.ToString();
    }
}
