# Master Plan — Gen AI Integration in PLC Logic Builder

Consolidated plan. Supersedes earlier drafts.
Task numbers refer to planner rows 21–32.

---

## 1. What we are building

**PLC Logic Builder** is an existing Windows desktop application. An engineer
writes a control scenario in natural language, defines terms and properties, and
the app maps those terms to real PLC variables. We are adding a
**Request AI Translator** button that reads the scenario plus its properties and
generates **SCL (Siemens Structured Text)**, with an explanation, ready for the
engineer to review and save into the project.

### Known and fixed

| Item | Value |
|---|---|
| Host application | PLC Logic Builder, Windows desktop, .NET |
| Target output | SCL — Siemens Structured Text |
| Trigger | "Request AI Translator" button |
| Input | Scenario text + terms + properties + PLC variable associations |
| Output | Generated logic + explanation, then Copy or Save to Project |
| Repository | `D:\XenAnalyticTransl` → Azure DevOps (authoritative) + GitHub (mirror) |
| Stack | C# / .NET, Visual Studio |

---

## 2. Open decisions — these block real work

| # | Question | Blocks | Status |
|---|---|---|---|
| 1 | May scenario text and PLC tag names leave the network? | Provider choice, whole architecture | **Open** |
| 2 | Must an engineer approve generated code before use? | Depth of validation, UI gating | **Open** |
| 3 | Does the app already translate deterministically? | Whether AI replaces or supplements | **Open** |

**Decision 1 is the important one.** Everything in section 5 can proceed with
invented scenarios while it is unresolved, but nothing can go to production
until it is answered.

### How decision 1 resolves

| Answer | Provider | Consequence |
|---|---|---|
| Data may leave | Gemini, Azure AI Foundry, or Qwen via Alibaba Cloud | Low effort, best quality |
| Data may not leave | Self-hosted Qwen3.8-27B or similar | GPU server needed, ongoing ops, weaker output |

---

## 3. Architecture

```
XenAnalyticTransl.UI          the existing app — reads panels, shows results
        |
        v
XenAnalyticTransl.AI          class library: context pack, prompt, call, validate
        |
        v
Internal AI service           holds the API key, logs usage
        |
        v
Model provider                Gemini / Azure AI Foundry / Qwen / self-hosted
```

### Three rules

1. **The UI never calls the model directly.** It builds a context pack, hands it
   to the class library, and displays what comes back.
2. **The API key never ships in the desktop app.** It lives on an internal
   service. A key inside a distributed binary can be extracted in minutes.
3. **Build against the OpenAI-compatible interface.** Qwen, Azure AI Foundry,
   OpenRouter and self-hosted vLLM all speak it. Switching provider then becomes
   a base URL and a model name rather than a rewrite — which keeps decision 1
   from blocking the build.

### The context pack

The single biggest quality lever. The app already holds everything needed:

```json
{
  "equipment": "Mtr01",
  "target_language": "SCL",
  "scenario": "When in Auto Mode, Manual Operation position will change...",
  "terms": [
    { "name": "PLC Initializing", "variable": "Sys_plsInit", "value": 1 }
  ],
  "timers": ["RunFailAlarmTimer", "TripAlarmTimer"]
}
```

Every highlighted phrase in the scenario becomes a glossary entry. Supply this
and the model uses your real variable names. Omit it and the model invents
plausible ones, which is worse than an outright failure.

### The response contract

```json
{
  "code": "IF AutoMode THEN ...",
  "explanation": "Lines 2-6 map the Auto Mode condition...",
  "variables_used": ["AutoMode", "ManualOperation", "Sys_plsInit"],
  "warnings": ["Scenario does not state behaviour when both conditions are true"]
}
```

`variables_used` feeds the validator. `warnings` surfaces ambiguity in the
scenario instead of letting the model guess silently.

---

## 4. Two safety gates

Neither is optional, because PLC code drives physical equipment.

1. **Identifier validation.** Every entry in `variables_used` must exist in the
   tag table. No AI involved — a set lookup. This catches hallucinated variable
   names, the most common and most dangerous failure mode, because the code
   still compiles.
2. **Engineer review.** Generated code reaches the project only through an
   explicit user action. Never auto-save.

---

## 5. Execution

### Phase A — Prove it works (tasks 21, 23, 24)

Do this with **invented scenarios**, on a free API key. No real plant data, so
decision 1 does not block you.

| Step | Work |
|---|---|
| A1 | Write a dummy motor scenario with fake tags, modelled on Mtr01 |
| A2 | Hand-build its context pack as JSON |
| A3 | Write the prompt: system role, SCL dialect, conventions, few-shot examples |
| A4 | Call the model from the console app, print the SCL |
| A5 | Paste the output into TIA Portal. Does it compile? Is the logic right? |
| A6 | Iterate the prompt until output is consistently usable |

**Gate:** if A5 never produces usable SCL after serious prompt work, stop and
report that. Better to find out in week one than in month four.

### Phase B — Make it a component (task 25)

| Step | Work |
|---|---|
| B1 | Create `XenAnalyticTransl.AI` class library |
| B2 | Move models, prompt builder and client into it |
| B3 | Add schema validation on the response |
| B4 | Add identifier validation against the tag table |
| B5 | Unit tests using recorded responses — no network, no cost |

### Phase C — Measure (task 27)

| Step | Work |
|---|---|
| C1 | Build an evaluation set of 30–50 scenarios with known-good output |
| C2 | Score: compiles, logic correct, consistent across repeat runs |
| C3 | Measure latency and cost per call, project monthly cost |
| C4 | Tune, re-run, record the deltas |

### Phase D — Wire the UI (task 26)

| Step | Work |
|---|---|
| D1 | Build the context pack from the live panels |
| D2 | Async button handler with progress and cancel |
| D3 | Result panel: generated logic, explanation tab, copy, save |
| D4 | Error and fallback states — never fail silently |
| D5 | Surface unknown identifiers as warnings on the result |

### Phase E — Production (tasks 28–32)

| Step | Work |
|---|---|
| E1 | Stand up the internal AI service holding the key |
| E2 | Deployment and environment configuration |
| E3 | Server-side integration and load testing |
| E4 | User acceptance testing with 3–5 engineers |
| E5 | Documentation: user guide, ops runbook, prompt rationale |

---

## 6. Where to start

1. Get a free Gemini key at aistudio.google.com — about two minutes
2. Write one dummy motor scenario with invented tags
3. Build its context pack by hand
4. Write the prompt and call the model from the console app
5. Paste the SCL into TIA Portal and see whether it compiles

Steps 1–5 are roughly a day's work and tell you more than a month of design.

In parallel, ask the person who owns your IP or source-control policy:

> *"Can plant scenario text and PLC tag names be sent to an external AI service?"*

---

## 7. Decision log

Record choices here as they are made, with the date and the reason.

| Date | Decision | Reason |
|---|---|---|
| 2026-09-22 | Repo `XenAnalyticTransl`, DevOps authoritative, GitHub mirror | Team server is DevOps |
| 2026-09-22 | Branch `main` | Matches GitHub default and DevOps convention |
| | Data may / may not leave network | *pending* |
| | Provider | *pending* |
| | Human review mandatory? | *pending* |
