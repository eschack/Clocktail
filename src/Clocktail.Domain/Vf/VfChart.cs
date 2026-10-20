using System.Globalization;
using System.Text;

namespace Clocktail.Domain.Vf;

/// <summary>
/// The V/F curve drawn in text, the way btop draws its graphs: each Braille character (U+2800..U+28FF) is a 2 × 4 grid
/// of dots. Voltage runs right, clock up, on a fixed scale so values can be read off the axes: a line of characters is
/// 100 MHz (a dot 25 MHz) and a character 5 mV (a dot 2.5 mV, half the curve's own point spacing), each line labelled
/// with the clock of its top dot, the voltage axis ticked every 25 mV and numbered every 50. A curve too tall or wide for
/// that takes 200 MHz a line or 10 mV a character. The curve now is a solid line, stock a dotted one, and where the card
/// runs now a ●. Only points up to the voltage limit are drawn: the card never reaches the ones above it. Nor are the
/// low ones: the curve idles flat at its floor clock (180 MHz on a 5090) up to about 750 mV and climbs steeply to half
/// its top by about 820 mV, where nothing is tuned; the chart starts where the curve now reaches 60 % of its top.
/// </summary>
public static class VfChart
{
    public const char Running = '●';
    /// <summary>The chart starts where the curve now reaches this share of its top.</summary>
    public const double TunedPart = 0.6;
    private const int LabelWidth = 5, MaxLines = 24, MaxChars = 72;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The dot bit for column dx (0..1) and row dy (0..3, top down) of a Braille cell.</summary>
    private static readonly int[,] Bit = { { 0x01, 0x02, 0x04, 0x40 }, { 0x08, 0x10, 0x20, 0x80 } };

    /// <summary>The scale and span of a chart: MHz a line and mV a character, the voltage at the left edge and at the
    /// right (the limit), the clock of the bottom line and of the top one.</summary>
    public sealed record Frame(double MhzPerLine, double MvPerChar, double X0, double X1, double Y0, double Y1)
    {
        public int Lines => (int)Math.Round((Y1 - Y0) / MhzPerLine) + 1;
        public int Chars => (int)Math.Ceiling((X1 - X0) / MvPerChar - 1e-9) + 1;
        public bool Contains((double Mv, double Mhz) p) => p.Mv >= X0 && p.Mv <= X1 && p.Mhz >= Y0 && p.Mhz <= Y1;
        /// <summary>The dot column of a voltage and the dot row of a clock, 0 at the left and at the top.</summary>
        public int Col(double mv) => (int)Math.Round((mv - X0) / (MvPerChar / 2));
        public int Row(double mhz) => (int)Math.Round((Y1 - mhz) / (MhzPerLine / 4));
    }

