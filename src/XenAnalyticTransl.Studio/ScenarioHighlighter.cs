using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;

namespace XenAnalyticTransl.Studio;

public enum TokenKind { Vocabulary, Scenario, Property, Term, Constant }

/// <summary>
/// Colours a scenario the way the production editor does, so you can see at a glance
/// what the translator will resolve and what it will treat as plain prose.
///
/// The rules the prompt works to say a reference need not match a name exactly - so the
/// matcher is case-insensitive, ignores extra whitespace, and takes the LONGEST phrase
/// first ("Run Fail Alarm" beats "Run Fail" beats "Alarm").
/// </summary>
public static class ScenarioHighlighter
{
    public static readonly Brush ScenarioBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x56, 0x21)); // dark orange
    public static readonly Brush PropertyBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x56, 0x31)); // dark green
    public static readonly Brush TermBrush     = new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57)); // green
    public static readonly Brush ConstantBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x68, 0xC1)); // blue
    public static readonly Brush VocabBrush    = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)); // black

    /// <summary>
    /// Logical keywords - the words that become operators or control flow in SCL.
    /// Coloured like numbers because both are constants of the language rather than
    /// names from the data: "or" becomes OR, "no" becomes NOT, "when" opens a condition.
    /// Extend this list as the scenarios grow.
    /// </summary>
    public static readonly string[] Keywords =
    {
        "when", "if", "or", "and", "not", "no", "without", "then", "during", "once"
    };

    public sealed record Phrase(string Text, TokenKind Kind);

    /// <summary>A match found in the scenario text.</summary>
    private sealed record Hit(int Start, int Length, TokenKind Kind);

    public static Brush BrushFor(TokenKind k) => k switch
    {
        TokenKind.Scenario => ScenarioBrush,
        TokenKind.Property => PropertyBrush,
        TokenKind.Term => TermBrush,
        TokenKind.Constant => ConstantBrush,
        _ => VocabBrush
    };

    /// <summary>
    /// Splits the text into coloured runs. Longest phrase wins; overlaps are discarded,
    /// so "Run Fail Alarm" is matched once rather than as three separate pieces.
    /// </summary>
    public static List<(string Text, TokenKind Kind)> Tokenize(string text, IEnumerable<Phrase> phrases)
    {
        var result = new List<(string, TokenKind)>();
        if (string.IsNullOrEmpty(text)) return result;

        var hits = new List<Hit>();

        foreach (var p in phrases.Where(p => !string.IsNullOrWhiteSpace(p.Text))
                                 .OrderByDescending(p => p.Text.Length))
        {
            // whitespace in a name may be any whitespace in the text
            var pattern = @"(?<![A-Za-z0-9_])" +
                          string.Join(@"\s+", Regex.Split(p.Text.Trim(), @"\s+").Select(Regex.Escape)) +
                          @"(?![A-Za-z0-9_])";
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
                hits.Add(new Hit(m.Index, m.Length, p.Kind));
        }

        // numbers
        foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z0-9_])\d+(\.\d+)?(?![A-Za-z0-9_])"))
            hits.Add(new Hit(m.Index, m.Length, TokenKind.Constant));

        // logical keywords - the same colour, since both are language constants
        foreach (var kw in Keywords)
            foreach (Match m in Regex.Matches(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(kw)}(?![A-Za-z0-9_])",
                                              RegexOptions.IgnoreCase))
                hits.Add(new Hit(m.Index, m.Length, TokenKind.Constant));

        // keep the longest, earliest, non-overlapping hits
        var taken = new bool[text.Length];
        var kept = new List<Hit>();
        foreach (var h in hits.OrderByDescending(h => h.Length).ThenBy(h => h.Start))
        {
            var free = true;
            for (var i = h.Start; i < h.Start + h.Length; i++)
                if (taken[i]) { free = false; break; }
            if (!free) continue;
            for (var i = h.Start; i < h.Start + h.Length; i++) taken[i] = true;
            kept.Add(h);
        }
        kept.Sort((a, b) => a.Start.CompareTo(b.Start));

        var pos = 0;
        foreach (var h in kept)
        {
            if (h.Start > pos) result.Add((text[pos..h.Start], TokenKind.Vocabulary));
            result.Add((text.Substring(h.Start, h.Length), h.Kind));
            pos = h.Start + h.Length;
        }
        if (pos < text.Length) result.Add((text[pos..], TokenKind.Vocabulary));

        return result;
    }

    /// <summary>Builds the coloured document for a scenario.</summary>
    public static FlowDocument Build(string text, IEnumerable<Phrase> phrases, double fontSize)
    {
        var doc = new FlowDocument { FontSize = fontSize, PagePadding = new System.Windows.Thickness(0) };

        foreach (var para in (text ?? "").Replace("\r\n", "\n").Split("\n\n"))
        {
            var p = new Paragraph { Margin = new System.Windows.Thickness(0, 0, 0, 10) };
            foreach (var (chunk, kind) in Tokenize(para, phrases))
            {
                foreach (var (line, i) in chunk.Split('\n').Select((l, i) => (l, i)))
                {
                    if (i > 0) p.Inlines.Add(new LineBreak());
                    if (line.Length == 0) continue;
                    p.Inlines.Add(new Run(line)
                    {
                        Foreground = BrushFor(kind),
                        FontWeight = kind is TokenKind.Scenario or TokenKind.Property
                            ? System.Windows.FontWeights.SemiBold
                            : System.Windows.FontWeights.Normal
                    });
                }
            }
            doc.Blocks.Add(p);
        }
        return doc;
    }
}
