using System.Text.RegularExpressions;

namespace XenAnalyticTransl.AI;

/// <summary>
/// Safety gate 1. No AI involved - a set lookup against the tag table.
///
/// This catches the most dangerous failure mode: a hallucinated variable name. The
/// generated code still compiles and still reads correctly, so nothing else will
/// notice. Run this before showing any result to an engineer.
/// </summary>
public static class TagValidator
{
    // SCL keywords, types and literals that are not variables.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF","THEN","ELSE","ELSIF","END_IF","CASE","OF","END_CASE","FOR","TO","BY","DO","END_FOR",
        "WHILE","END_WHILE","REPEAT","UNTIL","END_REPEAT","RETURN","EXIT","CONTINUE",
        "AND","OR","XOR","NOT","MOD","TRUE","FALSE","NULL",
        "VAR","VAR_INPUT","VAR_OUTPUT","VAR_IN_OUT","VAR_TEMP","END_VAR","CONSTANT","RETAIN",
        "BOOL","BYTE","WORD","DWORD","INT","DINT","SINT","USINT","UINT","UDINT","REAL","LREAL",
        "TIME","DATE","STRING","CHAR","ARRAY","STRUCT","END_STRUCT",
        "IN","PT","Q","ET","TON","TOF","TP","R_TRIG","F_TRIG"
    };

    /// <summary>
    /// Identifiers the model claims to have used that are not in the context pack.
    /// A non-empty result means the output must not be trusted without review.
    /// </summary>
    public static IEnumerable<string> FindUnknown(TranslationResult result, ContextPack pack)
    {
        var known = BuildKnownSet(pack);

        // What the model said it used.
        var claimed = result.VariablesUsed
            .Select(Normalise)
            .Where(v => v.Length > 0 && !Reserved.Contains(v));

        // What the code actually references - the model's own list can be incomplete.
        var inCode = ExtractIdentifiers(result.Code);

        return claimed.Concat(inCode)
                      .Where(v => !known.Contains(v))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(v => v, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Identifiers supplied in the context that the model never used - possible missed logic.</summary>
    public static IEnumerable<string> FindUnused(TranslationResult result, ContextPack pack)
    {
        var used = new HashSet<string>(ExtractIdentifiers(result.Code), StringComparer.OrdinalIgnoreCase);
        return pack.KnownIdentifiers()
                   .Select(Normalise)
                   .Where(v => v.Length > 0 && !used.Contains(v))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(v => v, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildKnownSet(ContextPack pack)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in pack.KnownIdentifiers())
        {
            var n = Normalise(id);
            if (n.Length > 0) set.Add(n);
        }
        return set;
    }

    /// <summary>Strips member access so Timer.Q and Timer.PT validate against Timer.</summary>
    private static string Normalise(string identifier)
    {
        var s = (identifier ?? "").Trim();
        var dot = s.IndexOf('.');
        if (dot > 0) s = s[..dot];
        return s.Trim();
    }

    /// <summary>Pulls candidate identifiers out of SCL, ignoring comments, strings and literals.</summary>
    private static IEnumerable<string> ExtractIdentifiers(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) yield break;

        var stripped = Regex.Replace(code, @"//.*?$", " ", RegexOptions.Multiline);
        stripped = Regex.Replace(stripped, @"\(\*.*?\*\)", " ", RegexOptions.Singleline);
        stripped = Regex.Replace(stripped, @"'[^']*'", " ");
        stripped = Regex.Replace(stripped, @"\bT#\w+", " ");   // time literals: T#10s

        foreach (Match m in Regex.Matches(stripped, @"[A-Za-z_][A-Za-z0-9_]*"))
        {
            var id = m.Value;
            if (Reserved.Contains(id)) continue;
            if (Regex.IsMatch(id, @"^\d")) continue;
            yield return id;
        }
    }
}
