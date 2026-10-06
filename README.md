# XenAnalyticTransl

Turning plain-English control scenarios into Siemens SCL (Structured Text), using a
large language model.

An engineer writes a scenario the way they would explain it to a colleague -
*"When auto run ready or manual run ready and no faults, then will command run"* - and
defines the terms it uses against real PLC tags. The translator sends that to a model
and returns compilable SCL, an explanation, and a list of anything it could not resolve.

This is the AI layer for **PLC Logic Builder**, an existing Windows desktop application.

## Status

**R&D, not production.** The current question is *which model to ship*, not whether the
pipeline works - that part runs end to end. Nothing generated here goes near equipment
without an engineer reading it first.

Qwen3.8 Flash is the front-runner: correct logic, roughly RM0.002 per scenario. Claude
Sonnet 5 produces the sharpest warnings at ~35x the cost.

## Layout

| Project | What it is |
|---|---|
| `src/XenAnalyticTransl.AI` | Context pack, prompt, provider client, tag validator. Provider-neutral. **This is the deliverable** - it drops into PLC Logic Builder later |
| `src/XenAnalyticTransl.Studio` | WPF test rig for comparing models side by side. Disposable |
| `src/XenAnalyticTransl.Cli` | Same pipeline, no UI. For debugging and batch runs |

Any OpenAI-compatible endpoint works. Ten presets ship, covering OpenRouter, Anthropic
direct and Hugging Face; per-model quirks are flags on the preset rather than branches in
the code.

## Quick start

Set a key - never commit one, this repo mirrors to two remotes:

```
setx OPENROUTER_API_KEY "your-key"
```

Reopen your terminal, then:

```
dotnet build XenAnalyticTransl.slnx
dotnet run --project src/XenAnalyticTransl.Studio
```

The Studio opens on the Lipico profile with real data loaded - 7 Motor and 2 Valve
scenarios across 16 properties. Press **Request AI Translator** to translate all of them.

Full setup, the preset table and troubleshooting are in
[Reference/00-START-HERE.md](Reference/00-START-HERE.md).

## Known limits

Two defects in the source data cap output quality more than model choice does:

- Six properties share the placeholder tag `New Instance`, so no model can tell them
  apart. Giving them real names cut output from 2,425 tokens to 724 and restored a fault
  interlock the model had been omitting.
- `MCC Trip` has both `Trip Off` and `Trip On` set to `0`.

Output also varies between runs even at temperature 0, which is why the validator and the
engineer review both exist.

## Documentation

| File | Covers |
|---|---|
| [Reference/00-START-HERE.md](Reference/00-START-HERE.md) | Setup, running it, models, troubleshooting |
| [Reference/execution-plan.md](Reference/execution-plan.md) | Project plan and milestones |
| [docs/master-plan.md](docs/master-plan.md) | Wider programme plan |
| [docs/gemini-integration-flow.md](docs/gemini-integration-flow.md) | Integration flowchart |
