using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XenAnalyticTransl.AI;

/// Which endpoint to call, with what model and key.
public sealed class LlmSettings
{
    public string ProviderName { get; set; } = "";

    /// OpenAI-compatible base URL
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";

    /// Environment variable that holds the key. The key itself is never stored here.
    public string ApiKeyEnvVar { get; set; } = "";

    /// 0 = always take the most likely token. 
    public double Temperature { get; set; } = 0.0;


    /// Send `temperature` default == ON.
    public bool SendTemperature { get; set; } = true;

        /// Response_format = JSON. Default == OFF.
    public bool RequestJsonMode { get; set; } = false;

	/// TimeoutSeconds how many seconds the app waits for the AI's reply.
	/// TimeoutSeconds = 0 means app waiting until either: the AI sends its answer or user press cancel
	public int TimeoutSeconds { get; set; } = 0;

    /// Output ceiling. 
    /// 1 scenario's ~ runs 700-2,100 tokens with so 6,000 token = ample headroom. 
    public int MaxTokens { get; set; } = 6000;


	/// DisableReasoning = true means the model skip thinking. just give the answer.
	public bool DisableReasoning { get; set; } = true;

	/// Dictionary = Extra settings added to the request for Claude 5 to thinks by default.
    /// other model is not thinking. only Claude 5 is thinking
	public Dictionary<string, object>? ExtraBody { get; set; }

    /// Published USD price per 1M tokens, for the local spend estimate.
    public decimal InputUsdPerM { get; set; }
    public decimal OutputUsdPerM { get; set; }

    /// Reads the key from the environment
    public string? ResolveApiKey() =>
        string.IsNullOrWhiteSpace(ApiKeyEnvVar) ? null
            : Environment.GetEnvironmentVariable(ApiKeyEnvVar);
}

