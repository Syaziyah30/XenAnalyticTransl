using System.Text.Json.Serialization;

namespace XenAnalyticTransl.AI;

/// <summary>A term highlighted in a scenario, mapped to the PLC variable it stands for.</summary>
public sealed class Term
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("variable")] public string Variable { get; set; } = "";
    /// <summary>
    /// IEC data type: BOOL, INT, REAL, TIME, TON...
    ///
    /// Leave it blank and the model guesses - and guesses differently run to run
    /// ("ManualOperation := FALSE" one time, ":= 0" the next). Stating the type is
    /// the single most effective way to make output reproducible.
    /// </summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("states")] public List<string>? States { get; set; }
}

/// <summary>
/// Everything the model needs to translate one scenario. Assembled from the UI panels:
/// the scenario text, every highlighted term with its real PLC variable, and the
/// timers/functions available. This is the single biggest quality lever - a model given
/// the real variable names uses them; one without them invents plausible ones.
/// </summary>
public sealed class ContextPack
{
    [JsonPropertyName("equipment")] public string Equipment { get; set; } = "";
    [JsonPropertyName("target_language")] public string TargetLanguage { get; set; } = "SCL";
    [JsonPropertyName("scenario")] public string Scenario { get; set; } = "";
    [JsonPropertyName("terms")] public List<Term> Terms { get; set; } = new();
    /// <summary>
    /// The other scenarios in the profile, so the model can resolve and flatten
    /// references. "When auto run ready or manual run ready and no faults" names three
    /// other scenarios - without their text the model cannot flatten them, and the
    /// referencing rules in the system prompt have nothing to act on.
    /// </summary>
    [JsonPropertyName("referenced_scenarios")]
    public List<ScenarioRef> ReferencedScenarios { get; set; } = new();

    [JsonPropertyName("timers")] public List<string> Timers { get; set; } = new();
    [JsonPropertyName("functions")] public List<string> Functions { get; set; } = new();
    [JsonPropertyName("conventions")] public string? Conventions { get; set; }

    /// <summary>Every identifier the model is allowed to reference.</summary>
    public IEnumerable<string> KnownIdentifiers() =>
        Terms.Select(t => t.Variable)
             .Concat(Timers)
             .Where(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>Another scenario in the profile, available to be referenced and flattened.</summary>
public sealed class ScenarioRef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

/// <summary>The structured answer we require back from the model.</summary>
public sealed class TranslationResult
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("explanation")] public string Explanation { get; set; } = "";
    [JsonPropertyName("variables_used")] public List<string> VariablesUsed { get; set; } = new();
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
}

/// <summary>One translation run: the result plus everything R&amp;D needs to compare models.</summary>
public sealed class TranslationOutcome
{
    public TranslationResult? Result { get; init; }
    public List<string> UnknownIdentifiers { get; init; } = new();
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public long ElapsedMs { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public string? RawResponse { get; init; }
    /// <summary>The exact JSON posted to the provider. Holds no key - that rides in a header.</summary>
    public string? RequestBody { get; init; }
    public string? Error { get; init; }

    public bool Ok => Error is null && Result is not null;

    /// <summary>Rough cost in USD. Rates are per 1M tokens.</summary>
    public decimal EstimateCostUsd(decimal inputPerM, decimal outputPerM) =>
        (PromptTokens / 1_000_000m * inputPerM) + (CompletionTokens / 1_000_000m * outputPerM);
}
