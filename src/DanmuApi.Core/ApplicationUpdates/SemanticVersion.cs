using System.Text.RegularExpressions;

namespace DanmuApi.Core.ApplicationUpdates;

/// <summary>Strict SemVer 2.0.0. Build metadata does not affect precedence.</summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly string[] core;
    private readonly string[] pre;
    public string Value { get; }
    public bool IsPrerelease => pre.Length != 0;
    private SemanticVersion(string value, string[] core, string[] pre) => (Value, this.core, this.pre) = (value, core, pre);
    public static SemanticVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 1024) throw new FormatException("Semantic version is too long.");
        var match = Pattern.Match(value);
        if (!match.Success) throw new FormatException($"Invalid semantic version: {value}");
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (pre.Any(p => Numeric(p) && p.Length > 1 && p[0] == '0')) throw new FormatException("Numeric prerelease identifiers cannot have leading zeroes.");
        return new(value, [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value], pre);
    }
    private static bool Numeric(string s) => s.All(c => c is >= '0' and <= '9');
    private static int NumberCompare(string a, string b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++) { var c = NumberCompare(core[i], other.core[i]); if (c != 0) return c; }
        if (pre.Length == 0 || other.pre.Length == 0) return pre.Length == other.pre.Length ? 0 : pre.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(pre.Length, other.pre.Length); i++)
        {
            var a = pre[i]; var b = other.pre[i];
            var c = Numeric(a) && Numeric(b) ? NumberCompare(a, b) : Numeric(a) != Numeric(b) ? Numeric(a) ? -1 : 1 : string.CompareOrdinal(a, b);
            if (c != 0) return c;
        }
        return pre.Length.CompareTo(other.pre.Length);
    }
    public override string ToString() => Value;
}
