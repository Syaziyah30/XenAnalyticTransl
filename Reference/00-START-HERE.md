# Start here

Everything you need to run your first AI translation, in order.
Read this file first; `execution-plan.md` covers the whole project after that.

Last updated 5 October 2026.

---

## What has been built

| Piece | Where | What it is |
|---|---|---|
| **AI library** | `src/XenAnalyticTransl.AI/` | Context pack, prompt, provider client, validator, code cleaner. Provider-neutral. **This is the deliverable** - it drops into PLC Logic Builder later. |
| **R&D Studio** | `src/XenAnalyticTransl.Studio/` | WPF app laid out like the production UI, for comparing models. Disposable. |
| **CLI harness** | `src/XenAnalyticTransl.Cli/` | Same data, same code path, no UI. For debugging and batch runs. |
| **Solution** | `XenAnalyticTransl.slnx` | Open this in Visual Studio. |

The Studio is a test rig, not the product. It carries the same panels as the real UI plus
what R&D needs: provider switching, live timing, token counts, spend tracking, the exact
request sent, and a Save Run button for building the evaluation set.

---

## Step 1 - Get an API key

**Use OpenRouter.** One key reaches Qwen, Gemini, Claude and GPT, so the whole evaluation
set can run across vendors without opening four accounts.

1. Go to **openrouter.ai**, sign in
2. **Keys** -> **Create Key**, set a credit limit (5 dollars is plenty)
3. Copy it - the full value is shown once

Two optional extras, each its own variable:

| Key | Unlocks |
|---|---|
| `ANTHROPIC_API_KEY` | Claude Opus 5 and Sonnet 5 direct |
| `HUGGINGFACE_API_KEY` | Llama and other open-weight models |

Alibaba Model Studio is the sensible production route for Qwen specifically, but no
Alibaba preset ships in the app today - see the end of this file.

---

## Step 2 - Store the key

Never put a key in code, in `appsettings.json`, or anywhere inside this repo. This
repository mirrors to GitHub; a leaked key would have to be scrubbed from two remotes.

Run this once in a terminal:

```
setx OPENROUTER_API_KEY "paste-your-key-here"
```

Then **close and reopen** the terminal and Visual Studio. `setx` only reaches processes
started after it runs, so anything already open still sees nothing.

To check it took:

```
echo %OPENROUTER_API_KEY%
```

The Studio reads the variable at startup and shows `Key: OK (sk-o...9f2c)` in the toolbar.
It never writes a key anywhere.

**Watch for curly quotes.** Copying a command out of formatted text often converts `"` into
`"` and `"`, and `setx` stores them as part of the value. The key then fails with a 401
that looks like a bad key. Type the quotes by hand if in doubt.

---

## Step 3 - Pick a model

Choose from the **Provider** dropdown. Base URL, model and every per-model quirk fill in
automatically; nothing to edit.

Measured on the `Operation` scenario:

| Preset | Time | Cost/run | Verdict |
|---|---|---|---|
| **Qwen3.8 Flash** | 8-12 s | RM0.002 | **Start here.** Correct logic, cheapest |
| Claude Sonnet 5 (direct) | 13 s | RM0.07 | Correct, sharpest warnings |
| Gemini 3.5 Flash Lite | 2.7 s | RM0.006 | Fastest, but dropped an ELSE branch |
| Llama 3.1 8B (HuggingFace) | 145 s | free | Produced invalid SCL. Keep as a floor |
| Qwen3.8 27B (free) | varies | free | Shared pool - 429s most of the time |
| Qwen Max, GPT, Claude via OpenRouter | - | - | Need more credit than a trial balance holds |

A note on the paid ones: a provider reserves `MaxTokens x the output rate` **before**
running anything. A model with a high ceiling and a high rate can fail with `402` on a
small balance even though the real reply would cost a fraction of a cent.

---

## Step 4 - Run it

From Visual Studio: set **XenAnalyticTransl.Studio** as the startup project, press F5.

Or double-click:

```
src\XenAnalyticTransl.Studio\bin\Debug\net10.0-windows\XenAnalyticTransl.Studio.exe
```

Close the app before rebuilding, or the build fails with `MSB3021 - file is locked`.

---

## Step 5 - Your first translation

The Studio opens on the **Lipico** profile with the real database loaded: 7 Motor
scenarios, 2 Valve scenarios, and 16 properties with their states and PLC variables.

1. Check the toolbar reads `Key: OK`
2. Click **Request AI Translator**

**It translates every scenario of the selected equipment**, one after another, following
along in the list. When it finishes, each scenario keeps its own result - click any of
them to see its code, with no re-running and no further cost.

The marker beside each name is the verdict:

| Marker | Meaning |
|---|---|
| **tick**, green | Generated, every identifier exists in the tag table |
| **!**, amber | Generated, but it referenced names that do not exist |
| **cross**, red | Failed. The status bar says why |

A tick means the *names* check out. It does not mean the logic is right.

Six tabs carry the detail:

| Tab | What to look for |
|---|---|
| Generated Logic (SCL) | The code. Copy it into TIA Portal and compile |
| Explanation | Whether the model understood the scenario or pattern-matched |
| **Validation** | **Read this first.** Unknown identifiers mean invented variable names |
| Context Pack | Exactly what was sent. When output is wrong, the context is usually thin |
| Request Sent | The literal JSON posted, including every per-model quirk. No key - that rides in a header |
| Raw Response | The unparsed reply, for when parsing fails |

