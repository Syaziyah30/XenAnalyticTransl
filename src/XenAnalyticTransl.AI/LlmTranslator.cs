using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XenAnalyticTransl.AI;

/// <summary>Which endpoint to call, with what model and key.</summary>
public sealed class LlmSettings
{
    public string ProviderName { get; set; } = "";
    /// <summary>OpenAI-compatible base URL, ending in /v1 (no trailing slash).</summary>
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Environment variable that holds the key. The key itself is never stored here.</summary>
    public string ApiKeyEnvVar { get; set; } = "";
    /// <summary>
    /// 0 = always take the most likely token. Does not make output perfectly
    /// reproducible - GPU batching and provider routing still vary - but it removes
    /// the sampling randomness that is ours to control.
    /// </summary>
    public double Temperature { get; set; } = 0.0;

    /// <summary>
    /// Whether to send `temperature` at all. Default ON.
    ///
    /// The Claude 5 family removed sampling parameters - sending temperature returns
    /// 400 "`temperature` is deprecated for this model". Set false for those endpoints.
    /// </summary>
    public bool SendTemperature { get; set; } = true;

    /// <summary>
    /// Sends response_format = json_object. Default OFF.
    ///
    /// On a router like OpenRouter this narrows provider selection to those supporting
    /// the parameter, which can land you on a rate-limited one and fail with 429 while
    /// the same model succeeds without it. The system prompt already demands bare JSON
    /// and the parser strips code fences, so this buys very little. Turn it on only for
    /// a direct provider endpoint that documents support for it.
    /// </summary>
    public bool RequestJsonMode { get; set; } = false;

    /// <summary>
    /// Seconds to wait for a reply. ZERO OR LESS MEANS NO DEADLINE - the call runs until
    /// the model answers or the user presses Cancel.
    ///
    /// No deadline is the default: a slow model is still a useful model, and cutting it
    /// off wastes the tokens already generated. The cost is that a dead connection waits
    /// forever, so Cancel is the only way out of a stall.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Output ceiling. Must be sent: providers that meter against a credit balance
    /// reserve the model's ENTIRE context window when this is absent, and reject the
    /// request with 402 even though the actual reply would cost a fraction of a cent.
    /// Must also leave room for reasoning models, which spend this budget thinking
    /// before they write anything - too low and the reply is cut off at zero characters.
    /// </summary>
    /// Sized to the job: one scenario's SCL plus explanation runs 700-2,100 tokens with
    /// thinking off, so 6,000 is ample headroom. Keep it tight - providers reserve
    /// max_tokens x the output rate against your balance before running anything, so an
    /// over-generous ceiling fails with 402 on a small balance for no benefit.
    /// Models that must think need far more (see the Qwen Max preset).
    public int MaxTokens { get; set; } = 6000;

    /// <summary>
    /// Asks the router to turn the model's reasoning off (OpenRouter's `reasoning` field).
    ///
    /// Qwen3.8 Flash and friends are reasoning models: left alone they can burn the whole
    /// token budget thinking and return EMPTY content with finish_reason "length" - billed
    /// in full for nothing. Translation from a structured context pack does not need
    /// extended reasoning. Set false for endpoints that reject the parameter.
    /// </summary>
    public bool DisableReasoning { get; set; } = true;

    /// <summary>
    /// Extra fields merged into the request body, for provider-specific parameters.
    ///
    /// Claude 5 models think by default, which on this task triples the output tokens
    /// and the wall time for no measurable gain - translation from a structured context
    /// pack is not a reasoning problem. Pass {"thinking": {type = "disabled"}} to turn
    /// it off. Unknown fields are ignored by most endpoints, so this is safe per preset.
    /// </summary>
    public Dictionary<string, object>? ExtraBody { get; set; }

    /// <summary>Published USD price per 1M tokens, for the local spend estimate.
    /// Only some providers report a balance; for the rest we work it out from usage.</summary>
    public decimal InputUsdPerM { get; set; }
    public decimal OutputUsdPerM { get; set; }

    /// <summary>Reads the key from the environment at call time. Returns null when unset.</summary>
    public string? ResolveApiKey() =>
        string.IsNullOrWhiteSpace(ApiKeyEnvVar) ? null
            : Environment.GetEnvironmentVariable(ApiKeyEnvVar);
}

