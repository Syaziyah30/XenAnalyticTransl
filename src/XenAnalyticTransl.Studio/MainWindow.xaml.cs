using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using XenAnalyticTransl.AI;

namespace XenAnalyticTransl.Studio;

public sealed class Scenario
{
    public string Name { get; set; } = "New scenario";
    public string Text { get; set; } = "";
    public override string ToString() => Name;
}

/// <summary>
/// One row of the Properties tree, mirroring the production app: a property
/// (Operation Mode) holding its states (Auto, Manual). A leaf - or a childless
/// property like Run Fail Timer - is what carries a PLC variable.
/// </summary>
public sealed class PropertyNode : System.ComponentModel.INotifyPropertyChanged
{
    private string _variable = "";
    private string _type = "";

    public string Name { get; set; } = "";
    public ObservableCollection<PropertyNode> Children { get; } = new();
    public PropertyNode? Parent { get; set; }

    public string Variable
    {
        get => _variable;
        set { _variable = value; Changed(nameof(Variable)); Changed(nameof(VariableHint)); }
    }

    public string Type
    {
        get => _type;
        set { _type = value; Changed(nameof(Type)); Changed(nameof(VariableHint)); }
    }

    /// <summary>Parent properties are bold, like the production tree.</summary>
    public string Weight => Children.Count > 0 ? "SemiBold" : "Normal";

    /// <summary>Grey hint beside the name so the mapping is visible without clicking.</summary>
    public string VariableHint =>
        string.IsNullOrWhiteSpace(Variable) ? "" :
        string.IsNullOrWhiteSpace(Type) ? Variable : $"{Variable} : {Type}";

    /// <summary>The full term name sent to the model: "Operation Mode Auto".</summary>
    public string QualifiedName =>
        Parent is null ? Name : $"{Parent.Name} {Name}";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Scenario> _scenarios = new();
    private readonly ObservableCollection<PropertyNode> _properties = new();
    private bool _suppressAssocSync;
    private readonly LlmTranslator _translator = new();
    private readonly HttpClient _usageHttp = new();

    private CancellationTokenSource? _cts;
    private TranslationOutcome? _lastOutcome;
    private bool _suppressTextSync;

