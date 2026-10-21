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

    private bool _isExpanded;
    private bool _isSelected;

    /// <summary>Bound two-way to the TreeViewItem so find can open a node's parents.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Changed(nameof(IsExpanded)); }
    }

    /// <summary>Bound two-way to the TreeViewItem so find can highlight a node.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Changed(nameof(IsSelected)); }
    }

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

    /// <summary>
    /// Saved results in XenCreator.dbo.OutputXenTrans. Null when XENTRANS_DB is not set -
    /// the app then works exactly as before, sending every scenario to the AI. Read once
    /// at start-up, like the API keys: setx needs an app restart.
    /// </summary>
    private readonly ResultStore? _store = ResultStore.FromEnvironment();
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

        foreach (var t in PromptBuilder.Targets) OutputBox.Items.Add(t);
        OutputBox.SelectedIndex = 0;

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
                    Value = string.IsNullOrWhiteSpace(n.Value) ? null : n.Value,
                    Property = n.Parent?.Name ?? n.Name,
                    State = n.Parent is null ? null : n.Name
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
        var fresh = 0; var loaded = 0; var freshTokens = 0;
        string? dbNote = null;
        var batch = System.Diagnostics.Stopwatch.StartNew();
        _batchMs = 0;           // fall back to per-scenario time until the batch lands

        try
        {
            // ---- results database: check once per batch --------------------------
            // An unreachable server should cost one timeout, not one per scenario. If
            // anything is wrong the batch carries on without it - the database saves
            // tokens, it is never allowed to stop a translation.
            ResultStore? store = null;
            if (_store is null)
            {
                dbNote = $"Not saving results - {ResultStore.EnvVar} is not set.";
            }
            else
            {
                Status("Checking the results database...");
                var problem = await _store.CheckAsync(_cts.Token);
                if (problem is null) store = _store;
                else dbNote = problem + " Every scenario was sent to the AI.";
            }
            var useSaved = store is not null && IgnoreSavedBox.IsChecked != true;

            foreach (var s in todo)
            {
                if (_cts.IsCancellationRequested) break;

                done++;
                ScenarioList.SelectedItem = s;          // follow along in the list

                // The fingerprint must come from the very pack that is translated, or a
                // saved row could be matched to a request it did not answer.
                var pack = BuildContextPack(s);
                var key = store is null ? null : ResultStore.ComputeKey(pack, settings);

                TranslationOutcome? outcome = null;

                // ---- 1. already saved? load it: no AI call, no tokens --------------
                if (useSaved && key is not null)
                {
                    try
                    {
                        outcome = await store!.TryLoadAsync(key, pack, _cts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        dbNote = "Database read failed - " + DbError(ex) + " Carried on without it.";
                        store = null; useSaved = false; key = null;
                    }
                }

                if (outcome is not null)
                {
                    loaded++;
                    Status($"[{done}/{todo.Count}] {s.Name} - loaded from database");
                }
                else
                {
                    // ---- 2. not saved: ask the AI --------------------------------------
                    ResultStatus.Text = $"Generating {done} of {todo.Count}...";
                    Status($"[{done}/{todo.Count}] {s.Name} - calling {settings.Model}...");
                    StartRunClock();
                    try
                    {
                        outcome = await _translator.TranslateAsync(pack, settings, _cts.Token);
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

                    fresh++;
                    freshTokens += outcome.PromptTokens + outcome.CompletionTokens;
                    if (outcome.Ok)
                    {
                        _sessionRuns++;
                        _sessionUsd += outcome.EstimateCostUsd(settings.InputUsdPerM, settings.OutputUsdPerM);
                    }

                    // ---- 3. save a good answer so the next click can reuse it ---------
                    // Failures are never saved: a bad answer must not become the cached one.
                    if (outcome.Ok && store is not null && key is not null)
                    {
                        try
                        {
                            await store.SaveAsync(key, pack, s.Name, outcome, _cts.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            dbNote = "Could not save to the database - " + DbError(ex);
                            store = null; useSaved = false;
                        }
                    }
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
        var source = loaded == 0
            ? $"{fresh} translated ({freshTokens:N0} tokens)"
            : $"{fresh} translated ({freshTokens:N0} tokens), {loaded} loaded from database";
        var summary = $"Done: {done} scenario(s) in {batch.Elapsed.TotalSeconds:0.0}s  |  {source}  |  " +
                      $"{ok} clean, {flagged} with unknown identifiers, {failed} failed";
        if (dbNote is not null) summary += $"  |  {dbNote}";

        // When everything failed it is almost always one cause - say what it was rather
        // than making the reader click a scenario to find out.
        var firstError = todo.FirstOrDefault(s => s.Outcome is { Ok: false })?.Outcome?.Error;
        if (failed == done && firstError is not null)
            Status($"{summary}  |  ALL FAILED: {firstError}");
        else
            Status($"{summary}  |  click a scenario to see its code.");
    }

    /// <summary>A database error in one line. SqlException text never contains the password.</summary>
    private static string DbError(Exception ex) =>
        ex is Microsoft.Data.SqlClient.SqlException sql ? ResultStore.Describe(sql) : ex.Message;

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
            LabelOutput(SelectedTarget);
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
    /// <summary>
    /// The scenarios a scenario refers to by name, and the ones those refer to in turn.
    ///
    /// Uses the editor's own tokenizer, longest match first, so a scenario counts only
    /// where the editor colours it orange: "Trip Signal" is a property and does not
    /// pull in the "Trip" scenario. What is orange on screen is what gets sent.
    /// </summary>
    private List<Scenario> ReferencedBy(Scenario start)
    {
        var phrases = HighlightPhrases();
        var byName = _scenarios.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var found = new List<Scenario>();
        var seen = new HashSet<Scenario> { start };
        var queue = new Queue<Scenario>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (text, kind) in ScenarioHighlighter.Tokenize(current.Text, phrases))
            {
                if (kind != TokenKind.Scenario) continue;
                if (byName.TryGetValue(text.Trim(), out var referenced) && seen.Add(referenced))
                {
                    found.Add(referenced);
                    queue.Enqueue(referenced);          // follow references of references
                }
            }
        }
        return found;
    }

    private ContextPack BuildContextPack(Scenario? target = null)
    {
        var s = target ?? ScenarioList.SelectedItem as Scenario;
        var terms = CollectTerms();

        return new ContextPack
        {
            Equipment = _equipmentId == 1 ? "Motor" : "Valve",
            Scenario = s?.Text ?? "",
            // Only the scenarios this one actually mentions, so references like
            // "auto run ready" can be flattened. Sending all of them made every request
            // depend on every scenario - editing one would change all seven fingerprints
            // and the database could never be reused.
            ReferencedScenarios = s is null ? new List<ScenarioRef>()
                : ReferencedBy(s)
                    .Where(o => !string.IsNullOrWhiteSpace(o.Text))
                    .Select(o => new ScenarioRef { Name = o.Name, Content = o.Text })
                    .ToList(),
            Terms = terms,
            Timers = terms
                .Where(t => string.Equals(t.Type, "TON", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Variable).Distinct().ToList(),
            Functions = new List<string> { "HMI", "PLC", "Delay Timer", "Time Recorder" },
            TargetLanguage = SelectedTarget,
            // The SCL convention would contradict the review formats, which must not use
            // tags or SCL syntax - so only send it when SCL is what was asked for.
            Conventions = PromptBuilder.UsesNames(SelectedTarget)
                ? $"Output is {SelectedTarget} for review by an engineer. Use names, not PLC tags."
                : "Siemens TIA Portal SCL. Label each block with a short comment, " +
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

        // Session spend is counted once, when a fresh AI result arrives (Translate_Click).
        // Counting it here added a "run" every time a result was merely displayed.

        var model = o.Model.Contains('/') ? o.Model[(o.Model.IndexOf('/') + 1)..] : o.Model;
        var total = o.PromptTokens + o.CompletionTokens;
        var tokens = total > 0
            ? $"  |  {o.PromptTokens:N0} in / {o.CompletionTokens:N0} out  |  total {total:N0} tokens"
            : "";
        MetricsText.Text = o.FromDatabase
            ? $"{model}  |  loaded from database, saved {o.SavedAt:dd MMM HH:mm}  |  0 tokens used now"
            : $"{model}{tokens}";

        // Slow runs are worth noticing when comparing models. A whole batch is allowed
        // longer than a single scenario before it counts as slow.
        TimeText.Foreground = showMs > (_batchMs > 0 ? 60000 : 30000)
            ? System.Windows.Media.Brushes.Firebrick
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x12, 0x44, 0x7F));
    }

    /// <summary>
    /// The request as the AI reads it, not as it travels. On the wire the message is
    /// JSON, which cannot hold a real line break inside a string, so every break is the
    /// two characters "\n" - the rule book arrives as one enormous line of them. Here the
    /// JSON is parsed back: settings on top, then each message under its role with real
    /// line breaks. The exact JSON is unchanged in Save Run, so nothing is lost.
    /// </summary>
    private static string ReadableRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("What was sent to the AI, shown with real line breaks.");
            sb.AppendLine("(In transit each line break travels as \\n - that is how JSON carries text.)");
            sb.AppendLine();

            // Settings first: everything except the messages, one per line.
            foreach (var p in root.EnumerateObject())
            {
                if (p.NameEquals("messages")) continue;
                var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                sb.AppendLine($"{p.Name.ToUpperInvariant().Replace('_', ' '),-18}{value}");
            }

            if (root.TryGetProperty("messages", out var messages))
            {
                foreach (var m in messages.EnumerateArray())
                {
                    var role = m.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                    var label = role switch
                    {
                        "system" => "SYSTEM  -  the rules",
                        "user"   => "USER  -  the task",
                        _        => role.ToUpperInvariant()
                    };
                    var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

                    sb.AppendLine();
                    sb.AppendLine($"==================== {label} ====================");
                    sb.AppendLine();
                    sb.AppendLine(content.Replace("\r\n", "\n").TrimEnd());
                }
            }
            return sb.ToString();
        }
        catch (JsonException)
        {
            return json;                    // not JSON after all - show it untouched
        }
    }

    private void Render(TranslationOutcome o)
    {
        RawBox.Text = o.RawResponse ?? "";
        RequestBox.Text = ReadableRequest(o.RequestBody);
        ShowMetrics(o);
        LabelOutput(o.Target);          // the result says what it is, whatever the dropdown says now

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

        UnknownBox.Text = PromptBuilder.UsesNames(o.Target)
            ? $"Not checked - {o.Target} uses Property names, not PLC tags. Review the logic by reading it."
            : o.UnknownIdentifiers.Count == 0
                ? "None - every identifier exists in the tag table."
                : string.Join(Environment.NewLine, o.UnknownIdentifiers.Select(u => "  " + u));

        WarningsBox.Text = r.Warnings.Count == 0
            ? "None reported."
            : string.Join(Environment.NewLine, r.Warnings.Select(w => "  " + w));

        var sclLeak = r.Warnings.Any(w => w.StartsWith("NOT PURE PSEUDOCODE", StringComparison.Ordinal));
        var badBrackets = r.Warnings.Any(w => w.StartsWith("AMBIGUOUS PRECEDENCE", StringComparison.Ordinal));
        var verdict = badBrackets ? "Generated - CHECK BRACKETS"
            : sclLeak ? "Generated - CONTAINS SCL SYNTAX"
            : o.UnknownIdentifiers.Count == 0 ? "Generated successfully"
            : "Generated WITH UNKNOWN IDENTIFIERS";
        if (o.FromDatabase) verdict = verdict.Replace("Generated", "Loaded from database");
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

    // ================= Ctrl+F find (whole window) ==========================
    //
    // Searches everything the user can see, not one panel: scenario names and
    // their text, every property and state in the tree, and all five result
    // tabs. Each hit knows how to reveal itself - select a scenario, expand and
    // highlight a tree node, or switch tab and select the characters.

    /// <summary>
    /// One match, with the means to bring it on screen. Box and Start are set only for
    /// hits inside a result tab, so the highlighter knows which rectangle is current.
    /// </summary>
    private sealed record Hit(string Where, Action Reveal, TextBox? Box = null, int Start = -1,
                              Scenario? InScenarioText = null);

    private readonly List<Hit> _hits = new();
    private int _hitAt = -1;

    /// <summary>Every result box that find paints into.</summary>
    // Only the visible tab. The other result tabs are hidden, and jumping to a match in
    // one would open it.
    private TextBox[] SearchableBoxes => [SclBox];

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            OpenFind();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && FindBar.Visibility == Visibility.Visible)
        {
            FindClose_Click(sender, e);
            e.Handled = true;
        }
    }

    private void OpenFind()
    {
        FindBar.Visibility = Visibility.Visible;

        // Seed from the selection, the way an editor does.
        if (ActiveResultBox() is { SelectionLength: > 0 and < 120 } box)
            FindBox.Text = box.SelectedText;

        FindBox.Focus();
        FindBox.SelectAll();
        Rebuild();
    }

    private void FindClose_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        _hits.Clear();
        _hitAt = -1;
        FindWhere.Text = "";
        FindCount.Text = "";
        FindHighlighter.ClearAll(SearchableBoxes);
        FindHighlighter.Clear(ScenarioText);
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FindHint.Visibility = string.IsNullOrEmpty(FindBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
        if (_hits.Count > 0) GoTo(0);          // jump as you type
    }

    private void FindBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        var back = (System.Windows.Input.Keyboard.Modifiers &
                    System.Windows.Input.ModifierKeys.Shift) != 0;
        Step(back ? -1 : +1);
        e.Handled = true;
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => Step(+1);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void ResultTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Character offsets belong to the old tab; they mean nothing in the new one.
        if (!ReferenceEquals(e.OriginalSource, ResultTabs)) return;
        if (FindBar is null || FindBar.Visibility != Visibility.Visible) return;
        Rebuild();
    }

    // ================= output target =======================================

    /// <summary>SCL, Pseudocode or Logic Expression, from the Output dropdown.</summary>
    private string SelectedTarget =>
        OutputBox?.SelectedItem as string ?? PromptBuilder.TargetPseudocode;   // the default output

    /// <summary>Names the result tab and the copy button after what the result actually is.</summary>
    private void LabelOutput(string? target)
    {
        if (OutputTab is null || CopyBtn is null) return;
        var t = string.IsNullOrWhiteSpace(target) ? PromptBuilder.TargetScl : target;

        OutputTab.Header = t == PromptBuilder.TargetScl ? "Generated Logic (SCL)" : $"Generated {t}";
        CopyBtn.Content = t == PromptBuilder.TargetScl ? "Copy SCL" : $"Copy {t}";
        if (ResultTitle is not null) ResultTitle.Text = $"AI Translation Result ({t})";
    }

    private void OutputBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A result already on screen keeps its own label - switching the dropdown
        // changes what the NEXT request asks for, not what the last one was.
        if (ScenarioList?.SelectedItem is Scenario { Outcome: not null }) return;
        LabelOutput(SelectedTarget);
        if (ScenarioList is not null) Status($"Output set to {SelectedTarget}. Press Request AI Translator to generate it.");
    }

    /// <summary>The searchable box behind each result tab. Validation holds TextBlocks.</summary>
    private TextBox? ActiveResultBox() => SclBox;      // the only visible result tab

    /// <summary>Walks every searchable surface and builds the hit list in screen order.</summary>
    private void Rebuild()
    {
        _hits.Clear();
        _hitAt = -1;

        var term = FindBox.Text;
        if (term.Length == 0) { ShowFindStatus(); return; }

        bool Has(string? s) =>
            s is not null && s.Contains(term, StringComparison.OrdinalIgnoreCase);

        // ---- 1. Scenarios: the name in the list, and the text in the editor ----
        foreach (var s in _scenarios)
        {
            var scenario = s;
            if (Has(scenario.Name))
                _hits.Add(new Hit($"Scenario: {scenario.Name}",
                    () => ScenarioList.SelectedItem = scenario));

            // One hit per occurrence, so stepping walks the text rather than the list.
            var t = scenario.Text;
            var k = t.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            var nth = 0;
            while (k >= 0)
            {
                nth++;
                var label = $"Scenario text: {scenario.Name}" + (nth > 1 ? $" ({nth})" : "");
                var hitStart = k;
                _hits.Add(new Hit(label, () =>
                {
                    ScenarioList.SelectedItem = scenario;
                }, null, hitStart, scenario));
                k = t.IndexOf(term, k + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ---- 2. Properties and their states, at any depth ----
        void WalkProps(IEnumerable<PropertyNode> nodes)
        {
            foreach (var n in nodes)
            {
                var node = n;
                if (Has(node.Name) || Has(node.Variable) || Has(node.Value) || Has(node.Type))
                {
                    var label = node.Parent is null
                        ? $"Property: {node.Name}"
                        : $"State: {node.Parent.Name} / {node.Name}";

                    _hits.Add(new Hit(label, () =>
                    {
                        for (var p = node.Parent; p is not null; p = p.Parent)
                            p.IsExpanded = true;         // open the way down to it
                        node.IsSelected = true;
                    }));
                }
                WalkProps(node.Children);
            }
        }
        WalkProps(_properties);

        // ---- 3. Every occurrence in every result tab ----
        (TextBox Box, string Name, int Tab)[] tabs =
        {
            (SclBox, "Result", 0),     // the other tabs are hidden - see SearchableBoxes
        };

        foreach (var (box, name, tab) in tabs)
        {
            var text = box.Text;
            if (string.IsNullOrEmpty(text)) continue;

            var i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                var at = i;
                var line = box.GetLineIndexFromCharacterIndex(at) + 1;
                _hits.Add(new Hit($"{name} line {line}", () =>
                {
                    ResultTabs.SelectedIndex = tab;
                    box.Select(at, term.Length);
                    box.ScrollToLine(Math.Max(0, box.GetLineIndexFromCharacterIndex(at) - 2));
                }, box, at));
                i = text.IndexOf(term, i + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        PaintMatches();
        ShowFindStatus();
    }

    /// <summary>
    /// Repaints the match rectangles in every result box. A TextBox can only select one
    /// range, so showing all matches at once needs the adorner layer.
    /// </summary>
    private void PaintMatches()
    {
        var term = FindBox.Text;
        if (FindBar.Visibility != Visibility.Visible || term.Length == 0)
        {
            FindHighlighter.ClearAll(SearchableBoxes);
            FindHighlighter.Clear(ScenarioText);
            return;
        }

        var currentHit = _hitAt >= 0 && _hitAt < _hits.Count ? _hits[_hitAt] : null;

        // ---- the scenario editor ----
        // Offsets are taken from the live document, not from Scenario.Text, because the
        // editor's run walk is what the adorner resolves pointers against.
        var docText = RichFindAdorner.RunText(ScenarioText.Document);
        var scenarioStarts = new List<int>();
        var j = docText.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        while (j >= 0)
        {
            scenarioStarts.Add(j);
            j = docText.IndexOf(term, j + 1, StringComparison.OrdinalIgnoreCase);
        }

        var shown = ScenarioList.SelectedItem as Scenario;
        var scenarioCurrent =
            currentHit?.InScenarioText is not null && ReferenceEquals(currentHit.InScenarioText, shown)
                ? scenarioStarts.IndexOf(currentHit.Start)
                : -1;

        FindHighlighter.Show(ScenarioText, scenarioStarts, term.Length, scenarioCurrent);

        // ---- the result tabs ----
        foreach (var box in SearchableBoxes)
        {
            var starts = _hits.Where(h => ReferenceEquals(h.Box, box))
                              .Select(h => h.Start).ToList();

            // -1 when the selected hit lives in another box or another panel.
            var current = currentHit is not null && ReferenceEquals(currentHit.Box, box)
                ? starts.IndexOf(currentHit.Start)
                : -1;

            FindHighlighter.Show(box, starts, term.Length, current);
        }
    }

    private void Step(int delta)
    {
        if (_hits.Count == 0) { ShowFindStatus(); return; }
        GoTo((_hitAt + delta + _hits.Count) % _hits.Count);     // wraps both ways
    }

    private void GoTo(int index)
    {
        if (_hits.Count == 0) return;
        _hitAt = index;

        // Reveal can change the tab or the selected scenario, which moves focus.
        // Put it back in the find box so Enter keeps stepping.
        _hits[index].Reveal();
        PaintMatches();                 // re-tint: the current match changes colour
        ShowFindStatus();

        // Selecting a scenario rebuilds the editor's document; repaint once that lands.
        Dispatcher.BeginInvoke(new Action(PaintMatches),
                               System.Windows.Threading.DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(() => FindBox.Focus()),
                               System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowFindStatus()
    {
        if (FindBox.Text.Length == 0) { FindWhere.Text = ""; FindCount.Text = ""; return; }

        FindCount.Text = _hits.Count == 0 ? "0/0" : $"{_hitAt + 1}/{_hits.Count}";
        FindWhere.Text = _hitAt >= 0 && _hitAt < _hits.Count ? _hits[_hitAt].Where : "";
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
            : $"Cleared {cleared} scenario result{(cleared == 1 ? "" : "s")} from the screen. " +
              "Saved results in the database are kept - tick Ignore saved to re-translate.");
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










