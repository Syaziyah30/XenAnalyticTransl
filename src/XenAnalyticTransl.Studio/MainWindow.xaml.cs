using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Documents;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using XenAnalyticTransl.AI;

namespace XenAnalyticTransl.Studio;

public sealed class Scenario : System.ComponentModel.INotifyPropertyChanged
{
    private TranslationOutcome? _outcome;

    public string Name { get; set; } = "New scenario";
    public string Text { get; set; } = "";

    /// <summary>The last translation of THIS scenario, kept so it can be browsed again.</summary>
    public TranslationOutcome? Outcome
    {
        get => _outcome;
        set
        {
            _outcome = value;
            Changed(nameof(Outcome)); Changed(nameof(Status)); Changed(nameof(StatusBrush));
        }
    }

    /// <summary>Marker beside the name in the list.</summary>
    public string Status => _outcome is null ? ""
        : !_outcome.Ok ? "✗"                                  // cross - failed
        : _outcome.UnknownIdentifiers.Count > 0 ? "!"              // generated, but unknown names
        : "✓";                                                // tick - clean

    public System.Windows.Media.Brush StatusBrush => _outcome is null
        ? System.Windows.Media.Brushes.Transparent
        : !_outcome.Ok ? System.Windows.Media.Brushes.Firebrick
        : _outcome.UnknownIdentifiers.Count > 0 ? System.Windows.Media.Brushes.DarkGoldenrod
        : System.Windows.Media.Brushes.SeaGreen;

    public override string ToString() => Name;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
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

    private string _value = "";

    public string Name { get; set; } = "";
    public ObservableCollection<PropertyNode> Children { get; } = new();
    public PropertyNode? Parent { get; set; }

    /// <summary>
    /// PlcValue. In the real data the variable belongs to the PROPERTY and the value to
    /// the STATE: Operation Mode is blnMA, with Auto = 0 and Manual = 1. So a state is a
    /// value of its parent's variable, not a variable of its own.
    /// </summary>
    public string Value
    {
        get => _value;
        set { _value = value; Changed(nameof(Value)); Changed(nameof(VariableHint)); }
    }

    /// <summary>The variable a state is tested against - its own, else its parent's.</summary>
    public string EffectiveVariable =>
        !string.IsNullOrWhiteSpace(Variable) ? Variable : Parent?.Variable ?? "";

    public string EffectiveType =>
        !string.IsNullOrWhiteSpace(Type) ? Type : Parent?.Type ?? "";

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

    /// <summary>
    /// Same colour code as the scenario editor, so the tree and the text agree:
    /// a property is dark green, one of its states is green.
    /// </summary>
    public System.Windows.Media.Brush NodeBrush =>
        Children.Count > 0 || Parent is null
            ? ScenarioHighlighter.PropertyBrush
            : ScenarioHighlighter.TermBrush;

    /// <summary>Grey hint beside the name so the mapping is visible without clicking.</summary>
    public string VariableHint
    {
        get
        {
            if (Children.Count > 0 || Parent is null)
                return string.IsNullOrWhiteSpace(Variable) ? ""
                     : string.IsNullOrWhiteSpace(Type) ? Variable : $"{Variable} : {Type}";
            // a state reads as "blnMA = 0"
            var v = EffectiveVariable;
            if (string.IsNullOrWhiteSpace(v)) return "";
            return string.IsNullOrWhiteSpace(Value) ? v : $"{v} = {Value}";
        }
    }

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

    /// <summary>Every statement in the profile, from the Statement table.</summary>
    private readonly List<(int EquipmentId, string Name, string Text)> _allScenarios = new();
    private int _equipmentId = 1;   // 1 = Motor, 2 = Valve
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

    /// <summary>
    /// Elapsed time for the WHOLE batch. The metrics bar reports this rather than the
    /// selected scenario's own time, so browsing results after a run does not keep
    /// changing the number being compared between models. 0 means no batch has finished.
    /// </summary>
    private long _batchMs;

    // Re-colouring on every keystroke fights the caret; wait for a pause.
    private readonly System.Windows.Threading.DispatcherTimer _recolour = new()
    { Interval = TimeSpan.FromMilliseconds(400) };

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
        _recolour.Tick += (_, _) => { _recolour.Stop(); Recolour(); };

        BuildReferenceTree();
        LoadSample();
        RefreshKeyStatus();
        _ = RefreshUsageAsync();

