using System.Text.RegularExpressions;

namespace AgentForge.HermeticTests.Support;

/// <summary>
/// One value no captured log entry may carry. A <see cref="Verbatim"/> value is matched as a substring; so is
/// a <see cref="NamePart"/>, unless it is <see cref="ShortNamePartLength"/> letters or fewer, when it is
/// matched as a whole word: "Ng" is found on its own but not inside "tracking", while "Whitfield" is still
/// found in "HaroldWhitfield.pdf". Both ignore case.
/// </summary>
internal sealed record LogSentinel
{
    private readonly Regex? _wholeWord;

    private LogSentinel(string value, bool wholeWord)
    {
        Value = value;
        _wholeWord = wholeWord
            ? new Regex($@"(?<!\p{{L}}){Regex.Escape(value)}(?!\p{{L}})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : null;
    }

    public string Value { get; }

    /// <summary>An identifier, a full name or a line of document text: matched anywhere in an entry.</summary>
    public static LogSentinel Verbatim(string value) => new(value, wholeWord: false);

    /// <summary>
    /// One part of a name. A short part is matched where no letter touches it on either side - digits,
    /// punctuation and <c>_</c> are boundaries, so <c>name_Ng</c> and <c>Ng:</c> are found; a longer part
    /// anywhere, so a concatenated key, a file name or a plural still carries it.
    /// </summary>
    public static LogSentinel NamePart(string value) => new(value, wholeWord: value.Length <= ShortNamePartLength);

    /// <summary>
    /// Up to this length a name part is an ordinary letter run inside English words ("ng", "an", "Lee",
    /// "Ross"); from five letters up, a substring hit inside a word is rare enough to be worth a look.
    /// </summary>
    public const int ShortNamePartLength = 4;

    /// <summary>
    /// Every part of a seeded demo name to scan for on its own: all but the shared <c>Demo</c> given-name
    /// prefix (<c>tools/SeedDemoPatients</c>), which names nobody.
    /// </summary>
    public static IEnumerable<LogSentinel> NamePartsOf(string fullName) =>
        fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Equals(DemoPrefix, StringComparison.Ordinal))
            .Select(NamePart);

    /// <summary>Whether <paramref name="entry"/> carries this value.</summary>
    public bool IsCarriedBy(string entry) =>
        _wholeWord?.IsMatch(entry) ?? entry.Contains(Value, StringComparison.OrdinalIgnoreCase);

    private const string DemoPrefix = "Demo";
}

/// <summary>Reads captured log entries for sentinel values.</summary>
internal static class LogScan
{
    /// <summary>Every (entry, sentinel) pair where the entry carries the sentinel.</summary>
    public static List<(string Entry, LogSentinel Sentinel)> Leaks(
        IEnumerable<string> entries, IEnumerable<LogSentinel> sentinels)
    {
        var all = sentinels.ToList();
        return [.. entries.SelectMany(e => all.Where(s => s.IsCarriedBy(e)).Select(s => (e, s)))];
    }
}
