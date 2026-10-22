# XenAnalyticTransl

Turning plain-English control scenarios into **pseudocode**, **logic expressions** or
**Siemens SCL**, using a large language model.

An engineer writes a scenario the way they would explain it to a colleague -
*"When auto run ready or manual run ready and no faults, then will command run"* - and
defines the properties it uses. The translator sends that to a model and returns the
logic, an explanation, and a list of anything it could not resolve.

This is the AI layer for **PLC Logic Builder**, an existing Windows desktop application.

## Status

**R&D, not production.** The pipeline runs end to end. Nothing generated here goes near
equipment without an engineer reading it first, and nothing has been compiled in TIA
Portal yet.

## Three outputs

| Output | For | Uses |
|---|---|---|
| **Pseudocode** (default) | Reviewing the logic before any PLC code is written | Property names |
| **Logic Expression** | Checking conditions and latches as Boolean algebra | Property names |
| **SCL** | The PLC itself | PLC tags |

The review formats come first on purpose. They use the names people say rather than PLC
tags, so they can express logic the tag data cannot yet: while six properties share the
placeholder tag `New Instance`, SCL has to leave the fault interlock as a TODO, but
pseudocode writes it in full. Review in pseudocode, then generate SCL.

Pseudocode follows a fixed house style:

```
IF ((Manual Run == fulfilled) OR (Auto Run == fulfilled))
   AND (Trip Alarm == OFF) THEN
    Turn ON PLC Run Command
ELSE
    Turn OFF PLC Run Command
END IF
```

## Built-in checks

The model is not trusted on its own. Every result is checked before it is shown:

| Check | Catches | Applies to |
|---|---|---|
| Tag validator | PLC variables the model invented | SCL |
| SCL-leak guard | `:=`, `;`, `END_IF` or PLC tags inside pseudocode | Pseudocode |
| Precedence guard | `AND` and `OR` at the same bracket level - `A OR B AND NOT C` silently drops the check from `A` | All |
| Code cleaner | The model's own deliberation written into the code | All |

A failed check shows in the result header (`CHECK BRACKETS`, `CONTAINS SCL SYNTAX`) and as a
warning count in the status bar.

## Saving results to the database

Results are saved to `XenCreator.dbo.OutputXenTrans`. Two buttons, for R&D:

| Button | What it does | AI calls |
|---|---|---|
| **Request AI Translator** | Sends every scenario to the AI and **replaces** each scenario's saved row with the new result | 7 |
| **Load from DB** | Shows the saved results. No AI call | 0 |

**Measured on the Motor scenarios:** a fresh run takes two to three minutes and about 33,000
tokens on Groq's free tier; loading the same results from DB took **0.4 seconds and 0
tokens**.

The table holds **one row per scenario and output type** - equipment, scenario name and
output (Pseudocode / Logic Expression / SCL). A new translation deletes the old row and
inserts the new one in a single transaction, so there is no history and no duplicates.
Running Motor never touches Valve's rows, and a pseudocode run never touches SCL rows.
Failed calls are never saved, so a failure keeps the previous answer.

**Load from DB** matches by scenario name, so it still shows a result after the scenario
was edited - but the result header then reads **OUT OF DATE, scenario changed since
saved**. That is decided by a SHA-256 **fingerprint** of everything that decides the
answer: the scenario text, the scenarios it refers to, the property list, the model, the
output type and the translation rules. Scenarios with nothing saved are listed as "not in
DB" in the status bar.

A request carries only the scenarios it actually mentions - the ones the editor colours
orange - rather than all of them, so requests are smaller.

**Setup, once - the easy way.** Run this yourself; it asks for the database password
(typed hidden, never shown or written to a file), tests the connection, creates the table
if missing, and stores `XENTRANS_DB`:

```
powershell -ExecutionPolicy Bypass -File "Reference\sql\Setup-XenTransDb.ps1"
```

**Or by hand:**

1. Someone with CREATE TABLE rights on XenCreator runs
   [`Reference/sql/OutputXenTrans.sql`](Reference/sql/OutputXenTrans.sql). It is safe to
   run twice.
2. Store the connection string - it holds the database password, so never in the code:
   ```
   setx XENTRANS_DB "Server=128.100.20.33\neptune;Database=XenCreator;User Id=...;Password=...;TrustServerCertificate=True"
   ```
   `Integrated Security=True` would avoid storing a password, but Windows sign-in is
   refused on `128.100.20.33\neptune` - use the SQL login.
3. Restart the app.

Adding the database brought in the project's first external package,
`Microsoft.Data.SqlClient`. It pulls in a component with a known high-severity
vulnerability, so `System.Security.Cryptography.Pkcs` is pinned to a patched version in
`XenAnalyticTransl.AI.csproj`; NuGet reports no vulnerable packages.

Without `XENTRANS_DB` the app works exactly as before and says so in the status bar. If the
database cannot be reached, the batch carries on without it.

## Layout

| Project | What it is |
|---|---|
| `src/XenAnalyticTransl.AI` | Context pack, prompt, provider client, checks, result store. Provider-neutral. **This is the deliverable** - it drops into PLC Logic Builder later |
| `src/XenAnalyticTransl.Studio` | WPF test rig for comparing models and outputs side by side. Disposable |
| `src/XenAnalyticTransl.Cli` | Same pipeline, no UI. For debugging and batch runs |

