using System.Globalization;
using System.Text;

namespace AgentForge.Documents;

/// <summary>
/// Decides whether an extracted fact's own text is supported by the quote its citation carries.
/// <see cref="CitationBoundingBoxResolver"/> checks that the quote is printed on its page; this checks the
/// other half of the citation's claim, that the quote says what the fact says. A mismatch is stamped
/// <see cref="Extraction.CitationQuoteMatch.Unlocatable"/> by the extractor.
/// </summary>
/// <remarks>
/// <b>The rule: normalised, whole-word, contiguous containment.</b> Both strings are put in Unicode NFC and
/// folded to a sequence of lower-cased words. A word is a run of letters, or a <b>number</b>: a run of
/// numeric characters (digits and characters such as <c>½</c>) together with the punctuation that carries
/// magnitude - a <c>.</c> or <c>,</c> followed by a numeric character (<c>0.5</c>, <c>.5</c>, <c>0,5</c>),
/// a <c>/</c> between two (<c>1/2</c>), a dash between two (a range such as <c>135-145</c> or
/// <c>135–145</c>), a dash that opens one unless a letter precedes it (<c>-5</c>, <c>–5</c>, <c>⁻5</c>), and
/// a comparator that opens one, glued to it or spaced off it (<c>&lt;0.01</c> and <c>&lt; 0.01</c> are both
/// <c>&lt;0.01</c>; <c>&gt;=60</c>). A dash is the hyphen, the minus sign, any other Unicode dash, or a minus
/// look-alike outside that category (<c>⁻</c>, <c>₋</c>, <c>˗</c>, <c>➖</c>, <c>⁃</c>, the soft hyphen).
/// A <c>%</c> is a word of its own. Whitespace and all other punctuation are dropped, a
/// letter/number boundary is a word break (so <c>50mg</c> is <c>50 mg</c>), and a full stop or comma that
/// ends a number is sentence punctuation, which cannot change its value. The text is supported only when
/// its word sequence is non-empty and appears, in order and unbroken, inside the quote's. So case, spacing
/// and punctuation never matter; a word fragment (<c>pen</c> in <c>penicillin</c>), a different number
/// (<c>5</c> against <c>0.5</c>, <c>.5</c>, <c>0,5</c>, <c>1/2</c>, <c>-5</c> or <c>5-10</c>), reordered
/// words and anything the quote does not print all fail.
/// <b>No synonym or abbreviation is forgiven</b>: <c>hydrochlorothiazide</c> is not supported by
/// <c>HCTZ</c>. That is deliberate. The prompt already forbids normalising a printed value, and a false
/// "not found" marks a real fact - which still ships, at page level - while a false "found" would launder
/// an invented one at full confidence. A forgiving rule belongs beside <c>a separate change</c>'s, as a stated floor.
/// <b>Containment is not assertion</b>: a quote that negates the text still carries it, so
/// <c>penicillin</c> is supported by <c>NO penicillin allergy</c>. Whether the quote asserts the fact is
/// outside this rule.
/// <b>Lab results</b> have their own rule, <see cref="IsLabResultSupported"/>. A separate change
/// </remarks>
public static class FactQuoteSupport
{
    /// <summary>Whether <paramref name="quote"/> carries <paramref name="claimed"/> under the rule above.</summary>
    public static bool IsSupported(string claimed, string quote)
    {
        var text = Words(claimed);
        if (text.Count == 0)
        {
            return false;
        }

        var source = Words(quote);
        for (var start = 0; start + text.Count <= source.Count; start++)
        {
            if (RunAt(source, start, text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="quote"/> prints the lab result - <paramref name="testName"/>,
    /// <paramref name="value"/> and <paramref name="unit"/> - as one row, under the same tokenising as
    /// <see cref="IsSupported"/>.
    /// </summary>
    /// <remarks>
    /// The quote's words must hold, in order and unbroken: the analyte's name, then the value, then - when a
    /// unit is claimed - the unit, with at most one abnormal flag (<c>H</c>, <c>L</c>, <c>HH</c>, <c>LL</c>,
    /// <c>A</c>) between value and unit. Adjacency is the point: containment alone would accept a value
    /// filed under the wrong analyte, a value read off the reference range and a unit from the next row of
    /// a multi-row quote. The name must also start a name: the word before it, if any, may not be a word of
    /// letters, so <c>HDL cholesterol</c> is not found in <c>Non-HDL cholesterol</c>, nor <c>K</c> in
    /// <c>Vitamin K</c>. The value may not be glued to the name by a dash (<c>Base excess-5</c> may be a
    /// negative number, <c>CA-125</c> is a name), nor open a <c>to</c> range (<c>135 to 145</c>) or a range
    /// whose dash is spaced on one side only (<c>135 –145</c>, <c>135– 145</c>), nor carry a dash printed
    /// straight after it (<c>5– mmol/L</c> may be a trailing minus). The unit
    /// must be the whole printed unit: the quote's next word may not be glued to it by a <c>/</c>, <c>^</c>,
    /// <c>.</c> or nothing at all, nor joined by a spaced <c>/</c> or <c>^</c> or the word <c>per</c>, so
    /// <c>mg</c> is not found in <c>mg/dL</c> or <c>mg / dL</c>, nor <c>mL/min</c> in <c>mL/min/1.73m2</c> or
    /// <c>mL/min per 1.73 m2</c>. A claimed unit that is also a flag (<c>L</c>, <c>H</c>, ...) is
    /// never found, because a printed <c>9 L</c> cannot say whether it means litres or low.
    /// <b>Abbreviations: an explicit, closed table</b> (<see cref="AnalyteAliases"/>). A claimed name that is
    /// one of a group's spellings may be printed as any spelling in that group, so <c>LDL Cholesterol</c> is
    /// supported by <c>LDL 168</c>. No other name is forgiven: <c>Sodium</c> on <c>Na 128</c> is not found.
    /// Any one-letter spelling - a claimed name such as <c>P</c>, or an alias such as <c>K</c> - also needs a
    /// claimed unit, because <c>WBC 7.2 K 4.5-11.0</c> prints K as thousands directly before a number.
    /// Checking only value and unit was rejected because it cannot see an analyte swap (<c>Potassium 139</c>
    /// cited on the row <c>Sodium 139</c>). A blank unit claims nothing. The costs are false "not found"
    /// results, the safe direction: a quote that omits the unit, puts anything but a flag between value and
    /// unit, or prefixes the name with a word (<c>Serum potassium 5.3</c>) marks a real result, and so do a
    /// bare <c>K 4.1</c> claimed without its unit, a value dashed onto its name (<c>Sodium-136</c>), a value
    /// with a dash after it (<c>Glucose 100- fasting</c>) and a unit followed by a spaced slash
    /// (<c>mg/dL / 5.6 mmol/L</c>). Separate changes
    /// </remarks>
    public static bool IsLabResultSupported(string testName, string value, string? unit, string quote)
    {
        var valueWords = Words(value);
        var unitWords = string.IsNullOrWhiteSpace(unit) ? [] : Words(unit);
        if (valueWords.Count == 0 || (unitWords.Count == 0 && !string.IsNullOrWhiteSpace(unit)))
        {
            return false;
        }

        if (unitWords.Count == 1 && AbnormalFlags.Contains(unitWords[0]))
        {
            return false;
        }

        var tokens = Tokens(quote);
        var source = tokens.ConvertAll(t => t.Text);
        foreach (var name in AnalyteSpellings(Words(testName)))
        {
            if (unitWords.Count == 0 && name.Count == 1 && name[0].Length == 1)
            {
                continue;
            }

            for (var start = 0; start < source.Count; start++)
            {
                if ((start > 0 && char.IsLetter(source[start - 1][0]))
                    || !RunAt(source, start, name)
                    || !RunAt(source, start + name.Count, valueWords)
                    || tokens[start + name.Count].Dashed
                    || tokens[start + name.Count + valueWords.Count - 1].Trailed)
                {
                    continue;
                }

                var next = start + name.Count + valueWords.Count;
                if (RangeHeadAt(source, next))
                {
                    continue;
                }

                if (unitWords.Count == 0
                    || WholeUnitAt(tokens, source, next, unitWords)
                    || (next < source.Count && AbnormalFlags.Contains(source[next]) && WholeUnitAt(tokens, source, next + 1, unitWords)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The unit, not the head of a longer one: the word after it may not be joined on (mg/dL, 1.73m2, mg / dL,
    // mL/min per 1.73 m2).
    private static bool WholeUnitAt(List<Token> tokens, List<string> source, int start, List<string> unit)
    {
        if (!RunAt(source, start, unit))
        {
            return false;
        }

        var after = start + unit.Count;
        return after >= tokens.Count
            || !(tokens[after].Glued || tokens[after].Joined || string.Equals(source[after], "per", StringComparison.Ordinal));
    }

    // The value opens a range, so it is not the result: "135 to 145", or a dash spaced before the tail only
    // ("135 –145"), which reads as a signed number.
    private static bool RangeHeadAt(List<string> source, int i) =>
        (i < source.Count && IsDash(source[i][0]))
        || (i + 1 < source.Count
            && string.Equals(source[i], "to", StringComparison.Ordinal)
            && !char.IsLetter(source[i + 1][0])
            && !string.Equals(source[i + 1], "%", StringComparison.Ordinal));

    /// <summary>
    /// Spellings of one analyte, each group reviewed: every spelling is how a lab report prints that analyte
    /// and no other. Add an entry only with a test that pins it - each is a place where a printed name is
    /// trusted to mean something it does not spell out.
    /// </summary>
    private static readonly string[][] AnalyteAliases =
    [
        ["LDL Cholesterol", "LDL", "LDL-C"],
        ["HDL Cholesterol", "HDL", "HDL-C"],
        ["Potassium", "K", "K+"],
    ];

    private static readonly List<string>[][] AliasWords =
        [.. AnalyteAliases.Select(group => group.Select(Words).ToArray())];

    private static readonly HashSet<string> AbnormalFlags = new(["h", "l", "hh", "ll", "a"], StringComparer.Ordinal);

    // The claimed name, or every spelling in its alias group. An empty name has none, so it grounds nothing.
    private static List<string>[] AnalyteSpellings(List<string> name)
    {
        if (name.Count == 0)
        {
            return [];
        }

        return AliasWords.FirstOrDefault(g => g.Any(spelling => spelling.SequenceEqual(name, StringComparer.Ordinal))) ?? [name];
    }

    private static bool RunAt(List<string> source, int start, List<string> run)
    {
        if (start + run.Count > source.Count)
        {
            return false;
        }

        for (var k = 0; k < run.Count; k++)
        {
            if (!string.Equals(source[start + k], run[k], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> Words(string raw) => Tokens(raw).ConvertAll(t => t.Text);

    // Glued: only a unit joiner (/ ^ . or no character at all) separates this word from the one before it.
    // Joined: a / or ^ with spaces round it does. Dashed: the separator opens with a dash, so a number glued
    // on after a letter may be signed or part of the name (Base excess-5, CA-125). Trailed: a dash follows a
    // number directly, as a trailing minus or a range spaced after its head only (5- mmol/L, 135- 145).
    private readonly record struct Token(string Text, bool Glued, bool Joined, bool Dashed, bool Trailed = false);

    private static List<Token> Tokens(string raw)
    {
        // NFC first, or a decomposed accent is dropped as punctuation and "café" reads as "cafe".
        var value = raw.Normalize(NormalizationForm.FormC);
        var words = new List<Token>();
        var word = new StringBuilder();
        var separator = new StringBuilder();
        var inNumber = false;
        var glued = false;
        var joined = false;
        var dashed = false;

        void Flush()
        {
            if (word.Length > 0)
            {
                words.Add(new(word.ToString(), glued, joined, dashed));
                word.Clear();
                separator.Clear();
            }
        }

        void Open()
        {
            if (word.Length == 0)
            {
                var sep = separator.ToString();
                var after = words.Count > 0;
                glued = after && sep.All(ch => ch is '/' or '^' or '.');
                joined = after && sep.Any(ch => ch is '/' or '^') && sep.All(ch => ch is '/' or '^' || char.IsWhiteSpace(ch));
                dashed = after && sep.Length > 0 && IsDash(sep[0]);
            }
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsNumber(c) || CarriesMagnitude(value, i))
            {
                if (!inNumber)
                {
                    Flush();
                }

                Open();
                inNumber = true;
                word.Append(c);
            }
            else if (inNumber && char.IsWhiteSpace(c) && word.ToString().All(IsComparator))
            {
                // A comparator spaced off the number it opens: "< 0.01" reads as "<0.01".
            }
            else if (c == '%')
            {
                // A unit of its own ("HbA1c 8.2 %"): dropped, it could be neither checked nor contradicted.
                Flush();
                Open();
                inNumber = false;
                word.Append('%');
                Flush();
            }
            else if (char.IsLetter(c))
            {
                if (inNumber)
                {
                    Flush();
                }

                Open();
                inNumber = false;
                word.Append(char.ToLowerInvariant(c));
            }
            else
            {
                var trailing = inNumber && word.Length > 0 && IsDash(c);
                Flush();
                if (trailing)
                {
                    words[^1] = words[^1] with { Trailed = true };
                }

                inNumber = false;
                separator.Append(c);
            }
        }

        Flush();
        return words;
    }

    // Dropping any of these turns one number into another: .5 into 5, 0,5 into 0 5, 1/2 into 1 2, <0.01 into
    // 0.01, -5 into 5, and a range 135-145 into 135 145, whose first number a blank-unit claim could take.
    // Any dash or minus look-alike opens a signed number unless a letter precedes it (-5, –5, ⁻5), and so
    // joins a range (135–145).
    private static bool CarriesMagnitude(string value, int i)
    {
        var next = i + 1 < value.Length && char.IsNumber(value[i + 1]);
        var afterNumber = i > 0 && char.IsNumber(value[i - 1]);
        return value[i] switch
        {
            '.' or ',' => next,
            '/' => next && afterNumber,
            '<' or '>' or '\u2264' or '\u2265' => OpensNumber(value, i + 1 < value.Length && value[i + 1] == '=' ? i + 2 : i + 1, spaced: true),
            '=' => i > 0 && value[i - 1] is '<' or '>' && OpensNumber(value, i + 1, spaced: true),
            _ => IsDash(value[i]) && OpensNumber(value, i + 1, spaced: false) && (i == 0 || !char.IsLetter(value[i - 1])),
        };
    }

    // A hyphen, minus sign or any other Unicode dash (en, em, figure, ...), or a minus look-alike outside
    // DashPunctuation: superscript, subscript, modifier-letter and heavy minus, hyphen bullet, soft hyphen.
    private static bool IsDash(char c) =>
        c is '\u2212' or '\u207b' or '\u208b' or '\u02d7' or '\u2796' or '\u2043' or '\u00ad'
        || char.GetUnicodeCategory(c) == UnicodeCategory.DashPunctuation;

    private static bool IsComparator(char c) => c is '<' or '>' or '=' or '\u2264' or '\u2265';

    private static bool OpensNumber(string value, int i, bool spaced)
    {
        while (spaced && i < value.Length && char.IsWhiteSpace(value[i]))
        {
            i++;
        }

        return i < value.Length
            && (char.IsNumber(value[i]) || (value[i] is '.' or ',' && i + 1 < value.Length && char.IsNumber(value[i + 1])));
    }
}
