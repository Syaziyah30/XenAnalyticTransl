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

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Scenario> _scenarios = new();
    private readonly ObservableCollection<Term> _terms = new();
    private readonly LlmTranslator _translator = new();
    private readonly HttpClient _usageHttp = new();

    private CancellationTokenSource? _cts;
    private TranslationOutcome? _lastOutcome;
    private bool _suppressTextSync;

    public MainWindow()
    {
        InitializeComponent();

        ScenarioList.ItemsSource = _scenarios;
        TermGrid.ItemsSource = _terms;

        foreach (var p in ProviderPresets.All) ProviderBox.Items.Add(p.ProviderName);
        ProviderBox.SelectedIndex = 0;

        BuildReferenceTree();
        LoadSample();
        RefreshKeyStatus();
        _ = RefreshUsageAsync();
    }

    // ---------------------------------------------------------------- credit

    private void RefreshUsage_Click(object sender, RoutedEventArgs e) => _ = RefreshUsageAsync();

    private async Task RefreshUsageAsync()
    {
        if (UsagePct is null) return;

        var settings = CurrentSettings();
        if (!AccountInfo.Supports(settings))
        {
            UsageBar.Value = FreeBar.Value = 0;
            UsagePct.Text = FreePct.Text = "n/a";
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
            SendTemperature = preset.SendTemperature
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

    private void AddTerm_Click(object sender, RoutedEventArgs e) =>
        _terms.Add(new Term { Name = "New term", Variable = "", Value = "" });

    private void DeleteTerm_Click(object sender, RoutedEventArgs e)
    {
        if (TermGrid.SelectedItem is Term t) _terms.Remove(t);
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
        Equipment = "Mtr01",
        TargetLanguage = "SCL",
        Scenario = ScenarioText.Text,
        Terms = _terms.Where(t => !string.IsNullOrWhiteSpace(t.Variable)).ToList(),
        Timers = _terms.Where(t => t.Variable.EndsWith("Timer", StringComparison.OrdinalIgnoreCase))
                       .Select(t => t.Variable).Distinct().ToList(),
        Functions = new List<string> { "PLC Timer", "Time Recorder" },
        Conventions = "Siemens TIA Portal SCL. Comment each block with the sentence it implements."
    };

    private void Render(TranslationOutcome o)
    {
        RawBox.Text = o.RawResponse ?? "";

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
               $"{o.PromptTokens} in / {o.CompletionTokens} out tokens  |  " +
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
        UnknownBox.Text = "-";
        WarningsBox.Text = "-";

        _lastOutcome = null;
        ResultStatus.Text = "Cleared";
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
        var profile = new TreeViewItem { Header = "Profile 1", IsExpanded = true };
        var equip = new TreeViewItem { Header = "Equipment Type", IsExpanded = true };
        equip.Items.Add(new TreeViewItem { Header = "Mtr01", IsSelected = true });
        equip.Items.Add(new TreeViewItem { Header = "Vlv01" });
        profile.Items.Add(equip);

        var fn = new TreeViewItem { Header = "Functions", IsExpanded = true };
        fn.Items.Add(new TreeViewItem { Header = "PLC Timer" });
        fn.Items.Add(new TreeViewItem { Header = "Time Recorder" });

        ReferenceTree.Items.Add(profile);
        ReferenceTree.Items.Add(fn);
        ReferenceTree.Items.Add(new TreeViewItem { Header = "Tags" });
    }

    private void LoadSample()
    {
        _scenarios.Clear();
        _scenarios.Add(new Scenario
        {
            Name = "Operation",
            Text =
                "When in Auto Mode, Manual Operation position will change and retain according with Input Run Signal.\n\n" +
                "During PLC Initializing or MCC Trip On, the Manual Operation position will be in Stop.\n\n" +
                "During PLC Initializing, Run Fail Alarm Timer Setpoint assigned value 10 seconds.\n\n" +
                "During PLC Initializing, Trip Alarm Timer Setpoint assigned value 3 seconds.\n\n" +
                "Trip Alarm Timer will start counting when have Trip Feedback On. If the Trip Alarm Timer elapsed, " +
                "Trip Alarm will turn on. Trip Alarm will off and Trip Alarm Timer will be reset when Trip Feedback is off.\n\n" +
                "When Trip Alarm On, Trip Buzzer will turn on and retain. Trip Buzzer only can be reset by operator."
        });
        _scenarios.Add(new Scenario { Name = "System Handle", Text = "" });
        _scenarios.Add(new Scenario { Name = "S1 - Manual Run Fulfilled", Text = "" });
        _scenarios.Add(new Scenario { Name = "S2 - Auto Run Fulfilled", Text = "" });
        ScenarioList.SelectedIndex = 0;

        _terms.Clear();
        void T(string name, string variable, string? value = null) =>
            _terms.Add(new Term { Name = name, Variable = variable, Value = value });

        T("Auto Mode", "AutoMode");
        T("Manual Operation", "ManualOperation");
        T("Input Run Signal", "InputRunSignal");
        T("PLC Initializing", "Sys_plsInit", "1");
        T("MCC Trip On", "MCC_Trip", "1");
        T("Run Fail Alarm Timer", "RunFailAlarmTimer");
        T("Trip Alarm Timer", "TripAlarmTimer");
        T("Trip Feedback", "TripFeedback");
        T("Trip Alarm", "TripAlarm");
        T("Trip Buzzer", "TripBuzzer");
        T("Operator Reset Buzzer", "OperatorResetBuzzer");

        Status("Sample Mtr01 scenario loaded. Set your key, then click Request AI Translator.");
    }

    protected override void OnClosed(EventArgs e)
    {
        _translator.Dispose();
        _usageHttp.Dispose();
        base.OnClosed(e);
    }
}
