// Implements ADR-002: Coolify API version and client strategy (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// Minimal SemVer-ish comparator used by the configure-phase floor check (FT-002 §Behaviour
/// step 5). Compares the first three dot-separated integer components; any pre-release /
/// build suffix after <c>-</c> or <c>+</c> is dropped (a pre-release of a major version is
/// treated equal to its release on the integer triple — adequate for the "below floor"
/// gate; finer SemVer pre-release ordering is out of scope).
/// </summary>
internal static class SemVerCompare
{
    public static int Compare(string a, string b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        for (int i = 0; i < 3; i++)
        {
            int c = pa[i].CompareTo(pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static int[] Parse(string s)
    {
        var result = new int[3];
        if (string.IsNullOrWhiteSpace(s)) return result;
        var core = s.Split('-', 2)[0].Split('+', 2)[0];
        var parts = core.Split('.');
        for (int i = 0; i < 3 && i < parts.Length; i++)
        {
            int.TryParse(parts[i], out result[i]);
        }
        return result;
    }
}