/// <summary>Starting points for the providers worth trying. All speak the OpenAI protocol.</summary>
public static class ProviderPresets
{
    public static IReadOnlyList<LlmSettings> All { get; } = new List<LlmSettings>
    {
        new() { ProviderName = "OpenRouter - Qwen3.8 Flash (cheap, reliable)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "qwen/qwen3.8-flash", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0.15m, OutputUsdPerM = 0.47m },

        // Routes to whatever is zero-cost right now. Named free models come and go -
        // qwen3.8-27b:free was withdrawn mid-project and started 404ing - so prefer the
        // router over any single :free slug. Capped around 50 requests a day, which is
        // roughly seven full seven-scenario runs.
        new() { ProviderName = "OpenRouter - Free (auto-router)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "openrouter/free", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m },

        // Kept so the free tier can be retried - these do get restored. As of 8 Oct 2026
        // it returns 404 on our own key even though openrouter.ai shows "PRICE: Free".
        new() { ProviderName = "OpenRouter - Qwen3.8 27B :free (404 as of 8 Oct, retry)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "qwen/qwen3.8-27b:free", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m },

        // Paid 27B. The ":free" variant of this slug is NOT usable: openrouter.ai still
        // shows it as "PRICE: Free", but requesting it returns
        //   404 "This model is unavailable for free. The paid version is available now"
        // Tested 8 Oct 2026 on our own key. Trust the endpoint, not the model page.
        new() { ProviderName = "OpenRouter - Qwen3.8 27B (paid)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "qwen/qwen3.8-27b", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0.20m, OutputUsdPerM = 0.60m },

        // Max refuses `reasoning: {enabled:false}` with a 400 - reasoning is mandatory on
        // that endpoint. So leave it on and give the budget room for thinking AND answer.
        // Reasoning tokens bill as output, so a run here costs far more than Flash.
        new() { ProviderName = "OpenRouter - Qwen3.8 Max (coding, slower, pricier)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "qwen/qwen3.8-max-0902", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 2m, OutputUsdPerM = 6m,
                DisableReasoning = false, MaxTokens = 32000 },

        // Gemini through the same OpenRouter key. Gemini accepts temperature, so that
        // Need payment [soon deleted]
        //new() { ProviderName = "OpenRouter - Gemini 3.5 Flash Lite",
        //        BaseUrl = "https://openrouter.ai/api/v1",
        //        Model = "google/gemini-3.5-flash-lite", ApiKeyEnvVar = "OPENROUTER_API_KEY",
        //        InputUsdPerM = 0.30m, OutputUsdPerM = 2.50m,
        //        DisableReasoning = false, MaxTokens = 10000 },

        new() { ProviderName = "OpenRouter - Gemini 3.1 Flash Lite (cheapest)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "google/gemini-3.1-flash-lite", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0.30m, OutputUsdPerM = 1.20m },

        // Hugging Face Inference Providers - an OpenAI-compatible router over Together,
        // fal, Replicate and others. Has a free tier; paid rates are the provider's own,
        // so the cost estimate below is left at zero rather than guessed.
        new() { ProviderName = "HuggingFace - Llama 3.1 8B Instruct",
                BaseUrl = "https://router.huggingface.co/v1",
                Model = "meta-llama/Llama-3.1-8B-Instruct", ApiKeyEnvVar = "HUGGINGFACE_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m },

        // Claude direct, via Anthropic's OpenAI-compatibility layer.
        // Anthropic documents this as a testing/comparison path, not production - fine for
        // R&D model comparison. Model ids use dashes: claude-opus-5, claude-sonnet-5.
        new() { ProviderName = "Claude - Anthropic direct (Opus 5)",
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-opus-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY",
                InputUsdPerM = 5m, OutputUsdPerM = 25m,
                SendTemperature = false, DisableReasoning = false,
                ExtraBody = new() { ["thinking"] = new { type = "disabled" } } },

        new() { ProviderName = "Claude - Anthropic direct (Sonnet 5, cheaper)",
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-sonnet-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY",
                InputUsdPerM = 2m, OutputUsdPerM = 10m,
                SendTemperature = false, DisableReasoning = false,
                ExtraBody = new() { ["thinking"] = new { type = "disabled" } } },

        // Claude through the SAME OpenRouter key - no Anthropic account needed.
        // ERROR in running cpde. insufficient credit. [NEED TO REMOVE SOON]
        //new() { ProviderName = "OpenRouter - Claude Sonnet 5 (needs credit)",
        //        BaseUrl = "https://openrouter.ai/api/v1",
        //        Model = "anthropic/claude-sonnet-5", ApiKeyEnvVar = "OPENROUTER_API_KEY",
        //        InputUsdPerM = 2m, OutputUsdPerM = 10m,
        //        SendTemperature = false },

        // GPT through the SAME OpenRouter key - no Anthropic account needed.
        new() { ProviderName = "OpenRouter - GPT 5.6 Luna (needs credit)",
				BaseUrl = "https://openrouter.ai/api/v1",
				Model = "openai/gpt-5.6-luna", ApiKeyEnvVar = "OPENROUTER_API_KEY",
				InputUsdPerM = 0.2m, OutputUsdPerM = 1.2m},
	};
}

/// <summary>
/// Calls any OpenAI-compatible chat-completions endpoint. Deliberately provider-neutral:
/// swapping Qwen for a self-hosted model, or for another vendor, is a BaseUrl and Model
/// change, not a rewrite.
/// </summary>
public sealed class LlmTranslator : IDisposable
{
    private readonly HttpClient _http;

    public LlmTranslator(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();

        // HttpClient defaults to 100 seconds and throws its own TaskCanceledException,
        // which used to fire before our own deadline and get reported with the wrong
        // number. Timing is this class's business, so disable the built-in one.
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<TranslationOutcome> TranslateAsync(
        ContextPack pack, LlmSettings settings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var key = settings.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return Fail(settings, sw, $"No API key. Set the environment variable {settings.ApiKeyEnvVar}, then restart the app.");
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || settings.BaseUrl.Contains("WORKSPACE_ID"))
            return Fail(settings, sw, "Base URL is not configured. Replace WORKSPACE_ID with your real workspace id.");

        var body = new Dictionary<string, object>
        {
            ["model"] = settings.Model,
            ["max_tokens"] = settings.MaxTokens,
            ["messages"] = new object[]
            {
                new { role = "system", content = PromptBuilder.SystemPrompt },
                new { role = "user",   content = PromptBuilder.BuildUserMessage(pack) }
            }
        };

        if (settings.SendTemperature)
            body["temperature"] = settings.Temperature;

        if (settings.RequestJsonMode)
            body["response_format"] = new { type = "json_object" };

        if (settings.DisableReasoning)
            body["reasoning"] = new { enabled = false };

        if (settings.ExtraBody is not null)
            foreach (var kv in settings.ExtraBody) body[kv.Key] = kv.Value;

        // Kept so the app can show exactly what was sent. Safe to display: the API key
        // travels in the Authorization header, never in the body.
        var requestJson = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        // Shared provider pools return 429 intermittently, so a transient blip should
        // not surface as a failure. Two retries with backoff, then give up.
        const int maxAttempts = 3;
        string raw = "";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/chat/completions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                req.Content = JsonContent.Create(body);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (settings.TimeoutSeconds > 0)                      // 0 = no deadline
                    timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

                using var resp = await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);
                raw = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode) break;

                var retryable = (int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500;
                if (retryable && attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                    continue;
                }

                // Anthropic reports an empty balance as 400, not 402, so the status code
                // alone would send the reader hunting for a bad parameter. Check the body
                // for billing wording before falling back to the per-status hint.
                var lower = raw.ToLowerInvariant();
                var isBilling =
                    lower.Contains("credit balance") || lower.Contains("too low") ||
                    lower.Contains("purchase credits") || lower.Contains("plans & billing") ||
                    lower.Contains("insufficient") || lower.Contains("quota");

                var hint = isBilling
                    ? " - OUT OF CREDIT on this provider. The key is fine; the account has no balance. Add credit, or switch to a free provider."
                    : (int)resp.StatusCode switch
                    {
                        429 => " - the provider is rate-limited. Try another model, or wait a minute.",
                        402 => " - not enough credit for this model. It reserves MaxTokens x the output rate up front. Use a cheaper model, or add credit.",
                        401 => " - the key was rejected. Check it is for this provider and, on Alibaba, the right region.",
                        400 => " - the endpoint rejected a parameter. Some models require reasoning and refuse to have it disabled.",
                        404 => " - no such model at this provider. The slug may have been withdrawn; a free variant can disappear while its model page still says Free.",
                        _ => ""
                    };
                return Fail(settings, sw,
                    $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}{hint} {Trim(raw, 500)}", raw);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail(settings, sw,
                    $"Timed out after {sw.Elapsed.TotalSeconds:0}s (limit {settings.TimeoutSeconds}s).",
                    null, requestJson);
            }
            catch (OperationCanceledException)
            {
                return Fail(settings, sw, "Cancelled.", null, requestJson);
            }
            catch (Exception ex)
            {
                return Fail(settings, sw, $"{ex.GetType().Name}: {ex.Message}", null, requestJson);
            }
        }

        // ---- unwrap the OpenAI envelope ----
        string content;
        int promptTokens = 0, completionTokens = 0;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            content = root.GetProperty("choices")[0].GetProperty("message")
                          .GetProperty("content").GetString() ?? "";

            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ctk)) completionTokens = ctk.GetInt32();
            }
        }
        catch (Exception ex)
        {
            return Fail(settings, sw, $"Could not read the response envelope: {ex.Message}", raw, requestJson);
        }

        // A reasoning model that runs out of budget mid-thought returns HTTP 200 with an
        // empty content string. Say so plainly rather than failing on "invalid JSON".
        if (string.IsNullOrWhiteSpace(content))
        {
            var finish = "unknown";
            try
            {
                using var d = JsonDocument.Parse(raw);
                if (d.RootElement.GetProperty("choices")[0].TryGetProperty("finish_reason", out var f))
                    finish = f.GetString() ?? "unknown";
            }
            catch { /* best effort */ }

            var advice = finish == "length"
                ? $" It used the whole {settings.MaxTokens}-token budget thinking and never wrote an answer. Raise MaxTokens, or keep DisableReasoning on."
                : "";
            return Fail(settings, sw, $"Model returned empty content (finish_reason: {finish}).{advice}", raw, requestJson);
        }

        // ---- parse the model's JSON answer ----
        TranslationResult? result;
        var cleaned = StripFences(content);
        try
        {
            result = JsonSerializer.Deserialize<TranslationResult>(cleaned);
        }
        catch (JsonException)
        {
            // Two more chances, in order of likelihood:
            //   1. prose wrapped around the object - dig the object out
            //   2. raw control characters inside a string literal - escape them
            var salvaged = ExtractJsonObject(cleaned) ?? cleaned;
            try
            {
                result = JsonSerializer.Deserialize<TranslationResult>(salvaged);
            }
            catch (JsonException)
            {
                try
                {
                    result = JsonSerializer.Deserialize<TranslationResult>(RepairControlChars(salvaged));
                }
                catch (Exception ex)
                {
                    return Fail(settings, sw, $"Model did not return valid JSON: {ex.Message}", content, requestJson);
                }
            }
        }
        catch (Exception ex)
        {
            return Fail(settings, sw, $"Model did not return valid JSON: {ex.Message}", content, requestJson);
        }

        if (result is null)
            return Fail(settings, sw, "Model returned an empty result.", content, requestJson);

        // Some models write their deliberation into the code as comments however firmly
        // the prompt forbids it. Strip it; note it so the behaviour stays visible.
        var (cleaned2, removed) = CodeCleaner.Clean(result.Code);
        if (removed > 0)
        {
            result.Code = cleaned2;
            result.Warnings.Add(
                $"{removed} comment line(s) of model deliberation were stripped from the code. " +
                "The reasoning is in the explanation; only SCL and scenario comments are kept here.");
        }

        var unknown = TagValidator.FindUnknown(result, pack).ToList();

        sw.Stop();
        return new TranslationOutcome
        {
            Result = result,
            UnknownIdentifiers = unknown,
            Provider = settings.ProviderName,
            Model = settings.Model,
            ElapsedMs = sw.ElapsedMilliseconds,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            RawResponse = content,
            RequestBody = requestJson
        };
    }

    /// <summary>
    /// Escapes raw control characters that appear INSIDE a JSON string literal.
    ///
    /// Smaller models routinely emit a code block with real newlines inside the "code"
    /// value, which is invalid JSON even though the intent is obvious. Rather than fail
    /// the whole run, walk the text tracking whether we are inside a string and escape
    /// the offending characters. Leaves well-formed JSON untouched.
    /// </summary>
    private static string RepairControlChars(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 64);
        bool inString = false, escaped = false;

        foreach (var ch in s)
        {
            if (escaped) { sb.Append(ch); escaped = false; continue; }

            switch (ch)
            {
                case '\\' when inString: sb.Append(ch); escaped = true; break;
                case '"': inString = !inString; sb.Append(ch); break;
                case '\n' when inString: sb.Append("\\n"); break;
                case '\r' when inString: sb.Append("\\r"); break;
                case '\t' when inString: sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pulls the first complete JSON object out of a reply that also contains prose.
    ///
    /// Models often explain their reasoning before answering, however firmly the prompt
    /// says otherwise - Claude in particular writes a paragraph of analysis and then the
    /// object. Scans for a balanced pair of braces, ignoring any inside string literals.
    /// Returns null when there is nothing object-shaped to find.
    /// </summary>
    private static string? ExtractJsonObject(string s)
    {
        var start = s.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inString = false, escaped = false;

        for (var i = start; i < s.Length; i++)
        {
            var ch = s[i];

            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inString) { escaped = true; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return s[start..(i + 1)];
        }
        return null;   // unbalanced - truncated reply
    }

    /// <summary>Some models wrap JSON in ```json fences despite being told not to.</summary>
    private static string StripFences(string s)
    {
        s = s.Trim();
        var m = Regex.Match(s, @"^```[a-zA-Z]*\s*(.*?)\s*```$", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : s;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static TranslationOutcome Fail(LlmSettings s, Stopwatch sw, string error, string? raw = null, string? request = null)
    {
        sw.Stop();
        return new TranslationOutcome
        {
            Provider = s.ProviderName,
            Model = s.Model,
            ElapsedMs = sw.ElapsedMilliseconds,
            Error = error,
            RawResponse = raw,
            RequestBody = request
        };
    }

    public void Dispose() => _http.Dispose();
}











