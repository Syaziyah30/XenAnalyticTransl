# Start here

Everything you need to run your first AI translation, in order.
Read this file first; `execution-plan.md` covers the whole project after that.

---

## What has been built for you

| Piece | Where | What it is |
|---|---|---|
| **AI library** | `src/XenAnalyticTransl.AI/` | Context pack, prompt, model client, validator. Provider-neutral. This is what later drops into the real app. |
| **R&D Studio** | `src/XenAnalyticTransl.Studio/` | A working WPF app laid out like the mockup, so you can test models today. |
| **Solution** | `XenAnalyticTransl.slnx` | Open this in Visual Studio. |

The Studio is **not** the product. It is a test rig: same panels as the real UI, plus
provider/model switching, timing, token counts and a Save Run button so you can build
the evaluation set. The real app later references `XenAnalyticTransl.AI` and throws the
Studio away.

---

## Step 1 - Get an API key

**Use OpenRouter to start.** One key reaches every Qwen model plus Gemini, Claude and GPT,
so the whole evaluation set can run across providers without opening four accounts. There
is no region, no workspace id and no expiry clock, and `qwen3.8-27b:free` costs nothing.

1. Go to **openrouter.ai** and sign in
2. Open **Keys** -> **Create Key**
3. Copy it

Alibaba Model Studio is the alternative, and the more sensible route for Qwen in
production. It is fiddlier to set up - see "Using Alibaba Model Studio instead" below.

---

## Step 2 - Store the key

Never put the key in code, in `appsettings.json`, or anywhere inside this repo. This
repository mirrors to GitHub; a leaked key would have to be scrubbed from two remotes.

Run this once in a terminal:

```
setx OPENROUTER_API_KEY "paste-your-key-here"
```

Then **close and reopen** the terminal and Visual Studio. `setx` only affects processes
started after it runs, so an app already open will still see no key.

To check it took:

```
echo %OPENROUTER_API_KEY%
```

The Studio reads this variable at startup and shows `Key: OK (sk-o...9f2c)` in the
toolbar. It never writes the key anywhere.

### Why not a config file

The Studio runs on your machine, so a file would be tolerable here. The real app will be
distributed to every engineer's machine, where a key inside the binary can be extracted
in minutes and bills to your account until someone notices. Building the habit now means
the production code inherits it.

---

## Step 3 - Pick a model

Choose a preset from the **Provider** dropdown. Base URL and model fill in automatically -
nothing to edit.

| Preset | Model | Cost per 1M tokens |
|---|---|---|
| OpenRouter - Qwen3.8 27B (FREE) | `qwen/qwen3.8-27b:free` | Free |
| OpenRouter - Qwen3.8 Flash (cheap) | `qwen/qwen3.8-flash` | $0.15 in / $0.47 out |
| OpenRouter - Qwen3.8 Max (coding) | `qwen/qwen3.8-max-0902` | $2.00 in / $6.00 out |

Start on the free one to check the plumbing works. Then compare all three on the same
scenario - `qwen3.8-max-0902` is post-trained for coding and is the strongest Qwen
candidate for SCL.

### Using Alibaba Model Studio instead

1. Create an Alibaba Cloud account, open **Model Studio** -> **API Key**
2. **Select the Singapore region first** - keys are bound to their region, and the free
   quota exists only in Singapore. A mismatched region returns `401 invalid_api_key`,
   which looks like a bad key but is not.
3. Create the key, copy it immediately (shown once), and note your **Workspace ID**
4. `setx DASHSCOPE_API_KEY "..."`
5. In the Studio, choose an Alibaba preset and replace `WORKSPACE_ID` in the Base URL
6. In the Alibaba console, turn on **Free Quota Only** - it is off by default, so billing
   starts silently when the free 1M tokens per model run out

---

## Step 4 - Run it

From Visual Studio: set **XenAnalyticTransl.Studio** as the startup project and press F5.

Or from a terminal:

```
dotnet run --project src\XenAnalyticTransl.Studio
```

---

## Step 5 - Your first translation

The Studio opens with the **Mtr01 Operation** scenario and its eleven terms already
loaded, taken from your mockup.

1. Check the toolbar says `Key: OK`
2. Click **Request AI Translator**
3. Watch the five tabs at the bottom

| Tab | What to look for |
|---|---|
| Generated Logic (SCL) | The code. Copy it into TIA Portal and try to compile. |
| Explanation | Whether the model understood the scenario or just pattern-matched. |
| **Validation** | **Check this first.** Unknown identifiers mean invented variable names. |
| Context Pack | Exactly what was sent. Useful when output is wrong - usually the context is thin. |
| Raw Response | The model's unparsed reply. For debugging bad JSON. |

**The real test is step 3 of the list above, then compiling in TIA Portal.** Everything
else is opinion until the code compiles.

---

## Step 6 - Build the evaluation set

Each time you run a scenario worth keeping, click **Save Run...**. It writes a JSON file
to `Reference/runs/` with the scenario, the context pack, the result, timings and token
counts.

Thirty to fifty of these become the evaluation set that decides which model you ship. That
set, not a benchmark table, is how the provider choice gets made.

---

## Trying a different model

The whole point of the OpenAI-compatible interface: change the **Provider** dropdown, or
just edit Base URL and Model. Same code path, same prompt, same validator. Presets included:

| Provider | Key variable |
|---|---|
| Qwen - Alibaba Model Studio (Singapore) | `DASHSCOPE_API_KEY` |
| Qwen - Alibaba Model Studio (Virginia) | `DASHSCOPE_API_KEY` |
| OpenRouter - many models, one key | `OPENROUTER_API_KEY` |
| Self-hosted vLLM / Ollama | `LOCAL_LLM_API_KEY` |

---

## Where to change things

| You want to... | Edit |
|---|---|
| Change how the model is instructed | `src/XenAnalyticTransl.AI/PromptBuilder.cs` - **highest-leverage file in the project** |
| Change what gets sent | `MainWindow.xaml.cs` -> `BuildContextPack()` |
| Change validation rules | `src/XenAnalyticTransl.AI/TagValidator.cs` |
| Add a provider preset | `src/XenAnalyticTransl.AI/LlmTranslator.cs` -> `ProviderPresets` |
| Change the layout | `src/XenAnalyticTransl.Studio/MainWindow.xaml` |

Every prompt change should be re-run against the saved evaluation set before you keep it.
Prompt edits that help one scenario often quietly break three others.

---

## Two things to remember

**Use invented data until the policy question is answered.** The sample scenario shipped
with the Studio is from your own mockup - check whether those tag names are considered
sensitive before sending them to Alibaba Cloud. The open question is in `execution-plan.md`.

**Nothing generated here goes near real equipment without an engineer reading it.** The
validator catches invented variable names, not wrong logic. Wrong logic that compiles is
the failure mode that matters.
