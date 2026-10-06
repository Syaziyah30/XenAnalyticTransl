using System.Text.RegularExpressions;

namespace XenAnalyticTransl.AI;

/// <summary>
/// Strips a model's internal monologue out of generated code.
///
/// Models that cannot think in a reasoning channel think in the only place they can
/// write - comments. One run produced eighty lines of "Wait, let me check..." above
/// eight lines of SCL. The prompt forbids it and some models ignore the prompt, so this
/// enforces it mechanically.
///
/// Only ever removes COMMENT lines; executable code is never touched. The worst case is
/// a comment that should have stayed, which is a far better failure than the alternative.
/// </summary>
public static class CodeCleaner
{
    /// <summary>
    /// A comment longer than this is prose, not a description. Real ones look like
    /// "// During PLC Initialize, Run Fail Timer's delay time Set to 10 seconds." - well
    /// under this. Deliberation runs to full paragraphs.
    /// </summary>
    private const int MaxCommentLength = 110;

    /// <summary>At most this many PROSE comment lines before the first statement.</summary>
    private const int MaxLeadingComments = 4;

    /// <summary>
    /// A comment at or under this length is a label, not prose, so it never counts
    /// toward the leading-comment wall. Without this, a scenario whose first blocks are
    /// all unwritable (Valve / System Handle) piles up legitimate labels and the rule
    /// starts eating them.
    /// </summary>
    private const int MaxLabelLength = 60;

    /// <summary>
    /// First-person or tentative phrasing - the mark of deliberation. A comment that
    /// describes what a block does never says "I will", "wait", or "assuming".
    /// </summary>
    private static readonly string[] Tells =
    {
        "wait", "let's", "lets ", "let me", "i will", "i must", "i cannot", "i can't",
        "i should", "i'll", "i am", "i have to", "i need to", "decision:", "re-read",
        "re-reading", "reevaluat", "re-evaluat", "double check", "double-check",
        "actually", "hmm", "alternative interpretation", "on reflection",
        "rule 1", "rule 2", "rule 3", "rule 4", "rule 5", "rule 6", "rule 7",
        "rule 8", "rule 9", "rule 10", "rule 11", "rule 12",
        "looking at", "look closer", "is it possible", "did i miss", "should i",
        "but wait", "however", "for the sake of", "pending resolution",
        "assuming", "this implies", "this suggests", "likely an error", "probably",
        "might be", "may be a placeholder", "unusable", "it seems", "perhaps",
        "given the strict", "strictly following", "i interpret", "my reading",
    };

    private static readonly Regex CommentLine = new(@"^\s*//", RegexOptions.Compiled);

    /// <summary>
    /// An excuse for code the model did not write: "(no Trip Alarm Timer instance and no
    /// Trip Alarm variable supplied - see warnings)". The refusal is right, the place is
    /// wrong - it belongs in "warnings". The sanctioned form is a bare
    /// "// TODO: name - no PLC tag", which this leaves alone.
    /// </summary>
    private static readonly string[] Excuses =
    {
        "see warnings", "see warning", "not supplied", "no variable supplied",
        "instance supplied", "variable supplied", "no plc variable", "not defined in",
        "not in the tag table", "cannot be resolved", "could not be resolved",
        "no tag for", "omitted because", "skipped because",
    };

    /// <summary>A comment that is nothing but a parenthetical aside.</summary>
    private static readonly Regex ParentheticalOnly =
        new(@"^\(.*\)[.]?$", RegexOptions.Compiled);

    public static (string Code, int Removed) Clean(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (code ?? "", 0);

        var lines = code.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        var removed = 0;
        var leadingComments = 0;
        var seenCode = false;

        foreach (var line in lines)
        {
            var isComment = CommentLine.IsMatch(line);

            if (!isComment)
            {
                if (line.Trim().Length > 0) { seenCode = true; leadingComments = 0; }
                kept.Add(line);
                continue;
            }

            var body = line.TrimStart().TrimStart('/').Trim();
            var lower = body.ToLowerInvariant();

            // "// TODO: Trip Alarm - no PLC tag" is the sanctioned way to mark an
            // unresolvable tag, so it survives everything below.
            var isTodo = lower.StartsWith("todo:");

            var drop = !isTodo && (
                body.Length > MaxCommentLength ||                       // a paragraph, not a label
                body.EndsWith('?') ||                                   // a question to itself
                Tells.Any(t => lower.Contains(t)) ||
                Excuses.Any(x => lower.Contains(x)) ||                  // an excuse, belongs in warnings
                ParentheticalOnly.IsMatch(body) ||                      // a bare aside
                (!seenCode && body.Length > MaxLabelLength
                           && ++leadingComments > MaxLeadingComments));  // a wall before any code

            if (drop) { removed++; continue; }
            kept.Add(line);
        }

        var text = string.Join("\n", kept);
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim('\n');
        return (text, removed);
    }
}
