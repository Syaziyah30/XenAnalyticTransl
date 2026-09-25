using XenAnalyticTransl.AI;

// Headless harness: runs the same code path as the Studio, with no UI.
// Use it to debug failures and, later, to batch-run the evaluation set.
//
//   dotnet run --project src\XenAnalyticTransl.Cli                 (default model)
//   dotnet run --project src\XenAnalyticTransl.Cli -- <model-id>

var model = args.Length > 0 ? args[0] : "qwen/qwen3.8-flash";

// Reuse the preset for this model so per-model quirks (reasoning, token budget)
// match exactly what the Studio sends.
var preset = ProviderPresets.All.FirstOrDefault(p =>
    string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));

var settings = new LlmSettings
{
    ProviderName = preset?.ProviderName ?? "OpenRouter",
    BaseUrl = preset?.BaseUrl ?? "https://openrouter.ai/api/v1",
    Model = model,
    ApiKeyEnvVar = preset?.ApiKeyEnvVar ?? "OPENROUTER_API_KEY",
    Temperature = 0.1,
    RequestJsonMode = false,
    DisableReasoning = preset?.DisableReasoning ?? true,
    MaxTokens = preset?.MaxTokens ?? 16000,
    SendTemperature = preset?.SendTemperature ?? true
};

var key = settings.ResolveApiKey();
Console.WriteLine($"model : {settings.Model}");
Console.WriteLine($"key   : {(string.IsNullOrWhiteSpace(key) ? "MISSING" : $"present ({key.Length} chars)")}");
Console.WriteLine();

var pack = new ContextPack
{
    Equipment = "Mtr01",
    TargetLanguage = "SCL",
    Scenario =
        "When in Auto Mode, Manual Operation position will change and retain according with Input Run Signal.\n\n" +
        "During PLC Initializing or MCC Trip On, the Manual Operation position will be in Stop.\n\n" +
        "During PLC Initializing, Run Fail Alarm Timer Setpoint assigned value 10 seconds.\n\n" +
        "During PLC Initializing, Trip Alarm Timer Setpoint assigned value 3 seconds.\n\n" +
        "Trip Alarm Timer will start counting when have Trip Feedback On. If the Trip Alarm Timer elapsed, " +
        "Trip Alarm will turn on. Trip Alarm will off and Trip Alarm Timer will be reset when Trip Feedback is off.\n\n" +
        "When Trip Alarm On, Trip Buzzer will turn on and retain. Trip Buzzer only can be reset by operator.",
    Terms =
    [
        new Term { Name = "Auto Mode",            Variable = "AutoMode" },
        new Term { Name = "Manual Operation",     Variable = "ManualOperation" },
        new Term { Name = "Input Run Signal",     Variable = "InputRunSignal" },
        new Term { Name = "PLC Initializing",     Variable = "Sys_plsInit", Value = "1" },
        new Term { Name = "MCC Trip On",          Variable = "MCC_Trip", Value = "1" },
        new Term { Name = "Run Fail Alarm Timer", Variable = "RunFailAlarmTimer" },
        new Term { Name = "Trip Alarm Timer",     Variable = "TripAlarmTimer" },
        new Term { Name = "Trip Feedback",        Variable = "TripFeedback" },
        new Term { Name = "Trip Alarm",           Variable = "TripAlarm" },
        new Term { Name = "Trip Buzzer",          Variable = "TripBuzzer" },
        new Term { Name = "Operator Reset Buzzer", Variable = "OperatorResetBuzzer" },
    ],
    Timers = ["RunFailAlarmTimer", "TripAlarmTimer"],
    Functions = ["PLC Timer", "Time Recorder"],
    Conventions = "Siemens TIA Portal SCL. Comment each block with the sentence it implements."
};

using var translator = new LlmTranslator();
var outcome = await translator.TranslateAsync(pack, settings);

Console.WriteLine($"elapsed : {outcome.ElapsedMs} ms");
Console.WriteLine($"tokens  : {outcome.PromptTokens} in / {outcome.CompletionTokens} out");
Console.WriteLine();

if (!outcome.Ok)
{
    Console.WriteLine("=== FAILED ===");
    Console.WriteLine(outcome.Error);
    Console.WriteLine();
    Console.WriteLine("=== RAW RESPONSE ===");
    Console.WriteLine(outcome.RawResponse ?? "(none)");
    return 1;
}

var r = outcome.Result!;
Console.WriteLine("=== GENERATED SCL ===");
Console.WriteLine(r.Code);
Console.WriteLine();
Console.WriteLine("=== EXPLANATION ===");
Console.WriteLine(r.Explanation);
Console.WriteLine();
Console.WriteLine("=== UNKNOWN IDENTIFIERS ===");
Console.WriteLine(outcome.UnknownIdentifiers.Count == 0
    ? "none"
    : string.Join(", ", outcome.UnknownIdentifiers));
Console.WriteLine();
Console.WriteLine("=== MODEL WARNINGS ===");
Console.WriteLine(r.Warnings.Count == 0 ? "none" : string.Join("\n", r.Warnings.Select(w => "- " + w)));
return 0;
