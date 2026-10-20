using System.Globalization;
using System.Text;

namespace Clocktail.Domain.Vf;

/// <summary>
/// The V/F curve drawn in text, the way btop draws its graphs: each Braille character (U+2800..U+28FF) is a 2 × 4 grid
/// of dots, so a chart <c>width</c> characters wide has twice as many columns of dots and four rows of dots per line.
/// Voltage runs right, clock up. The curve now is a solid line, stock a dotted one, and where the card runs now a ●.
/// Only points up to the voltage limit are drawn: the card never reaches the ones above it. Nor are the low ones: the
/// curve idles flat at its floor clock (180 MHz on a 5090) up to about 750 mV and climbs steeply to half its top by about
/// 820 mV, where nothing is tuned; the chart starts where the curve now reaches half its top, so the part that is tuned
/// fills it.
/// </summary>
public static class VfChart
{
    public const char Running = '●';
    private const int LabelWidth = 5;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The dot bit for column dx (0..1) and row dy (0..3, top down) of a Braille cell.</summary>
    private static readonly int[,] Bit = { { 0x01, 0x02, 0x04, 0x40 }, { 0x08, 0x10, 0x20, 0x80 } };

    /// <summary>
    /// The chart's lines: the plot with clock labels on the left, the voltage axis and its labels. Empty when the curve
    /// has fewer than two points under the limit. <paramref name="at"/> is drawn only when it falls inside the chart
    /// (an idle card runs below the curve); <see cref="Contains"/> tells.
    /// </summary>
    public static IReadOnlyList<string> Draw(
        IReadOnlyList<(double Mv, double Mhz)> now, IReadOnlyList<(double Mv, double Mhz)>? stock,
        (double Mv, double Mhz)? at, double? maxMv, int width = 56, int height = 12)
    {
        if (Under(now, maxMv).Count < 2 || width < 8 || height < 3) return [];
        var (x0, x1, y0, y1) = Bounds(now, stock, maxMv);
        var n = Window(now, x0, maxMv);
        var st = stock == null ? [] : Window(stock, x0, maxMv);
        if (n.Count < 2) return [];
        int cols = width * 2, rows = height * 4;
        var cells = new int[height, width];

        int Col(double mv) => (int)Math.Round((mv - x0) / (x1 - x0) * (cols - 1));
        int Row(double mhz) => (rows - 1) - (int)Math.Round((mhz - y0) / (y1 - y0) * (rows - 1));
        void Dot(int c, int r)
        {
            if (c < 0 || c >= cols || r < 0 || r >= rows) return;
            cells[r / 4, c / 2] |= Bit[c % 2, r % 4];
        }
        void Line(List<(double Mv, double Mhz)> s, bool dotted)
        {
            // Past its last point the line holds that point's clock up to the limit, the chart's right edge.
            int first = Col(s[0].Mv), last = cols - 1;
            int? prev = null;
            for (int c = first; c <= last; c++)
            {
                int r = Row(At(s, x0 + (x1 - x0) * c / (cols - 1)));
                if (dotted) { if (c % 3 == 0) Dot(c, r); continue; }
                // A solid line fills the rows between neighbouring columns, so a steep stretch stays connected.
                if (prev is { } p) for (int y = Math.Min(p, r); y <= Math.Max(p, r); y++) Dot(c, y);
                else Dot(c, r);
                prev = r;
            }
        }
        if (st.Count >= 2) Line(st, dotted: true);
        Line(n, dotted: false);

        (int R, int C)? mark = at is { } a && Contains(a, x0, x1, y0, y1) ? (Row(a.Mhz) / 4, Col(a.Mv) / 2) : null;
        var lines = new List<string>();
        for (int r = 0; r < height; r++)
        {
            var b = new StringBuilder();
            string label = r == 0 ? F(y1) : r == height - 1 ? F(y0) : "";
            b.Append(label.PadLeft(LabelWidth)).Append(label.Length > 0 ? '┤' : '│');
            for (int c = 0; c < width; c++)
                b.Append(mark is { } m && m.R == r && m.C == c ? Running : (char)(0x2800 + cells[r, c]));
            lines.Add(b.ToString().TrimEnd((char)0x2800));
        }
        lines.Add(new string(' ', LabelWidth) + '└' + new string('─', width));
        string left = $"{F(x0)} mV", mid = $"{F((x0 + x1) / 2)}", right = maxMv == x1 ? $"limit {F(x1)} mV" : $"{F(x1)} mV";
        var axis = new StringBuilder(new string(' ', LabelWidth + 1 + width));
        Put(axis, LabelWidth + 1, left);
        Put(axis, LabelWidth + 1 + width / 2 - mid.Length / 2, mid);
        Put(axis, Math.Max(LabelWidth + 1, LabelWidth + 1 + width - right.Length), right);
        lines.Add(axis.ToString().TrimEnd());
        return lines;
    }

    /// <summary>The voltage and clock the chart spans: from the first point where the curve now reaches half its top to
    /// the limit (or the last point when there is none), the clock rounded out to 100 MHz.</summary>
    public static (double X0, double X1, double Y0, double Y1) Bounds(
        IReadOnlyList<(double Mv, double Mhz)> now, IReadOnlyList<(double Mv, double Mhz)>? stock, double? maxMv)
    {
        var n = Under(now, maxMv);
        double half = n.Max(p => p.Mhz) / 2;
        double x0 = n.First(p => p.Mhz >= half).Mv;
        var all = Window(now, x0, maxMv).Concat(stock == null ? [] : Window(stock, x0, maxMv)).ToList();
        double x1 = Math.Max(all.Max(p => p.Mv), maxMv ?? 0);
        double y0 = Math.Floor(all.Min(p => p.Mhz) / 100) * 100, y1 = Math.Ceiling(all.Max(p => p.Mhz) / 100) * 100;
        if (x1 <= x0) x1 = x0 + 1;
        if (y1 <= y0) y1 = y0 + 100;
        return (x0, x1, y0, y1);
    }

    public static bool Contains((double Mv, double Mhz) p, double x0, double x1, double y0, double y1) =>
        p.Mv >= x0 && p.Mv <= x1 && p.Mhz >= y0 && p.Mhz <= y1;

    /// <summary>The points at or under the limit, in voltage order.</summary>
    private static List<(double Mv, double Mhz)> Under(IReadOnlyList<(double Mv, double Mhz)> s, double? maxMv) =>
        s.Where(p => maxMv is not { } m || p.Mv <= m + 0.5).OrderBy(p => p.Mv).ToList();

    private static List<(double Mv, double Mhz)> Window(IReadOnlyList<(double Mv, double Mhz)> s, double x0, double? maxMv) =>
        Under(s, maxMv).Where(p => p.Mv >= x0 - 0.5).ToList();

    /// <summary>The clock at a voltage: a point's clock holds until the next point's voltage, as the driver runs it.</summary>
    private static double At(List<(double Mv, double Mhz)> s, double mv)
    {
        double y = s[0].Mhz;
        foreach (var p in s)
        {
            if (p.Mv > mv + 1e-9) break;
            y = p.Mhz;
        }
        return y;
    }

    private static void Put(StringBuilder b, int at, string text)
    {
        for (int i = 0; i < text.Length && at + i < b.Length; i++) b[at + i] = text[i];
    }

    private static string F(double v) => v.ToString("0", Inv);
}