The translation rules are hardcoded in `src/XenAnalyticTransl.AI/PromptBuilder.cs`. That
is the file to edit to change how the model translates; rebuild afterwards.

## Providers

Any OpenAI-compatible endpoint works; per-model quirks are flags on the preset, not
branches in the code. Presets live in `ProviderPresets` in `LlmTranslator.cs`.

| Provider | Status |
|---|---|
| **Groq** | **Active.** Free daily allowance. Qwen3.8 27B, GPT-OSS 120B and GPT-OSS 20B all tested working |
| Anthropic (Claude) | Presets kept. Strongest output in testing; needs account credit |
| Hugging Face | Preset kept. Free credit exhausted |
| OpenRouter | Presets commented out, not deleted - remove the `//` to restore them once the account has credit |

Groq's two Llama models are Enterprise-only and return 404 on a free key.

## Quick start

Set a key - never commit one, this repo mirrors to two remotes. Use `setx`, not `set`:

```
setx GROQ_API_KEY "your-key"
```

Close and reopen your terminal, then:

```
dotnet build XenAnalyticTransl.slnx
dotnet run --project src/XenAnalyticTransl.Studio
```

The Studio opens on the Lipico profile with real data loaded - 7 Motor and 2 Valve
scenarios across 16 properties. Choose an **Output**, then press **Request AI Translator**
to translate every scenario of the selected equipment.

Using the Studio:

- **General References** holds Lipico > Equipment Type > Motor, Valve. Pick one to list its
  scenarios; the Statement title says which is showing ("Statement - Motor").
- **Results stay on screen** while the app is open. Click between scenarios, or switch
  Motor to Valve and back, and each scenario keeps its output; switching back reopens the
  scenario that was open. **Load Sample** starts fresh on purpose.
- The result panel shows **Generated Pseudocode** only. The Explanation, Validation,
  Context Pack, Request Sent and Raw Response tabs are hidden, not removed - delete
  `Visibility="Collapsed"` on a tab in `MainWindow.xaml` to bring it back.
- **Ctrl+F** searches scenario names and text, the properties and the result.

From the command line:

```
dotnet run --project src/XenAnalyticTransl.Cli -- openai/gpt-oss-120b "Trip" motor pseudo
```

The last argument is `pseudo`, `logic` or `scl`; it defaults to `pseudo`.

**Groq free-tier limits shape how it runs.** It allows about 7,000 input tokens a minute
and one scenario sends about 4,400, so a full seven-scenario run takes two to three
minutes - the app waits out each limit and retries rather than failing. The daily allowance
covers roughly 40 calls, about five full runs.

## Known limits

- **Six properties share the tag `New Instance`** - MCC Trip, VOP, Trip Alarm, Run Fail
  Alarm, Run Fail Buzzer, Trip Buzzer. SCL cannot name them. Giving them real names cut
  output from 2,425 tokens to 724 and restored a fault interlock the model had been
  omitting. This is the biggest single limit on SCL quality.
- **Valve scenarios use names the data does not contain** - "Manual Run Fulfilled",
  "motor Run", "timer recorder", "Trip Alarm Timer", "Trip Feedback". The model has to
  invent names for them, so wording varies from run to run.
- **Output varies between runs**, even at low temperature. Logic is usually stable;
  names and layout drift. Run a scenario more than once before judging a model.
- **Assumed behaviour is flagged, not hidden.** Where a scenario does not say what happens
  otherwise - an ELSE, a reset - the model states its assumption in the warnings.
- **No history in DB.** Request AI Translator overwrites the previous answer for every
  scenario it runs, even if the old one was better. Copy anything worth keeping first.
- **The screen starts empty after a restart.** Results are kept while the app is open;
  after reopening it, Load from DB brings them back in under a second.
- **The explanation and warning text are hidden** with the other result tabs. Serious
  problems still show in the result header; the rest only as a count in the status bar.
- **Scenario wording decides what is reused.** System Handle says "the Manual Operation
  position", and the editor reads "Operation" as the Operation scenario - so editing
  Operation also re-translates System Handle. If that is not what the wording means,
  rephrase it.

## Documentation

| File | Covers |
|---|---|
| [Reference/00-START-HERE.md](Reference/00-START-HERE.md) | Setup, running it, troubleshooting |
| [Reference/Save AI Results to Database - Built and Tested.pptx](Reference/Save%20AI%20Results%20to%20Database%20-%20Built%20and%20Tested.pptx) (and [PDF](Reference/Save%20AI%20Results%20to%20Database%20-%20Built%20and%20Tested.pdf)) | The database feature as built: how it works, measured result, test results, what is next |
| [Reference/Proposal - Save AI Results to Database.pptx](Reference/Proposal%20-%20Save%20AI%20Results%20to%20Database.pptx) (and [PDF](Reference/Proposal%20-%20Save%20AI%20Results%20to%20Database.pdf)) | The original proposal, before it was built |
| [Reference/sql/OutputXenTrans.sql](Reference/sql/OutputXenTrans.sql) | The table script |
| [Reference/sql/Setup-XenTransDb.ps1](Reference/sql/Setup-XenTransDb.ps1) | One-time setup: creates the table if missing and stores `XENTRANS_DB` |
| [Reference/execution-plan.md](Reference/execution-plan.md) | Project plan and milestones |
| [docs/master-plan.md](docs/master-plan.md) | Wider programme plan |
| [docs/gemini-integration-flow.md](docs/gemini-integration-flow.md) | Integration flowchart |
