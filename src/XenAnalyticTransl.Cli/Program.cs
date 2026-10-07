using XenAnalyticTransl.AI;

// Headless harness: the same code path and the same data as the Studio, with no UI.
//
//   dotnet run --project src\XenAnalyticTransl.Cli -- list
//   dotnet run --project src\XenAnalyticTransl.Cli -- <model-id>
//   dotnet run --project src\XenAnalyticTransl.Cli -- <model-id> "<scenario>" [motor|valve]

var model = args.Length > 0 ? args[0] : "qwen/qwen3.8-flash";
var wanted = args.Length > 1 ? args[1] : "Operation";
var equipName = args.Length > 2 ? args[2] : "motor";
var equipmentId = equipName.Equals("valve", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

// ---- Statement table, Lipico profile -------------------------------------
var profile = new (int Eq, string Name, string Text)[]
{
    (1, "Operation",
        "When auto run ready or manual run ready and no faults, then will command run."),

    (1, "System Handle",
        "When operation mode in Auto, the Manual Start/Stop position will according with " +
        "Operation Signal Run/Stop.\n\n" +
        "During PLC Initialize or MCC Trip On, the Manual Operation position will be in Stop.\n\n" +
        "During PLC Initialize, Run Fail Timer's delay time Set to 10 seconds.\n\n" +
        "Operation Signal Run time will be recorded by runtime recorder."),

    (1, "Run Failed",
        "When Operation signal Run not received after run fail timer elapsed, turn on Run Fail alarm, " +
        "turn on Run Fail Buzzer once.\n\nWhen HMI Acknowledge, turn off Run Fail Buzzer."),

    (1, "Auto Run Ready",   "When operation mode in Auto and Sequence Auto is fulfilled."),
    (1, "Manual Run Ready", "When operation mode in Manual and manual started."),
    (1, "Faults",           "When trip alarm is on or run fail alarm is on."),

    (1, "Trip",
        "When Trip Signal is On, turn on Trip Alarm, turn on Trip Buzzer once.\n\n" +
        "When HMI Acknowledge, turn off Trip Buzzer."),

    (2, "Operation",
        "When Manual Run Fulfilled or Auto Run Fulfilled, without trip Alarm, then PLC command Run.\n\n" +
        "During motor Run, the running time will be recorded by timer recorder."),

    (2, "System Handle",
        "When in Auto Mode, Manual Operation position will change and retain according with Input Run Signal.\n\n" +
        "During PLC Initializing or MCC Trip On, the Manual Operation position will be in Stop.\n\n" +
        "During PLC Initializing, Run Fail Alarm Timer Setpoint assigned value 10 seconds.\n\n" +
        "During PLC Initializing, Trip Alarm Timer Setpoint assigned value 3 seconds.\n\n" +
        "Trip Alarm Timer will start counting when have Trip Feedback On. If the Trip Alarm Timer elapsed, " +
        "Trip Alarm will turn on. Trip Alarm will off and Trip Alarm Timer will be reset when Trip Feedback is off.\n\n" +
        "When Trip Alarm On, Trip Buzzer will turn on and retain. Trip Buzzer only can be reset by operator."),
};

if (string.Equals(model, "list", StringComparison.OrdinalIgnoreCase))
{
    foreach (var g in profile.GroupBy(p => p.Eq))
    {
        Console.WriteLine(g.Key == 1 ? "Motor:" : "Valve:");
        foreach (var s in g) Console.WriteLine($"  {s.Name}");
    }
    return 0;
}

var pool = profile.Where(p => p.Eq == equipmentId).ToArray();
var current = pool.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase));
if (current.Name is null)
{
    Console.WriteLine($"No scenario \"{wanted}\" for that equipment. Known: {string.Join(", ", pool.Select(p => p.Name))}");
    return 1;
}

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
Console.WriteLine($"model     : {settings.Model}");
Console.WriteLine($"equipment : {(equipmentId == 1 ? "Motor" : "Valve")}");
Console.WriteLine($"scenario  : {current.Name}");
Console.WriteLine($"key       : {(string.IsNullOrWhiteSpace(key) ? "MISSING" : $"present ({key.Length} chars)")}");
Console.WriteLine();

// ---- StatementProperty + PropertyTerm ------------------------------------
// PlcVariable belongs to the property, PlcValue to the state: Operation Mode is
// blnMA, with Auto = 0 and Manual = 1.
var terms = new List<Term>();
void State(string prop, string variable, string type, string state, string value) =>
    terms.Add(new Term { Name = $"{prop} {state}", Variable = variable, Type = type, Value = value });
void Plain(string prop, string variable, string type) =>
    terms.Add(new Term { Name = prop, Variable = variable, Type = type });

State("Operation Mode", "blnMA", "INT", "Auto", "0");
State("Operation Mode", "blnMA", "INT", "Manual", "1");
State("MCC Trip", "New Instance", "BOOL", "Trip Off", "0");
State("MCC Trip", "New Instance", "BOOL", "Trip On", "1");
Plain("VOP", "New Instance", "BOOL");
State("Manual", "blnMC", "INT", "Start", "1");
State("Manual", "blnMC", "INT", "Stop", "0");
State("Trip Signal", "blnTRP", "INT", "On", "1");
State("Trip Signal", "blnTRP", "INT", "Off", "0");
State("Operation Signal", "blnRUN", "INT", "Run", "1");
State("Operation Signal", "blnRUN", "INT", "Stop", "0");
State("Command", "blnOut", "INT", "Run", "1");
State("Command", "blnOut", "INT", "Stop", "0");
Plain("PLC Initialize", "PLCInit", "BOOL");
Plain("Run Fail Timer", "tmrRFAL", "TON");
Plain("Runtime recorder", "RFTimer", "TIME");
State("Trip Alarm", "New Instance", "INT", "On", "1");
State("Trip Alarm", "New Instance", "INT", "Off", "0");
State("Run Fail Alarm", "New Instance", "INT", "On", "1");
State("Run Fail Alarm", "New Instance", "INT", "Off", "0");
State("Sequence Auto", "blnAUT", "INT", "Fulfilled", "1");
State("Sequence Auto", "blnAUT", "INT", "Unfulfilled", "0");
State("Run Fail Buzzer", "New Instance", "INT", "On", "1");
State("Run Fail Buzzer", "New Instance", "INT", "Off", "0");
State("Trip Buzzer", "New Instance", "INT", "On", "1");
State("Trip Buzzer", "New Instance", "INT", "Off", "0");
Plain("HMI Acknowledge", "SysAck", "BOOL");

var pack = new ContextPack
{
    Equipment = equipmentId == 1 ? "Motor" : "Valve",
    TargetLanguage = "SCL",
    Scenario = current.Text,
    ReferencedScenarios = pool
        .Where(s => s.Name != current.Name)
        .Select(s => new ScenarioRef { Name = s.Name, Content = s.Text })
        .ToList(),
    Terms = terms,
    Timers = terms.Where(t => t.Type == "TON").Select(t => t.Variable).Distinct().ToList(),
    Functions = ["HMI", "PLC", "Delay Timer", "Time Recorder"],
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
Console.WriteLine(outcome.UnknownIdentifiers.Count == 0 ? "none" : string.Join(", ", outcome.UnknownIdentifiers));
Console.WriteLine();
Console.WriteLine("=== MODEL WARNINGS ===");
Console.WriteLine(r.Warnings.Count == 0 ? "none" : string.Join("\n", r.Warnings.Select(w => "- " + w)));
return 0;
