using XenAnalyticTransl.AI;

// Headless harness: the same code path as the Studio, with no UI.
// Use it to debug failures and, later, to batch-run the evaluation set.
//
//   dotnet run --project src\XenAnalyticTransl.Cli                          default model, Operation
//   dotnet run --project src\XenAnalyticTransl.Cli -- <model-id>
//   dotnet run --project src\XenAnalyticTransl.Cli -- <model-id> "<scenario name>"
//   dotnet run --project src\XenAnalyticTransl.Cli -- list

var model = args.Length > 0 ? args[0] : "qwen/qwen3.8-flash";
var wanted = args.Length > 1 ? args[1] : "Operation";

// ---- the Motor profile, matching the Studio's sample data ----
var profile = new (string Name, string Text)[]
{
    ("Operation",
        "When auto run ready or manual run ready and no faults, then will command run."),

    ("System Handle",
        "When operation mode in Auto, the Manual Start/Stop position will according with " +
        "Operation Signal Run/Stop.\n\n" +
        "During PLC Initialize or MCC Trip On, the Manual Operation position will be in Stop.\n\n" +
        "During PLC Initialize, Run Fail Timer's delay time Set to 10 seconds.\n\n" +
        "Operation Signal Run time will be recorded by runtime recorder."),

    ("Run Failed",
        "When Operation signal Run not received after run fail timer elapsed, turn on Run Fail alarm, " +
        "turn on Run Fail Buzzer once. When HMI Acknowledge, turn off Run Fail Buzzer."),

    ("Auto run ready",
        "When operation mode in Auto and Sequence Auto is fulfilled."),

    ("Manual run ready",
        "When operation mode in Manual and manual started."),

    ("Faults",
        "When trip alarm is on or run fail alarm is on."),

    ("Trip",
        "When Trip Signal is On, turn on Trip Alarm, turn on Trip Buzzer once. " +
        "When HMI Acknowledge, turn off Trip Buzzer."),
};

if (string.Equals(model, "list", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("scenarios:");
    foreach (var s in profile) Console.WriteLine($"  {s.Name}");
    return 0;
}

var current = profile.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase));
if (current.Name is null)
{
    Console.WriteLine($"No scenario called \"{wanted}\". Known: {string.Join(", ", profile.Select(p => p.Name))}");
    return 1;
}

// Reuse the preset for this model so per-model quirks match the Studio exactly.
var preset = ProviderPresets.All.FirstOrDefault(p =>
    string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));

var settings = new LlmSettings
{
    ProviderName = preset?.ProviderName ?? "OpenRouter",
    BaseUrl = preset?.BaseUrl ?? "https://openrouter.ai/api/v1",
    Model = model,
    ApiKeyEnvVar = preset?.ApiKeyEnvVar ?? "OPENROUTER_API_KEY",
    Temperature = 0.0,
    RequestJsonMode = false,
    DisableReasoning = preset?.DisableReasoning ?? true,
    MaxTokens = preset?.MaxTokens ?? 6000,
    SendTemperature = preset?.SendTemperature ?? true,
    ExtraBody = preset?.ExtraBody
};

var key = settings.ResolveApiKey();
Console.WriteLine($"model    : {settings.Model}");
Console.WriteLine($"scenario : {current.Name}");
Console.WriteLine($"key      : {(string.IsNullOrWhiteSpace(key) ? "MISSING" : $"present ({key.Length} chars)")}");
Console.WriteLine();

Term T(string name, string variable, string type) => new() { Name = name, Variable = variable, Type = type };

var pack = new ContextPack
{
    Equipment = "Motor",
    TargetLanguage = "SCL",
    Scenario = current.Text,
    ReferencedScenarios = profile
        .Where(s => s.Name != current.Name)
        .Select(s => new ScenarioRef { Name = s.Name, Content = s.Text })
        .ToList(),
    Terms =
    [
        T("Operation Mode Auto",     "OpMode_Auto",       "BOOL"),
        T("Operation Mode Manual",   "OpMode_Manual",     "BOOL"),
        T("Manual Start",            "Manual_Start",      "BOOL"),
        T("Manual Stop",             "Manual_Stop",       "BOOL"),
        T("Manual Operation",        "ManualOperation",   "BOOL"),
        T("Trip Signal",             "TripSignal",        "BOOL"),
        T("Operation Signal Run",    "OpSignal_Run",      "BOOL"),
        T("Operation Signal Stop",   "OpSignal_Stop",     "BOOL"),
        T("Command Run",             "Cmd_Run",           "BOOL"),
        T("Command Stop",            "Cmd_Stop",          "BOOL"),
        T("Run Fail Timer",          "RunFailTimer",      "TON"),
        T("Runtime Recorder",        "RuntimeRecorder",   "TIME"),
        T("Trip Alarm",              "TripAlarm",         "BOOL"),
        T("Run Fail Alarm",          "RunFailAlarm",      "BOOL"),
        T("Sequence Auto Fulfilled", "SeqAuto_Fulfilled", "BOOL"),
        T("Run Fail Buzzer",         "RunFailBuzzer",     "BOOL"),
        T("Trip Buzzer",             "TripBuzzer",        "BOOL"),
        T("HMI Acknowledge",         "HMI_Ack",           "BOOL"),
        T("PLC Initialize",          "Sys_plsInit",       "BOOL"),
        T("MCC Trip",                "MCC_Trip",          "BOOL"),
    ],
    Timers = ["RunFailTimer"],
    Functions = ["Run Fail Timer", "Runtime Recorder"],
    Conventions = "Siemens TIA Portal SCL. Comment each block with the sentence it implements."
};

using var translator = new LlmTranslator();
var outcome = await translator.TranslateAsync(pack, settings);

Console.WriteLine($"elapsed : {outcome.ElapsedMs} ms");
Console.WriteLine($"tokens  : {outcome.PromptTokens} in / {outcome.CompletionTokens} out " +
                  $"(total {outcome.PromptTokens + outcome.CompletionTokens})");
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
