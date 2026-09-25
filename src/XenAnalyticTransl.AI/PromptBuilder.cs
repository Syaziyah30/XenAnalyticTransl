using System.Text;
using System.Text.Json;

namespace XenAnalyticTransl.AI;

/// <summary>
/// Turns a ContextPack into the two messages we send the model.
/// Edit the system prompt here - it is the highest-leverage file in the project,
/// and every change should be re-run against the evaluation set before it is kept.
/// </summary>
public static class PromptBuilder
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public const string SystemPrompt = """
        You are a PLC programming assistant for Siemens TIA Portal. You translate a control
        scenario written in natural language into SCL (Structured Text).

        RULES

        1. Use ONLY identifiers supplied in the "terms" and "timers" arrays of the context.
           Never invent a variable name. If the scenario needs something that is not in the
           context, do not guess - add a warning saying exactly what is missing.
        2. Target IEC 61131-3 Structured Text as accepted by TIA Portal SCL.
        3. Timers are IEC TON function blocks: call as Timer(IN := <condition>);
           read the elapsed flag as Timer.Q, set the preset as Timer.PT := T#10s.
        4. "Retain" or "latch" means the value holds until something explicitly resets it -
           write an explicit reset branch, never rely on implicit behaviour.
        5. Comment each block with the sentence of the scenario it implements.
        6. Do not emit a variable declaration block (VAR ... END_VAR) - the project already
           declares these tags. Emit executable logic only.
        7. Prefer several small IF blocks in scenario order over one nested condition.

        WARNINGS - add one for each of these when it applies:
        - the scenario does not say what happens in some state
        - two statements could contradict each other
        - a term is referenced but has no PLC variable in the context
        - you had to assume an execution order that the scenario does not state

        Return ONLY a JSON object, no prose and no markdown fences:

        {
          "code": "<the SCL, newlines as \n>",
          "explanation": "<short plain-English walkthrough of the logic>",
          "variables_used": ["<every identifier your code references>"],
          "warnings": ["<one string per warning, empty array if none>"]
        }
        """;

    public static string BuildUserMessage(ContextPack pack)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Equipment: {pack.Equipment}");
        sb.AppendLine($"Target language: {pack.TargetLanguage}");
        sb.AppendLine();

        sb.AppendLine("SCENARIO");
        sb.AppendLine(pack.Scenario.Trim());
        sb.AppendLine();

        sb.AppendLine("TERM GLOSSARY - the only variables you may use");
        if (pack.Terms.Count == 0)
        {
            sb.AppendLine("(none supplied)");
        }
        else
        {
            foreach (var t in pack.Terms)
            {
                sb.Append($"- \"{t.Name}\" -> {t.Variable}");
                if (!string.IsNullOrWhiteSpace(t.Value)) sb.Append($" (active value: {t.Value})");
                if (t.States is { Count: > 0 }) sb.Append($" (states: {string.Join(", ", t.States)})");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        if (pack.Timers.Count > 0)
        {
            sb.AppendLine("TIMERS available (IEC TON):");
            foreach (var t in pack.Timers) sb.AppendLine($"- {t}");
            sb.AppendLine();
        }

        if (pack.Functions.Count > 0)
        {
            sb.AppendLine("FUNCTION BLOCKS available:");
            foreach (var f in pack.Functions) sb.AppendLine($"- {f}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(pack.Conventions))
        {
            sb.AppendLine("PROJECT CONVENTIONS");
            sb.AppendLine(pack.Conventions!.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("Translate the scenario into " + pack.TargetLanguage + " and return the JSON object.");
        return sb.ToString();
    }

    /// <summary>The context pack as JSON - handy for saving test cases to disk.</summary>
    public static string ToJson(ContextPack pack) => JsonSerializer.Serialize(pack, Pretty);

    public static ContextPack? FromJson(string json) =>
        JsonSerializer.Deserialize<ContextPack>(json);
}
