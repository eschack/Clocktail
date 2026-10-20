namespace Clocktail.Hw;

/// <summary>
/// The (size, revision) pair behind an NVAPI structure version word.
/// <para>
/// The driver reads <c>size | (rev &lt;&lt; 16)</c>. The word is built with OR, so a size above
/// 0xFFFF bleeds into the revision half: 0x15b0c r2 becomes 0x35b0c, 0x424604 r5 becomes 0x474604. The word is
/// therefore ambiguous and is never parsed back; the pair is the truth and the word is derived from it.
/// </para>
/// </summary>
public readonly record struct Versioned
{
    public int Size { get; }
    public int Rev { get; }

    public Versioned(int size, int rev)
    {
        if (size < 4) throw new ArgumentOutOfRangeException(nameof(size), size, "A versioned structure holds at least its version word.");
        if (rev is < 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(rev), rev, "The revision occupies 16 bits.");
        Size = size;
        Rev = rev;
    }

    /// <summary>The word the driver expects at offset 0 (bitwise OR).</summary>
    public uint Word => (uint)Size | ((uint)Rev << 16);

    /// <summary>True when the size spills into the upper half, so <c>Word &gt;&gt; 16</c> is not the revision.</summary>
    public bool Overflows => Size > 0xFFFF;

    public bool Matches(uint word) => word == Word;

    public override string ToString() => Overflows ? $"0x{Size:x} r{Rev} (word 0x{Word:x}, size overflows)" : $"0x{Size:x} r{Rev}";
}
