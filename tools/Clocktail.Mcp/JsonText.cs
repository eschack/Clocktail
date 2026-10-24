using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clocktail.Mcp;

/// <summary>
/// A tool's JSON answer with every character as itself. System.Text.Json's encoder escapes each character outside the
/// Basic Multilingual Plane even when relaxed, so most emoji (🩺, 📈, 🧠) still went out as "🩺": 38 escapes in
/// card_report after 0.4.0 relaxed the rest, a dozen tokens each for one glyph, and a small model had to put the pairs
/// back together before it could show the table. A pair escaped after an escaped backslash ("\\uD83E") is text, not an
/// escape, and stays as it is.
/// </summary>
public static partial class JsonText
{
    public static string Serialize<T>(T value, JsonSerializerOptions options) => Unescape(JsonSerializer.Serialize(value, options));

    public static string Unescape(string json) => Pair().Replace(json, m => m.Groups[1].Value
        + (char)int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        + (char)int.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    [GeneratedRegex(@"(?<!\\)((?:\\\\)*)\\u([dD][89abAB][0-9a-fA-F]{2})\\u([dD][c-fC-F][0-9a-fA-F]{2})")]
    private static partial Regex Pair();
}