**The real test is compiling in TIA Portal.** Everything else is opinion until it compiles.

---

## Step 6 - Build the evaluation set

Click **Save Run...** on anything worth keeping. It writes a JSON file to
`Reference/runs/` with the scenario, context pack, result, timings and token counts.

Thirty to fifty of these decide which model you ship. Run each scenario several times:
the same model gave `ManualOperation := FALSE` on one run and `:= 0` on the next, so a
single run tells you what a model did once, not what it does.

---

## Reading the scenario editor

The text is coloured by what the translator will resolve:

| Colour | Means |
|---|---|
| Dark orange | A scenario name - will be flattened into this one |
| Dark green | A property |
| Green | A state of a property |
| Blue | A constant or logical keyword (`when`, `or`, `and`, `no`, `then`, `10`) |
| Black | Ordinary words |

**If a phrase you expect to resolve shows up black, the translator will not resolve it
either.** The colouring is a diagnostic, not decoration.

---

## Where to change things

| You want to... | Edit |
|---|---|
| Change how the model is instructed | `AI/PromptBuilder.cs` - **highest-leverage file in the project** |
| Add a provider or model | `AI/LlmTranslator.cs` -> `ProviderPresets` |
| Change validation rules | `AI/TagValidator.cs` |
| Change what counts as model waffle in the code | `AI/CodeCleaner.cs` |
| Change the scenario or property data | `Studio/MainWindow.xaml.cs` -> `LoadSample()` |
| Change the layout | `Studio/MainWindow.xaml` |
| Change the highlight colours or keywords | `Studio/ScenarioHighlighter.cs` |

Re-run the saved evaluation set after any prompt change. Edits that help one scenario
quietly break three others.

### Adding a model

Four lines. Find the slug on the provider's model page - it is the path in the URL.

```csharp
new() { ProviderName = "OpenRouter - Gemini 3.8 Flash",
        BaseUrl = "https://openrouter.ai/api/v1",
        Model = "google/gemini-3.8-flash", ApiKeyEnvVar = "OPENROUTER_API_KEY",
        InputUsdPerM = 0.75m, OutputUsdPerM = 3.75m },
```

Close the app, rebuild, run. If it errors, add the flag the message points at:

| Error | Add |
|---|---|
| `400 reasoning is mandatory` | `DisableReasoning = false, MaxTokens = 10000` |
| `400 temperature is deprecated` | `SendTemperature = false` |
| Empty output, `finish_reason: length` | Raise `MaxTokens` |
| `402 not enough credit` | Lower `MaxTokens`, or use a cheaper model |
| `429 rate-limited` | Not yours to fix. Another model, or wait |

For a one-off test, skip all of this and type the slug straight into the **Model** box.

---

## Things that will bite you

**Six properties are called `New Instance`.** MCC Trip, VOP, Trip Alarm, Run Fail Alarm,
Run Fail Buzzer and Trip Buzzer all share that placeholder, so no model can tell them
apart. Measured effect of giving them real names: output fell from 2,425 tokens to 724,
time from 30.5s to 8.5s, and the fault interlock appeared instead of being omitted.
**This is the single biggest limit on output quality.**

**`MCC Trip` has both its states set to 0.** `Trip Off = 0` and `Trip On = 0` in the
source data, so nothing distinguishes them. Every other property splits 1/0 cleanly.
Any model asked to test MCC Trip is guessing. Second item to fix after the names above.

**There is no request deadline.** A slow model runs to completion - Llama took 145
seconds on one scenario. Cancel is the only way out of a stall.

**Output varies between runs.** Even at temperature 0, providers are not deterministic.
Declare every data type and the variation drops sharply, but it never reaches zero. That
is why both safety gates exist.

**Nothing generated here goes near equipment without an engineer reading it.** The
validator catches invented names, not wrong logic. Wrong logic that compiles is the
failure that matters.

**The policy question is still open.** Can plant scenario text and PLC tag names be sent
to an external AI service? Until that is answered, keep real plant data out of the free
tiers - their terms generally allow the provider to use submitted content.

---

## Using Alibaba Model Studio instead

The better production route for Qwen - buying it direct rather than through a router -
and fiddlier to set up. **There is no Alibaba preset in the app right now**; the earlier
ones were removed once OpenRouter covered Qwen. You would add one back.

1. Create an Alibaba Cloud account, open **Model Studio** -> **API Key**
2. **Select the Singapore region first.** Keys are bound to their region and the free
   quota exists only there. A mismatch returns `401 invalid_api_key`, which looks like a
   bad key but is not.
3. Create the key, copy it immediately (shown once), note your **Workspace ID**
4. `setx DASHSCOPE_API_KEY "..."`
5. Add a preset in `ProviderPresets`, substituting your own workspace id:

```csharp
new() { ProviderName = "Alibaba - Qwen Plus",
        BaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
        Model = "qwen-plus", ApiKeyEnvVar = "DASHSCOPE_API_KEY" },
```

6. In the console, turn on **Free Quota Only**. It is off by default, so billing starts
   silently when the free 1M tokens per model run out.