    /// <summary>
    /// The chart's lines: the plot with a clock label on every line, then the voltage axis with its ticks and numbers.
    /// Empty when the curve has fewer than two points under the limit. <paramref name="at"/> is drawn only when it falls
    /// inside the chart (an idle card runs below the curve); <see cref="Frame.Contains"/> tells.
    /// </summary>
    public static IReadOnlyList<string> Draw(
        IReadOnlyList<(double Mv, double Mhz)> now, IReadOnlyList<(double Mv, double Mhz)>? stock,
        (double Mv, double Mhz)? at, double? maxMv)
    {
        if (Bounds(now, stock, maxMv) is not { } f) return [];
        var n = Window(now, f.X0, maxMv);
        var st = stock == null ? [] : Window(stock, f.X0, maxMv);
        int height = f.Lines, width = f.Chars, cols = width * 2, rows = height * 4;
        var cells = new int[height, width];

        void Dot(int c, int r)
        {
            if (c < 0 || c >= cols || r < 0 || r >= rows) return;
            cells[r / 4, c / 2] |= Bit[c % 2, r % 4];
        }
        void Line(List<(double Mv, double Mhz)> s, bool dotted)
        {
            // Past its last point the line holds that point's clock up to the limit, the chart's right edge.
            int first = Math.Max(0, f.Col(s[0].Mv)), last = f.Col(f.X1);
            int? prev = null;
            for (int c = first; c <= last; c++)
            {
                int r = f.Row(At(s, f.X0 + c * f.MvPerChar / 2));
                if (dotted) { if (c % 3 == 0) Dot(c, r); continue; }
                // A solid line fills the rows between neighbouring columns, so a steep stretch stays connected.
                if (prev is { } p) for (int y = Math.Min(p, r); y <= Math.Max(p, r); y++) Dot(c, y);
                else Dot(c, r);
                prev = r;
            }
        }
        if (st.Count >= 2) Line(st, dotted: true);
        Line(n, dotted: false);

        (int R, int C)? mark = at is { } a && f.Contains(a) ? (f.Row(a.Mhz) / 4, f.Col(a.Mv) / 2) : null;
        var lines = new List<string>();
        for (int r = 0; r < height; r++)
        {
            var b = new StringBuilder(F(f.Y1 - r * f.MhzPerLine).PadLeft(LabelWidth)).Append('┤');
            for (int c = 0; c < width; c++)
                b.Append(mark is { } m && m.R == r && m.C == c ? Running : (char)(0x2800 + cells[r, c]));
            lines.Add(b.ToString().TrimEnd((char)0x2800));
        }
        // A tick every 5 characters, a number every 10, centred under its tick.
        var axis = new StringBuilder(new string(' ', LabelWidth)).Append('└');
        var numbers = new StringBuilder(new string(' ', LabelWidth + 1 + width + 4));
        int end = 0;
        for (int c = 0; c < width; c++)
        {
            double mv = f.X0 + c * f.MvPerChar;
            axis.Append(c == width - 1 ? '┘' : Multiple(mv, 5 * f.MvPerChar) ? '┬' : '─');
            if (Multiple(mv, 10 * f.MvPerChar) && c < width - 1)
            {
                int at0 = LabelWidth + 1 + c - F(mv).Length / 2;
                Put(numbers, at0, F(mv));
                end = at0 + F(mv).Length;
            }
        }
        // The right edge is the limit: its voltage under its corner, when it clears the last number.
        string edge = F(f.X1);
        int edgeAt = LabelWidth + width - edge.Length / 2;
        if (edgeAt > end) Put(numbers, edgeAt, edge);
        lines.Add(axis.ToString());
        lines.Add(numbers.ToString().TrimEnd() + " mV");
        return lines;
    }

    /// <summary>The chart's frame: from the first point where the curve now reaches 60 % of its top (rounded down to a
    /// numbered voltage) to the limit (or the last point when there is none), the clock rounded out to whole lines. Null
    /// when fewer than two points lie under the limit.</summary>
    public static Frame? Bounds(
        IReadOnlyList<(double Mv, double Mhz)> now, IReadOnlyList<(double Mv, double Mhz)>? stock, double? maxMv)
    {
        var n = Under(now, maxMv);
        if (n.Count < 2) return null;
        double part = n.Max(p => p.Mhz) * TunedPart;
        double first = n.First(p => p.Mhz >= part).Mv;
        double right = Math.Max(n.Max(p => p.Mv), maxMv ?? 0);
        double mvPerChar = new[] { 5.0, 10, 20 }.FirstOrDefault(w => (right - first) / w + 11 <= MaxChars, 50);
        double x0 = Math.Floor(first / (10 * mvPerChar)) * 10 * mvPerChar;
        // The clock spans the points from that first one up; the climb left of it, between x0 and it, runs off the bottom.
        var all = Window(now, first, maxMv).Concat(stock == null ? [] : Window(stock, first, maxMv)).ToList();
        double lo = all.Min(p => p.Mhz), hi = all.Max(p => p.Mhz);
        double step = new[] { 100.0, 200, 500 }.FirstOrDefault(s => Math.Ceiling(hi / s) - Math.Floor(lo / s) + 1 <= MaxLines, 1000);
        double y0 = Math.Floor(lo / step) * step, y1 = Math.Max(Math.Ceiling(hi / step) * step, y0 + step);
        return new Frame(step, mvPerChar, x0, Math.Max(right, x0 + mvPerChar), y0, y1);
    }

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

    private static bool Multiple(double v, double step) => Math.Abs(v / step - Math.Round(v / step)) < 1e-9;

    private static void Put(StringBuilder b, int at, string text)
    {
        for (int i = 0; i < text.Length && at + i < b.Length; i++)
            if (at + i >= 0) b[at + i] = text[i];
    }

    private static string F(double v) => v.ToString("0", Inv);
}