    // Live elapsed clock while a request is in flight.
    private readonly System.Windows.Threading.DispatcherTimer _tick = new()
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };
    private readonly System.Diagnostics.Stopwatch _runClock = new();

    // Local spend tally, for providers that publish no balance endpoint.
    private int _sessionRuns;
    private decimal _sessionUsd;

    public MainWindow()
    {
        InitializeComponent();

        ScenarioList.ItemsSource = _scenarios;
        PropertyTree.ItemsSource = _properties;

        foreach (var p in ProviderPresets.All) ProviderBox.Items.Add(p.ProviderName);
        ProviderBox.SelectedIndex = 0;

        _tick.Tick += (_, _) => TimeText.Text = $"{_runClock.Elapsed.TotalSeconds:0.0} s";

        BuildReferenceTree();
        LoadSample();
        RefreshKeyStatus();
        _ = RefreshUsageAsync();
    }

    /// <summary>Starts the on-screen clock so a long call never looks like a hang.</summary>
    private void StartRunClock()
    {
        _runClock.Restart();
        MetricsPanel.Visibility = Visibility.Visible;
        TimeText.Text = "0.0 s";
        TimeText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x12, 0x44, 0x7F));
        MetricsText.Text = $"{ModelBox.Text.Trim()}  |  running...";
        _tick.Start();
    }

    private void StopRunClock()
    {
        _tick.Stop();
        _runClock.Stop();
    }

    // ---------------------------------------------------------------- credit

    private void RefreshUsage_Click(object sender, RoutedEventArgs e) => _ = RefreshUsageAsync();

    private async Task RefreshUsageAsync()
    {
        if (UsagePct is null) return;

        var settings = CurrentSettings();

        // Anthropic and most direct providers publish no balance endpoint - their usage
        // sits behind a separate admin credential. Show what this session has spent instead,
        // worked out from token counts and the published rates.
        if (!AccountInfo.Supports(settings))
        {
            UsageBar.Value = FreeBar.Value = 0;
            UsagePct.Text = _sessionRuns == 0
                ? "no runs yet"
                : $"~${_sessionUsd:0.000} this session";
            FreePct.Text = _sessionRuns == 0
                ? "provider reports no balance"
                : $"{_sessionRuns} run{(_sessionRuns == 1 ? "" : "s")}";
            UsagePct.Foreground = FreePct.Foreground = System.Windows.Media.Brushes.Black;
            return;
        }

        UsagePct.Text = FreePct.Text = "...";
        var usage = await AccountInfo.FetchAsync(settings, _usageHttp);

        if (!usage.Ok)
        {
            UsageBar.Value = FreeBar.Value = 0;
            UsagePct.Text = FreePct.Text = "?";
            Status(usage.Error!);
            return;
        }

        UsageBar.Value = usage.UsedFraction;
        UsagePct.Text = AccountUsage.Percent(usage.UsedFraction);
        UsageBar.ToolTip = $"${usage.Used:0.000} of ${usage.Limit:0.00} used, ${usage.Remaining:0.000} left";

        FreeBar.Value = usage.FreeUsedFraction;
        FreePct.Text = AccountUsage.Percent(usage.FreeUsedFraction);
        FreeBar.ToolTip = $"{usage.FreeRequestsUsed} of {usage.FreeRequestsLimit} free requests used today";

        // Warn before the key stops working rather than after.
        var red = System.Windows.Media.Brushes.Firebrick;
        var normal = System.Windows.Media.Brushes.Black;
        UsagePct.Foreground = usage.UsedFraction > 0.8 ? red : normal;
        FreePct.Foreground = usage.FreeUsedFraction > 0.8 ? red : normal;
    }

    // ---------------------------------------------------------------- provider

    private LlmSettings CurrentSettings()
    {
        var preset = ProviderPresets.All[Math.Max(0, ProviderBox.SelectedIndex)];
        return new LlmSettings
        {
            ProviderName = preset.ProviderName,
            ApiKeyEnvVar = preset.ApiKeyEnvVar,
            BaseUrl = BaseUrlBox.Text.Trim(),
            Model = ModelBox.Text.Trim(),
            Temperature = 0.1,
            RequestJsonMode = false,
            // Per-model: some endpoints require reasoning and reject a request that
            // tries to switch it off, others waste the whole budget thinking.
            DisableReasoning = preset.DisableReasoning,
            MaxTokens = preset.MaxTokens,
            SendTemperature = preset.SendTemperature,
            ExtraBody = preset.ExtraBody,
            InputUsdPerM = preset.InputUsdPerM,
            OutputUsdPerM = preset.OutputUsdPerM
        };
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedIndex < 0 || BaseUrlBox is null) return;
        var preset = ProviderPresets.All[ProviderBox.SelectedIndex];
        BaseUrlBox.Text = preset.BaseUrl;
        ModelBox.Text = preset.Model;
        RefreshKeyStatus();
        _ = RefreshUsageAsync();
    }

    private void CheckKey_Click(object sender, RoutedEventArgs e) => RefreshKeyStatus();

    private void RefreshKeyStatus()
    {
        if (KeyStatusBtn is null) return;
        var s = CurrentSettings();
        var key = s.ResolveApiKey();

        // WPF treats "_" in button content as an access-key marker and hides it,
        // so the variable name must be escaped to display correctly.
        var shownVar = s.ApiKeyEnvVar.Replace("_", "__");

        if (string.IsNullOrWhiteSpace(key))
        {
            KeyStatusBtn.Content = $"Key: MISSING ({shownVar})";
            KeyStatusBtn.ToolTip = $"Set the environment variable {s.ApiKeyEnvVar}, then restart this app.";
            Status($"No API key. Run:  setx {s.ApiKeyEnvVar} \"your-key\"   then restart the app.");
        }
        else
        {
            KeyStatusBtn.Content = $"Key: OK ({Mask(key)})";
            KeyStatusBtn.ToolTip = $"Read from {s.ApiKeyEnvVar}. The key is never stored in this app or the repo.";
            Status("Ready.");
        }
    }

    private static string Mask(string key) =>
        key.Length <= 8 ? "****" : key[..4] + "..." + key[^4..];

    // ---------------------------------------------------------------- scenarios

    private void ScenarioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScenarioList.SelectedItem is not Scenario s) return;
        _suppressTextSync = true;
        ScenarioText.Text = s.Text;
        _suppressTextSync = false;
    }

    private void ScenarioText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextSync) return;
        if (ScenarioList.SelectedItem is Scenario s) s.Text = ScenarioText.Text;
    }

    private void AddScenario_Click(object sender, RoutedEventArgs e)
    {
        var s = new Scenario { Name = $"Scenario {_scenarios.Count + 1}", Text = "" };
        _scenarios.Add(s);
        ScenarioList.SelectedItem = s;
    }

    private void DeleteScenario_Click(object sender, RoutedEventArgs e)
    {
        if (ScenarioList.SelectedItem is Scenario s) _scenarios.Remove(s);
    }

    // ---------------------------------------------------------------- properties tree

    private void AddProperty_Click(object sender, RoutedEventArgs e)
    {
        var n = new PropertyNode { Name = "New property" };
        _properties.Add(n);
        Status("Property added. Select it and fill in the PLC variable below.");
    }

    private void AddState_Click(object sender, RoutedEventArgs e)
    {
        if (PropertyTree.SelectedItem is not PropertyNode sel)
        {
            Status("Select a property first, then Add State.");
            return;
        }
        // A state always hangs off a property, never off another state.
        var parent = sel.Parent ?? sel;
        parent.Children.Add(new PropertyNode { Name = "New state", Parent = parent });
    }

    private void DeleteNode_Click(object sender, RoutedEventArgs e)
    {
        if (PropertyTree.SelectedItem is not PropertyNode sel) return;
        if (sel.Parent is null) _properties.Remove(sel);
        else sel.Parent.Children.Remove(sel);
    }

    private void PropertyTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        _suppressAssocSync = true;
        if (e.NewValue is PropertyNode n)
        {
            AssocVariable.Text = n.Variable;
            AssocType.Text = n.Type;
            AssocVariable.IsEnabled = AssocType.IsEnabled = true;
        }
        else
        {
            AssocVariable.Text = AssocType.Text = "";
            AssocVariable.IsEnabled = AssocType.IsEnabled = false;
        }
        _suppressAssocSync = false;
    }

    private void Assoc_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressAssocSync) return;
        if (PropertyTree.SelectedItem is not PropertyNode n) return;
        n.Variable = AssocVariable.Text.Trim();
        n.Type = AssocType.Text.Trim();
    }

    /// <summary>Flattens the tree into the terms the model is allowed to use.</summary>
    private List<Term> CollectTerms()
    {
        var terms = new List<Term>();

        void Walk(PropertyNode n)
        {
            if (!string.IsNullOrWhiteSpace(n.Variable))
                terms.Add(new Term { Name = n.QualifiedName, Variable = n.Variable, Type = n.Type });
            foreach (var c in n.Children) Walk(c);
        }

        foreach (var p in _properties) Walk(p);
        return terms;
    }

    private void LoadSample_Click(object sender, RoutedEventArgs e) => LoadSample();

    // ---------------------------------------------------------------- translate

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        var pack = BuildContextPack();

        if (string.IsNullOrWhiteSpace(pack.Scenario))
        {
            Status("Nothing to translate - the scenario is empty.");
            return;
        }

        ContextBox.Text = PromptBuilder.ToJson(pack);

        SetBusy(true);
        ResultStatus.Text = "Generating...";
        StartRunClock();
        Status($"Calling {ModelBox.Text}...");

        _cts = new CancellationTokenSource();
        try
        {
            var outcome = await _translator.TranslateAsync(pack, CurrentSettings(), _cts.Token);
            _lastOutcome = outcome;
            Render(outcome);
        }
        catch (Exception ex)
        {
            ResultStatus.Text = "Failed";
            Status($"Unexpected error: {ex.Message}");
        }
        finally
        {
            StopRunClock();
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            _ = RefreshUsageAsync();   // spend changed - show it
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        TranslateBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private ContextPack BuildContextPack() => new()
    {
        Equipment = "Motor",
        TargetLanguage = "SCL",
        Scenario = ScenarioText.Text,
        // Every other scenario goes along, so references like "auto run ready" can be
        // resolved and flattened instead of guessed at.
        ReferencedScenarios = _scenarios
            .Where(s => !ReferenceEquals(s, ScenarioList.SelectedItem)
                        && !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => new ScenarioRef { Name = s.Name, Content = s.Text })
            .ToList(),
        Terms = CollectTerms(),
        Timers = CollectTerms()
            .Where(t => string.Equals(t.Type, "TON", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Variable).Distinct().ToList(),
        Functions = new List<string> { "PLC Timer", "Time Recorder" },
        Conventions = "Siemens TIA Portal SCL. Comment each block with the sentence it implements."
    };

    /// <summary>Shows the numbers a model is judged on, where they cannot scroll off screen.</summary>
    private void ShowMetrics(TranslationOutcome o)
    {
        MetricsPanel.Visibility = Visibility.Visible;
        TimeText.Text = $"{o.ElapsedMs / 1000.0:0.0} s";

        if (o.Ok)
        {
            var s = CurrentSettings();
            _sessionRuns++;
            _sessionUsd += o.EstimateCostUsd(s.InputUsdPerM, s.OutputUsdPerM);
        }

        var model = o.Model.Contains('/') ? o.Model[(o.Model.IndexOf('/') + 1)..] : o.Model;
        var total = o.PromptTokens + o.CompletionTokens;
        var tokens = total > 0
            ? $"  |  {o.PromptTokens:N0} in / {o.CompletionTokens:N0} out  |  total {total:N0} tokens"
            : "";
        MetricsText.Text = $"{model}{tokens}";

        // Slow runs are worth noticing when comparing models.
        TimeText.Foreground = o.ElapsedMs > 30000
            ? System.Windows.Media.Brushes.Firebrick
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x12, 0x44, 0x7F));
    }

    private void Render(TranslationOutcome o)
    {
        RawBox.Text = o.RawResponse ?? "";
        RequestBox.Text = o.RequestBody ?? "";
        ShowMetrics(o);

        if (!o.Ok)
        {
            ResultStatus.Text = "Failed";
            SclBox.Text = "";
            ExplanationBox.Text = "";
            UnknownBox.Text = "-";
            WarningsBox.Text = "-";
            Status($"FAILED: {o.Error}");
            return;
        }

        var r = o.Result!;
        SclBox.Text = r.Code;
        ExplanationBox.Text = r.Explanation;

        UnknownBox.Text = o.UnknownIdentifiers.Count == 0
            ? "None - every identifier exists in the tag table."
            : string.Join(Environment.NewLine, o.UnknownIdentifiers.Select(u => "  " + u));

        WarningsBox.Text = r.Warnings.Count == 0
            ? "None reported."
            : string.Join(Environment.NewLine, r.Warnings.Select(w => "  " + w));

        var verdict = o.UnknownIdentifiers.Count == 0 ? "Generated successfully" : "Generated WITH UNKNOWN IDENTIFIERS";
        ResultStatus.Text = verdict;

        Status($"{verdict}  |  {o.Model}  |  {o.ElapsedMs} ms  |  " +
               $"{o.PromptTokens:N0} in / {o.CompletionTokens:N0} out  |  " +
               $"total {o.PromptTokens + o.CompletionTokens:N0} tokens  |  " +
               $"{o.UnknownIdentifiers.Count} unknown, {r.Warnings.Count} warnings");
    }

    // ---------------------------------------------------------------- output

    private void CopyScl_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SclBox.Text)) { Status("Nothing to copy."); return; }
        Clipboard.SetText(SclBox.Text);
        Status("SCL copied to clipboard. Paste it into TIA Portal and try to compile it.");
    }

    /// <summary>
    /// Empties the result tabs only. The scenario and the term table are left alone -
    /// clearing those would throw away work, which is not what this button promises.
    /// </summary>
    private void ClearResult_Click(object sender, RoutedEventArgs e)
    {
        SclBox.Text = "";
        ExplanationBox.Text = "";
        ContextBox.Text = "";
        RawBox.Text = "";
        RequestBox.Text = "";
        UnknownBox.Text = "-";
        WarningsBox.Text = "-";

        _lastOutcome = null;
        ResultStatus.Text = "Cleared";
        MetricsPanel.Visibility = Visibility.Collapsed;
        Status("Result cleared. Scenario and terms are unchanged.");
    }

    private void SaveRun_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutcome is null) { Status("Run a translation first."); return; }

        var dir = Path.Combine(RepoRoot(), "Reference", "runs");
        Directory.CreateDirectory(dir);

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = dir,
            FileName = $"run-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON|*.json"
        };
        if (dlg.ShowDialog() != true) return;

        var record = new
        {
            savedAt = DateTime.Now,
            provider = _lastOutcome.Provider,
            model = _lastOutcome.Model,
            elapsedMs = _lastOutcome.ElapsedMs,
            promptTokens = _lastOutcome.PromptTokens,
            completionTokens = _lastOutcome.CompletionTokens,
            contextPack = BuildContextPack(),
            result = _lastOutcome.Result,
            unknownIdentifiers = _lastOutcome.UnknownIdentifiers,
            error = _lastOutcome.Error
        };

        File.WriteAllText(dlg.FileName,
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        Status($"Saved {dlg.FileName}");
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    private void Status(string msg) => StatusBar.Text = msg;

    // ---------------------------------------------------------------- sample data

    private void BuildReferenceTree()
    {
        var profile = new TreeViewItem { Header = "Lipico", IsExpanded = true };
        var equip = new TreeViewItem { Header = "Equipment Type", IsExpanded = true };
        equip.Items.Add(new TreeViewItem { Header = "Motor", IsSelected = true });
        equip.Items.Add(new TreeViewItem { Header = "Valve" });
        profile.Items.Add(equip);

        var fn = new TreeViewItem { Header = "Function", IsExpanded = true };
        fn.Items.Add(new TreeViewItem { Header = "Run Fail Timer" });
        fn.Items.Add(new TreeViewItem { Header = "Runtime Recorder" });

        ReferenceTree.Items.Add(profile);
        ReferenceTree.Items.Add(fn);
        ReferenceTree.Items.Add(new TreeViewItem { Header = "Tags" });
    }

    private void LoadSample()
    {
        // The Motor profile as it stands in the real app. These scenarios reference each
        // other by name, which is what the flattening rules in the system prompt act on.
        _scenarios.Clear();
        void S(string name, string text) => _scenarios.Add(new Scenario { Name = name, Text = text });

        S("Operation",
            "When auto run ready or manual run ready and no faults, then will command run.");

        S("System Handle",
            "When operation mode in Auto, the Manual Start/Stop position will according with " +
            "Operation Signal Run/Stop.\n\n" +
            "During PLC Initialize or MCC Trip On, the Manual Operation position will be in Stop.\n\n" +
            "During PLC Initialize, Run Fail Timer's delay time Set to 10 seconds.\n\n" +
            "Operation Signal Run time will be recorded by runtime recorder.");

        S("Run Failed",
            "When Operation signal Run not received after run fail timer elapsed, turn on Run Fail alarm, " +
            "turn on Run Fail Buzzer once. When HMI Acknowledge, turn off Run Fail Buzzer.");

        S("Auto run ready",
            "When operation mode in Auto and Sequence Auto is fulfilled.");

        S("Manual run ready",
            "When operation mode in Manual and manual started.");

        S("Faults",
            "When trip alarm is on or run fail alarm is on.");

        S("Trip",
            "When Trip Signal is On, turn on Trip Alarm, turn on Trip Buzzer once. " +
            "When HMI Acknowledge, turn off Trip Buzzer.");

        ScenarioList.SelectedIndex = 0;

        // The Properties tree of the Motor profile: a property, then its states.
        _properties.Clear();

        PropertyNode P(string name, string variable = "", string type = "")
        {
            var n = new PropertyNode { Name = name, Variable = variable, Type = type };
            _properties.Add(n);
            return n;
        }
        void St(PropertyNode parent, string name, string variable, string type = "BOOL") =>
            parent.Children.Add(new PropertyNode
            { Name = name, Variable = variable, Type = type, Parent = parent });

        var opMode = P("Operation Mode");
        St(opMode, "Auto",   "OpMode_Auto");
        St(opMode, "Manual", "OpMode_Manual");

        var manual = P("Manual");
        St(manual, "Start", "Manual_Start");
        St(manual, "Stop",  "Manual_Stop");

        var tripSig = P("Trip Signal");
        St(tripSig, "On",  "TripSignal");
        St(tripSig, "Off", "TripSignal_Off");

        var opSig = P("Operation Signal");
        St(opSig, "Run",  "OpSignal_Run");
        St(opSig, "Stop", "OpSignal_Stop");

        var cmd = P("Command");
        St(cmd, "Run",  "Cmd_Run");
        St(cmd, "Stop", "Cmd_Stop");

        P("Run Fail Timer",   "RunFailTimer",    "TON");
        P("Runtime recorder", "RuntimeRecorder", "TIME");

        var tripAlarm = P("Trip Alarm");
        St(tripAlarm, "On",  "TripAlarm");
        St(tripAlarm, "Off", "TripAlarm_Off");

        var runFailAlarm = P("Run Fail Alarm");
        St(runFailAlarm, "On",  "RunFailAlarm");
        St(runFailAlarm, "Off", "RunFailAlarm_Off");

        var seqAuto = P("Sequence Auto");
        St(seqAuto, "Fulfilled",   "SeqAuto_Fulfilled");
        St(seqAuto, "Unfulfilled", "SeqAuto_Unfulfilled");

        var runFailBuzzer = P("Run Fail Buzzer");
        St(runFailBuzzer, "On",  "RunFailBuzzer");
        St(runFailBuzzer, "Off", "RunFailBuzzer_Off");

        var tripBuzzer = P("Trip Buzzer");
        St(tripBuzzer, "On",  "TripBuzzer");
        St(tripBuzzer, "Off", "TripBuzzer_Off");

        P("HMI Acknowledge", "HMI_Ack",     "BOOL");
        P("PLC Initialize",  "Sys_plsInit", "BOOL");
        P("MCC Trip",        "MCC_Trip",    "BOOL");

        Status("Motor profile loaded - 7 scenarios. Pick one, then click Request AI Translator.");
    }

    protected override void OnClosed(EventArgs e)
    {
        _translator.Dispose();
        _usageHttp.Dispose();
        base.OnClosed(e);
    }
}