        // Colour once everything is loaded and the window is up. Doing it only from the
        // selection event is fragile - the event does not fire if the index is already 0.
        Dispatcher.BeginInvoke(new Action(Recolour),
            System.Windows.Threading.DispatcherPriority.Loaded);
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
            OutputUsdPerM = preset.OutputUsdPerM,
            TimeoutSeconds = preset.TimeoutSeconds
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

    // ---------------------------------------------------------------- scenario text

    /// <summary>The scenario as plain text, independent of the colouring.</summary>
    private string ScenarioPlainText
    {
        get => new TextRange(ScenarioText.Document.ContentStart,
                             ScenarioText.Document.ContentEnd).Text.TrimEnd('\r', '\n');
        set
        {
            _suppressTextSync = true;
            ScenarioText.Document = ScenarioHighlighter.Build(value, HighlightPhrases(), 14);
            _suppressTextSync = false;
        }
    }

    /// <summary>
    /// Everything the editor should colour: scenario names, property names, state names.
    /// Rebuilt on demand so renaming a property recolours the text.
    /// </summary>
    private List<ScenarioHighlighter.Phrase> HighlightPhrases()
    {
        var list = new List<ScenarioHighlighter.Phrase>();

        foreach (var s in _scenarios)
            list.Add(new(s.Name, TokenKind.Scenario));

        void Walk(PropertyNode n)
        {
            list.Add(new(n.Name, n.Children.Count > 0 || n.Parent is null
                ? TokenKind.Property : TokenKind.Term));
            foreach (var c in n.Children) Walk(c);
        }
        foreach (var p in _properties) Walk(p);

        return list;
    }