/// Starting points for the providers
public static class ProviderPresets
{
    public static IReadOnlyList<LlmSettings> All { get; } = new List<LlmSettings>
    {
        // ==================================================================
        // GROQ - the active free tier. 
        // ==================================================================

        new() { ProviderName = "Groq - Qwen3.8 27B (free)",
                BaseUrl = "https://api.groq.com/openai/v1",
                Model = "qwen/qwen3.8-27b", ApiKeyEnvVar = "GROQ_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m,
                DisableReasoning = false, MaxTokens = 4000 },

        new() { ProviderName = "Groq - GPT-OSS 120B (free)",
                BaseUrl = "https://api.groq.com/openai/v1",
                Model = "openai/gpt-oss-120b", ApiKeyEnvVar = "GROQ_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m,
                DisableReasoning = false, MaxTokens = 4000 },

        new() { ProviderName = "Groq - GPT-OSS 20B (free, fastest)",
                BaseUrl = "https://api.groq.com/openai/v1",
                Model = "openai/gpt-oss-20b", ApiKeyEnvVar = "GROQ_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m,
                DisableReasoning = false, MaxTokens = 4000 },

        // Llama on Groq == No longer supported free access [need to delet soon]

        new() { ProviderName = "[NO ACCESS] Groq - Llama 3.3 70B",
                BaseUrl = "https://api.groq.com/openai/v1",
                Model = "llama-3.3-70b-versatile", ApiKeyEnvVar = "GROQ_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m,
                DisableReasoning = false, MaxTokens = 4000 },

        new() { ProviderName = "[NO ACCESS] Groq - Llama 3.1 8",
                BaseUrl = "https://api.groq.com/openai/v1",
                Model = "llama-3.1-8b-instant", ApiKeyEnvVar = "GROQ_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m,
                DisableReasoning = false, MaxTokens = 4000 },

        // ==================================================================
        // OPENROUTER - All disabled 9 Oct 2026 OUT OF CREDIT
        // ==================================================================

        new() { ProviderName = "[NO TOKEN] OpenRouter - Qwen3.8 Flash ",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "qwen/qwen3.8-flash", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0.15m, OutputUsdPerM = 0.47m },


        new() { ProviderName = "[NO TOKEN] OpenRouter - Free",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "openrouter/free", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m },

        new() { ProviderName = "[NO TOKEN] OpenRouter - Qwen3.8 27B :free (404)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "qwen/qwen3.8-27b:free", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m },

        new() { ProviderName = "[NO TOKEN] OpenRouter - Qwen3.8 27B",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "qwen/qwen3.8-27b", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0.20m, OutputUsdPerM = 0.60m },


        new() { ProviderName = "[NO TOKEN] OpenRouter - Qwen3.8 Max ",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "qwen/qwen3.8-max-0902", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 2m, OutputUsdPerM = 6m,
                DisableReasoning = false, MaxTokens = 32000 },

        new() { ProviderName = "[NO TOKEN] OpenRouter - Gemini 3.5 Flash Lite ",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "google/gemini-3.5-flash-lite", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0.30m, OutputUsdPerM = 2.50m,
                DisableReasoning = false, MaxTokens = 10000 },

        new() { ProviderName = "[NO TOKEN] OpenRouter - Gemini 3.1 Flash Lite (cheapest)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "google/gemini-3.1-flash-lite", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0.30m, OutputUsdPerM = 1.20m },

        new() { ProviderName = "[NO TOKEN] OpenRouter - Claude Sonnet 5 (needs credit)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "anthropic/claude-sonnet-5", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 2m, OutputUsdPerM = 10m,
                SendTemperature = false },

        new() { ProviderName = "[NO TOKEN] OpenRouter - GPT 5.6 Luna (needs credit)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "openai/gpt-5.6-luna", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                InputUsdPerM = 0.2m, OutputUsdPerM = 1.2m },


        // ==================================================================
        // HUGGING FACE - All disabled 9 Oct 2026 OUT OF CREDIT
        // ==================================================================

        new() { ProviderName = "[NO TOKEN] HuggingFace - Llama 3.1 8B Instruct",
                BaseUrl = "https://router.huggingface.co/v1",
                Model = "meta-llama/Llama-3.1-8B-Instruct", ApiKeyEnvVar = "HUGGINGFACE_API_KEY",
                InputUsdPerM = 0m, OutputUsdPerM = 0m },

        // ==================================================================
        // Claude via Anthropic's - All disabled 9 Oct 2026 OUT OF CREDIT
        // ==================================================================
        new() { ProviderName = "[NO TOKEN] Claude - Anthropic direct (Opus 5)",
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-opus-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY",
                InputUsdPerM = 5m, OutputUsdPerM = 25m,
                SendTemperature = false, DisableReasoning = false,
                ExtraBody = new() { ["thinking"] = new { type = "disabled" } } },

        new() { ProviderName = "[NO TOKEN] Claude - Anthropic direct (Sonnet 5, cheaper)",
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-sonnet-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY",
                InputUsdPerM = 2m, OutputUsdPerM = 10m,
                SendTemperature = false, DisableReasoning = false,
                ExtraBody = new() { ["thinking"] = new { type = "disabled" } } },


	};
}


/// Calls any OpenAI-compatible chat-completions endpoint. 
public sealed class LlmTranslator : IDisposable
{
    private readonly HttpClient _http;

    public LlmTranslator(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();

		// Don't stop waiting after 100 seconds. Let the app decide when to stop.
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<TranslationOutcome> TranslateAsync(
        ContextPack pack, LlmSettings settings, CancellationToken ct = default)
    {
		// Remember which output was asked for (Pseudocode, Logic, SCL), even if the request fails.
		var outcome = await TranslateCoreAsync(pack, settings, ct).ConfigureAwait(false);
		outcome.Target = pack.TargetLanguage;
		return outcome;
    }


	// ===== REQUEST (sent to the AI) =====
	private async Task<TranslationOutcome> TranslateCoreAsync(
        ContextPack pack, LlmSettings settings, CancellationToken ct)
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
                new { role = "system", content = PromptBuilder.SystemPromptFor(pack.TargetLanguage) },
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

		// Save a copy of the message so the Request Sent tab can show it. The API key isn't in it.
		var requestJson = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

		// If the provider = "too many requests" (429), wait and try again, up to 6 times.
		// Groq's free plan only allows about 2 scenarios per minute, so later scenarios must wait their turn.
		const int maxAttempts = 6;
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
                    await Task.Delay(RetryDelay(resp, raw, attempt), ct).ConfigureAwait(false);
                    continue;
                }

				// Check if the error == money (no credit), by looking for words like "credit balance".
				// We can't rely on the error number: Claude uses 400 for "no credit" instead of 402.
				var lower = raw.ToLowerInvariant();
                var isBilling =
                    lower.Contains("credit balance") || lower.Contains("too low") ||
                    lower.Contains("purchase credits") || lower.Contains("plans & billing") ||
                    lower.Contains("insufficient") || lower.Contains("quota");

                var hint = isBilling
                    ? " - OUT OF CREDIT on this provider. The key is fine; the account has no balance. Add credit, or switch to a free provider."
                    : (int)resp.StatusCode switch
                    {
						400 => " - the endpoint rejected a parameter. Some models require reasoning and refuse to have it disabled.",
						401 => " - the key was rejected. Check it is for this provider and, on Alibaba, the right region.",
						402 => " - not enough credit for this model. It reserves MaxTokens x the output rate up front. Use a cheaper model, or add credit.",
						404 => " - no such model at this provider. The slug may have been withdrawn; a free variant can disappear while its model page still says Free.",
						429 => " - the provider is rate-limited. Try another model, or wait a minute.",

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

		// RESPONSE (received from the AI)
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

		// If the AI sends back an empty answer, find out why (usually it ran out of tokens while thinking).
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
			// The AI's answer wasn't clean JSON. Try to fix it:
			//   1. If it added extra text around the JSON, cut the JSON out
			//   2. If it put broken characters inside the text, repair them
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

        // Double-escaped newlines arrive as a literal "\n" and render as one long line.
        result.Code = CodeCleaner.UnescapeNewlines(result.Code);

		// Delete the AI's extra notes from the code. Keep only the real logic.
		var (cleaned2, removed) = CodeCleaner.Clean(result.Code);
        if (removed > 0)
        {
            result.Code = cleaned2;
            result.Warnings.Add(
                $"{removed} comment line(s) of model deliberation were stripped from the code. " +
                "The reasoning is in the explanation; only the logic and short labels are kept here.");
        }

		// Warn if AND and OR are mixed without brackets. It can change the meaning of the logic.
		foreach (var amb in CodeCleaner.FindAmbiguousPrecedence(result.Code))
            result.Warnings.Insert(0,
                "AMBIGUOUS PRECEDENCE - AND and OR at the same bracket level, so the order of " +
                $"evaluation is not what it looks like. Check the brackets: {amb}");

		// If the output is Pseudocode, check that no SCL code slipped in.
		if (string.Equals(pack.TargetLanguage, PromptBuilder.TargetPseudocode, StringComparison.OrdinalIgnoreCase))
        {
			// Remove all // comments from the pseudocode.
			(result.Code, _) = CodeCleaner.StripComments(result.Code);

            var leaks = CodeCleaner.FindSclSyntax(result.Code, pack.Terms.Select(t => t.Variable));
            if (leaks.Count > 0)
                result.Warnings.Insert(0,
                    "NOT PURE PSEUDOCODE - SCL found in the output: " + string.Join("; ", leaks) +
                    ". Re-run, or try a stronger model.");
        }

		// Check PLC tag names only for SCL. Pseudocode uses normal names, so skip the check.
		var unknown = PromptBuilder.UsesNames(pack.TargetLanguage)
            ? new List<string>()
            : TagValidator.FindUnknown(result, pack).ToList();

        sw.Stop();
        return new TranslationOutcome
        {
            Result = result,
            UnknownIdentifiers = unknown,
            Target = pack.TargetLanguage,
            Provider = settings.ProviderName,
            Model = settings.Model,
            ElapsedMs = sw.ElapsedMilliseconds,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            RawResponse = content,
            RequestBody = requestJson
        };
    }

	/// Fix line breaks that break the AI's JSON answer.
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

	/// Find the JSON answer when the AI adds extra text around it.
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
        return null;   
    }

    /// <summary>Some models wrap JSON in ```json fences despite being told not to.</summary>
    private static string StripFences(string s)
    {
        s = s.Trim();
        var m = Regex.Match(s, @"^```[a-zA-Z]*\s*(.*?)\s*```$", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : s;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";

	/// How long to wait before trying again. Uses the time the provider gives, up to 70 seconds.
	private static readonly Regex TryAgainIn =
        new(@"try again in\s+([0-9]+(?:\.[0-9]+)?)\s*s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static TimeSpan RetryDelay(HttpResponseMessage resp, string body, int attempt)
    {
        var cap = TimeSpan.FromSeconds(70);

        // The standard header first.
        var after = resp.Headers.RetryAfter;
        if (after?.Delta is { } d && d > TimeSpan.Zero)
            return d < cap ? d + TimeSpan.FromMilliseconds(500) : cap;

		// Read the wait time from Groq's message, e.g. "try again in 18.9s".
		var m = TryAgainIn.Match(body);
        if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs) && secs > 0)
        {
            var wait = TimeSpan.FromSeconds(secs + 0.5);   // clear the window, don't race it
            return wait < cap ? wait : cap;
        }

        return TimeSpan.FromSeconds(2 * attempt);          // nothing stated - plain backoff
    }

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