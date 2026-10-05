using System.Globalization;
using System.Text.RegularExpressions;

namespace Renamer.Core;

// Compare canonical decimal strings without binary rounding or decimal underflow.
public readonly record struct ExactNumber(int Sign, string Digits, int Exponent) : IComparable<ExactNumber>
{
    public static bool TryParse(string? text, out ExactNumber number)
    {
        number = default;
        if (text == null || text.Length > 4096) return false;
        var m = Regex.Match(text.Trim(), @"^(?<s>[+-]?)(?<i>[0-9]*)(?:\.(?<f>[0-9]*))?(?:[eE](?<e>[+-]?[0-9]+))?$", RegexOptions.CultureInvariant, RuleEngine.RegexTimeout);
        if (!m.Success || m.Groups["i"].Length + m.Groups["f"].Length == 0) return false;
        var e = 0;
        if (m.Groups["e"].Success && (!int.TryParse(m.Groups["e"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out e) || Math.Abs((long)e) > 100000)) return false;
        var digits = (m.Groups["i"].Value + m.Groups["f"].Value).TrimStart('0');
        if (digits.Length == 0) { number = new(0, "0", 0); return true; }
        e -= m.Groups["f"].Length;
        var length = digits.Length; digits = digits.TrimEnd('0'); e += length - digits.Length;
        number = new(m.Groups["s"].Value == "-" ? -1 : 1, digits, e); return true;
    }
    public int CompareTo(ExactNumber other)
    {
        if (Sign != other.Sign) return Sign.CompareTo(other.Sign);
        if (Sign == 0) return 0;
        var magnitude = (Digits.Length + Exponent).CompareTo(other.Digits.Length + other.Exponent);
        if (magnitude != 0) return Sign * magnitude;
        for (var i = 0; i < Math.Max(Digits.Length, other.Digits.Length); i++)
        {
            var a = i < Digits.Length ? Digits[i] : '0'; var b = i < other.Digits.Length ? other.Digits[i] : '0';
            if (a != b) return Sign * a.CompareTo(b);
        }
        return 0;
    }
}