    private void ScenarioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScenarioList.SelectedItem is not Scenario s) return;
        ScenarioPlainText = s.Text;
        ShowScenarioResult(s);     // every result tab follows the selection too
    }

    private void ScenarioText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextSync) return;
        if (ScenarioList.SelectedItem is Scenario s) s.Text = ScenarioPlainText;

        // Re-colour once typing pauses - doing it per keystroke fights the caret.
        _recolour.Stop();
        _recolour.Start();
    }

    /// <summary>Re-applies the colours, putting the caret back where it was.</summary>
    private void Recolour()
    {
        if (ScenarioList.SelectedItem is not Scenario s) return;

        var offset = CaretOffset();
        _suppressTextSync = true;
        ScenarioText.Document = ScenarioHighlighter.Build(s.Text, HighlightPhrases(), 14);
        _suppressTextSync = false;
        RestoreCaret(offset);
    }

    private int CaretOffset() =>
        new TextRange(ScenarioText.Document.ContentStart, ScenarioText.CaretPosition).Text.Length;

    private void RestoreCaret(int offset)
    {
        var pos = ScenarioText.Document.ContentStart;
        var seen = 0;
        while (pos is not null)
        {
            if (pos.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var run = pos.GetTextInRun(LogicalDirection.Forward).Length;
                if (seen + run >= offset)
                {
                    ScenarioText.CaretPosition = pos.GetPositionAtOffset(offset - seen) ?? pos;
                    return;
                }
                seen += run;
            }
            pos = pos.GetNextContextPosition(LogicalDirection.Forward);
        }
        ScenarioText.CaretPosition = ScenarioText.Document.ContentEnd;
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
            // A property with states contributes nothing itself - its states carry the
            // value that distinguishes them. A property with no states is a term in itself.
            if (n.Children.Count == 0 && !string.IsNullOrWhiteSpace(n.EffectiveVariable))
            {
                terms.Add(new Term
                {
                    Name = n.QualifiedName,
                    Variable = n.EffectiveVariable,
                    Type = n.EffectiveType,
                    Value = string.IsNullOrWhiteSpace(n.Value) ? null : n.Value
                });
            }
            foreach (var c in n.Children) Walk(c);
        }

        foreach (var p in _properties) Walk(p);
        return terms;
    }

    private void LoadSample_Click(object sender, RoutedEventArgs e) => LoadSample();

    // ---------------------------------------------------------------- translate

    /// <summary>
    /// Translates EVERY scenario of the selected equipment, one after another, and keeps
    /// each result on its scenario. Clicking a scenario afterwards shows its own code,
    /// so the whole profile can be reviewed without re-running anything.
    /// </summary>
    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        var todo = _scenarios.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (todo.Count == 0)
        {
            Status("Nothing to translate - every scenario is empty.");
            return;
        }

        var settings = CurrentSettings();
        SetBusy(true);
        _cts = new CancellationTokenSource();

        var done = 0; var failed = 0; var flagged = 0;
        var batch = System.Diagnostics.Stopwatch.StartNew();
        _batchMs = 0;           // fall back to per-scenario time until the batch lands

        try
        {
            foreach (var s in todo)
            {
                if (_cts.IsCancellationRequested) break;

                done++;
                ScenarioList.SelectedItem = s;          // follow along in the list
                ResultStatus.Text = $"Generating {done} of {todo.Count}...";
                Status($"[{done}/{todo.Count}] {s.Name} - calling {settings.Model}...");
                StartRunClock();

                TranslationOutcome outcome;
                try
                {
                    outcome = await _translator.TranslateAsync(BuildContextPack(s), settings, _cts.Token);
                }
                catch (Exception ex)
                {
                    Status($"[{done}/{todo.Count}] {s.Name} - unexpected error: {ex.Message}");
                    break;
                }
                finally
                {
                    StopRunClock();
                }

                s.Outcome = outcome;
                if (!outcome.Ok) failed++;
                else if (outcome.UnknownIdentifiers.Count > 0) flagged++;

                ShowScenarioResult(s);                  // render as each one lands
            }
        }
        finally
        {
            batch.Stop();
            _batchMs = (long)batch.Elapsed.TotalMilliseconds;
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);

            // Repaint the bar so it shows the batch total instead of whichever
            // scenario happened to finish last.
            if (ScenarioList.SelectedItem is Scenario sel && sel.Outcome is not null)
                ShowMetrics(sel.Outcome);

            _ = RefreshUsageAsync();
        }

        var ok = done - failed - flagged;
        var summary = $"Done: {done} scenario(s) in {batch.Elapsed.TotalSeconds:0.0}s  |  " +
                      $"{ok} clean, {flagged} with unknown identifiers, {failed} failed";

        // When everything failed it is almost always one cause - say what it was rather
        // than making the reader click a scenario to find out.
        var firstError = todo.FirstOrDefault(s => s.Outcome is { Ok: false })?.Outcome?.Error;
        if (failed == done && firstError is not null)
            Status($"{summary}  |  ALL FAILED: {firstError}");
        else
            Status($"{summary}  |  click a scenario to see its code.");
    }

    /// <summary>Shows a scenario's stored result, or clears the panel if it has none.</summary>
    private void ShowScenarioResult(Scenario s)
    {
        ContextBox.Text = PromptBuilder.ToJson(BuildContextPack(s));

        if (s.Outcome is null)
        {
            SclBox.Text = ExplanationBox.Text = RawBox.Text = RequestBox.Text = "";
            UnknownBox.Text = WarningsBox.Text = "-";
            ResultStatus.Text = "Not translated yet";
            MetricsPanel.Visibility = Visibility.Collapsed;
            _lastOutcome = null;
            return;
        }

        _lastOutcome = s.Outcome;
        Render(s.Outcome);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        TranslateBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    /// <summary>
    /// The context pack for one scenario. Defaults to the selected one, but the batch
    /// translator passes each in turn, so it must not read the editor.
    /// </summary>
    private ContextPack BuildContextPack(Scenario? target = null)
    {
        var s = target ?? ScenarioList.SelectedItem as Scenario;
        var terms = CollectTerms();

        return new ContextPack
        {
            Equipment = _equipmentId == 1 ? "Motor" : "Valve",
            TargetLanguage = "SCL",
            Scenario = s?.Text ?? "",
            // Every other scenario goes along, so references like "auto run ready" can be
            // resolved and flattened instead of guessed at.
            ReferencedScenarios = _scenarios
                .Where(o => !ReferenceEquals(o, s) && !string.IsNullOrWhiteSpace(o.Text))
                .Select(o => new ScenarioRef { Name = o.Name, Content = o.Text })
                .ToList(),
            Terms = terms,
            Timers = terms
                .Where(t => string.Equals(t.Type, "TON", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Variable).Distinct().ToList(),
            Functions = new List<string> { "HMI", "PLC", "Delay Timer", "Time Recorder" },
            Conventions = "Siemens TIA Portal SCL. Label each block with a short comment, " +
                          "at most 8 words. Do not copy the scenario sentence into the code."
        };
    }

    /// <summary>Shows the numbers a model is judged on, where they cannot scroll off screen.</summary>
    private void ShowMetrics(TranslationOutcome o)
    {
        MetricsPanel.Visibility = Visibility.Visible;

        // Whole-run time, held steady while the user clicks through the results.
        var showMs = _batchMs > 0 ? _batchMs : o.ElapsedMs;
        TimeText.Text = $"{showMs / 1000.0:0.0} s";
        TimeText.ToolTip = _batchMs > 0
            ? $"Total for the whole run. This scenario took {o.ElapsedMs / 1000.0:0.0} s."
            : "Time for this scenario.";

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

        // Slow runs are worth noticing when comparing models. A whole batch is allowed
        // longer than a single scenario before it counts as slow.
        TimeText.Foreground = showMs > (_batchMs > 0 ? 60000 : 30000)
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
        _batchMs = 0;
        ResultStatus.Text = "Cleared";
        MetricsPanel.Visibility = Visibility.Collapsed;

        // Clearing the panes alone would leave each scenario still holding its stored
        // result, so the tick/!/cross markers would stay beside the names and clicking
        // a scenario would bring the "cleared" code straight back. Drop the outcomes too.
        var cleared = _scenarios.Count(s => s.Outcome is not null);
        foreach (var s in _scenarios) s.Outcome = null;

        Status(cleared == 0
            ? "Nothing to clear. Scenario and terms are unchanged."
            : $"Cleared {cleared} scenario result{(cleared == 1 ? "" : "s")}. Scenario and terms are unchanged.");
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
        var profile = new TreeViewItem { Header = "Lipico", IsExpanded = true };   // ProfileId 1
        var equip = new TreeViewItem { Header = "Equipment Type", IsExpanded = true };

        var motor = new TreeViewItem { Header = "Motor", Tag = 1, IsSelected = true };
        var valve = new TreeViewItem { Header = "Valve", Tag = 2 };
        motor.Selected += Equipment_Selected;
        valve.Selected += Equipment_Selected;
        equip.Items.Add(motor);
        equip.Items.Add(valve);
        profile.Items.Add(equip);

        var fn = new TreeViewItem { Header = "Function", IsExpanded = true };
        fn.Items.Add(new TreeViewItem { Header = "HMI" });
        fn.Items.Add(new TreeViewItem { Header = "PLC" });
        fn.Items.Add(new TreeViewItem { Header = "Delay Timer" });
        fn.Items.Add(new TreeViewItem { Header = "Time Recorder" });

        ReferenceTree.Items.Add(profile);
        ReferenceTree.Items.Add(fn);
        ReferenceTree.Items.Add(new TreeViewItem { Header = "Tags" });
    }

    /// <summary>Picking equipment in the tree shows only that equipment's scenarios.</summary>
    private void Equipment_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { Tag: int id }) return;
        e.Handled = true;
        if (_equipmentId == id) return;
        _equipmentId = id;
        ShowScenariosForEquipment();
    }

    private void ShowScenariosForEquipment()
    {
        _scenarios.Clear();
        foreach (var s in _allScenarios.Where(s => s.EquipmentId == _equipmentId))
            _scenarios.Add(new Scenario { Name = s.Name, Text = s.Text });

        if (_scenarios.Count > 0) ScenarioList.SelectedIndex = 0;
        else { ScenarioPlainText = ""; }

        var eq = _equipmentId == 1 ? "Motor" : "Valve";
        Status($"{eq}: {_scenarios.Count} scenario{(_scenarios.Count == 1 ? "" : "s")} loaded.");
    }

    /// <summary>
    /// The Lipico profile (ProfileId 1) exactly as it stands in the database:
    /// Statement, StatementProperty and PropertyTerm, joined.
    ///
    /// The important shape: PlcVariable belongs to the PROPERTY, PlcValue to the STATE.
    /// Operation Mode is blnMA, and Auto is blnMA = 0 while Manual is blnMA = 1 - two
    /// values of one variable, not two variables.
    /// </summary>
    private void LoadSample()
    {
        // ---- Statement table -------------------------------------------------
        _allScenarios.Clear();
        void S(int equipmentId, string name, string text) =>
            _allScenarios.Add((equipmentId, name, text));

        // EquipmentId 1 - Motor
        S(1, "Operation",
            "When auto run ready or manual run ready and no faults, then will command run.");

        S(1, "System Handle",
            "When operation mode in Auto, the Manual Start/Stop position will according with " +
            "Operation Signal Run/Stop.\n\n" +
            "During PLC Initialize or MCC Trip On, the Manual Operation position will be in Stop.\n\n" +
            "During PLC Initialize, Run Fail Timer's delay time Set to 10 seconds.\n\n" +
            "Operation Signal Run time will be recorded by runtime recorder.");

        S(1, "Run Failed",
            "When Operation signal Run not received after run fail timer elapsed, turn on Run Fail alarm, " +
            "turn on Run Fail Buzzer once.\n\n" +
            "When HMI Acknowledge, turn off Run Fail Buzzer.");

        S(1, "Auto Run Ready",
            "When operation mode in Auto and Sequence Auto is fulfilled.");

        S(1, "Manual Run Ready",
            "When operation mode in Manual and manual started.");

        S(1, "Faults",
            "When trip alarm is on or run fail alarm is on.");

        S(1, "Trip",
            "When Trip Signal is On, turn on Trip Alarm, turn on Trip Buzzer once.\n\n" +
            "When HMI Acknowledge, turn off Trip Buzzer.");

        // EquipmentId 2 - Valve
        S(2, "Operation",
            "When Manual Run Fulfilled or Auto Run Fulfilled, without trip Alarm, then PLC command Run.\n\n" +
            "During motor Run, the running time will be recorded by timer recorder.");

        S(2, "System Handle",
            "When in Auto Mode, Manual Operation position will change and retain according with Input Run Signal.\n\n" +
            "During PLC Initializing or MCC Trip On, the Manual Operation position will be in Stop.\n\n" +
            "During PLC Initializing, Run Fail Alarm Timer Setpoint assigned value 10 seconds.\n\n" +
            "During PLC Initializing, Trip Alarm Timer Setpoint assigned value 3 seconds.\n\n" +
            "Trip Alarm Timer will start counting when have Trip Feedback On. If the Trip Alarm Timer elapsed, " +
            "Trip Alarm will turn on. Trip Alarm will off and Trip Alarm Timer will be reset when Trip Feedback is off.\n\n" +
            "When Trip Alarm On, Trip Buzzer will turn on and retain. Trip Buzzer only can be reset by operator.");

        ShowScenariosForEquipment();

        // ---- StatementProperty + PropertyTerm --------------------------------
        _properties.Clear();

        PropertyNode P(string name, string variable, string type, string plcFunction)
        {
            var n = new PropertyNode { Name = name, Variable = variable, Type = type };
            _properties.Add(n);
            return n;
        }
        void St(PropertyNode parent, string name, string value) =>
            parent.Children.Add(new PropertyNode { Name = name, Value = value, Parent = parent });

        var opMode = P("Operation Mode", "blnMA", "INT", "HMI");
        St(opMode, "Auto", "0");
        St(opMode, "Manual", "1");

        var mccTrip = P("MCC Trip", "New Instance", "BOOL", "HMI");
        St(mccTrip, "Trip Off", "0");
        St(mccTrip, "Trip On", "1");

        P("VOP", "New Instance", "BOOL", "HMI");

        var manual = P("Manual", "blnMC", "INT", "HMI");
        St(manual, "Start", "1");
        St(manual, "Stop", "0");

        var tripSig = P("Trip Signal", "blnTRP", "INT", "PLC");
        St(tripSig, "On", "1");
        St(tripSig, "Off", "0");

        var opSig = P("Operation Signal", "blnRUN", "INT", "PLC");
        St(opSig, "Run", "1");
        St(opSig, "Stop", "0");

        var cmd = P("Command", "blnOut", "INT", "PLC");
        St(cmd, "Run", "1");
        St(cmd, "Stop", "0");

        P("PLC Initialize", "PLCInit", "BOOL", "PLC");
        P("Run Fail Timer", "tmrRFAL", "TON", "Delay Timer");
        P("Runtime recorder", "RFTimer", "TIME", "Time Recorder");

        var tripAlarm = P("Trip Alarm", "New Instance", "INT", "HMI");
        St(tripAlarm, "On", "1");
        St(tripAlarm, "Off", "0");

        var runFailAlarm = P("Run Fail Alarm", "New Instance", "INT", "HMI");
        St(runFailAlarm, "On", "1");
        St(runFailAlarm, "Off", "0");

        var seqAuto = P("Sequence Auto", "blnAUT", "INT", "HMI");
        St(seqAuto, "Fulfilled", "1");
        St(seqAuto, "Unfulfilled", "0");

        var runFailBuzzer = P("Run Fail Buzzer", "New Instance", "INT", "HMI");
        St(runFailBuzzer, "On", "1");
        St(runFailBuzzer, "Off", "0");

        var tripBuzzer = P("Trip Buzzer", "New Instance", "INT", "HMI");
        St(tripBuzzer, "On", "1");
        St(tripBuzzer, "Off", "0");

        P("HMI Acknowledge", "SysAck", "BOOL", "HMI");
    }


    protected override void OnClosed(EventArgs e)
    {
        _translator.Dispose();
        _usageHttp.Dispose();
        base.OnClosed(e);
    }
}










