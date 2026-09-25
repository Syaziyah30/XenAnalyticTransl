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
    public double Temperature { get; set; } = 0.1;

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

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Output ceiling. Must be sent: providers that meter against a credit balance
    /// reserve the model's ENTIRE context window when this is absent, and reject the
    /// request with 402 even though the actual reply would cost a fraction of a cent.
    /// Must also leave room for reasoning models, which spend this budget thinking
    /// before they write anything - too low and the reply is cut off at zero characters.
    /// </summary>
    public int MaxTokens { get; set; } = 16000;

    /// <summary>
    /// Asks the router to turn the model's reasoning off (OpenRouter's `reasoning` field).
    ///
    /// Qwen3.8 Flash and friends are reasoning models: left alone they can burn the whole
    /// token budget thinking and return EMPTY content with finish_reason "length" - billed
    /// in full for nothing. Translation from a structured context pack does not need
    /// extended reasoning. Set false for endpoints that reject the parameter.
    /// </summary>
    public bool DisableReasoning { get; set; } = true;

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
                Model = "qwen/qwen3.8-flash", ApiKeyEnvVar = "OPENROUTER_API_KEY" },

        // Free tier runs on a shared pool and returns 429 whenever it is busy.
        // Fine when it works; not something to depend on for a test session.
        new() { ProviderName = "OpenRouter - Qwen3.8 27B (free, often rate-limited)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "qwen/qwen3.8-27b:free", ApiKeyEnvVar = "OPENROUTER_API_KEY" },

        // Max refuses `reasoning: {enabled:false}` with a 400 - reasoning is mandatory on
        // that endpoint. So leave it on and give the budget room for thinking AND answer.
        // Reasoning tokens bill as output, so a run here costs far more than Flash.
        new() { ProviderName = "OpenRouter - Qwen3.8 Max (coding, slower, pricier)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "qwen/qwen3.8-max-0902", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                DisableReasoning = false, MaxTokens = 32000 },

        // Claude direct, via Anthropic's OpenAI-compatibility layer.
        // Anthropic documents this as a testing/comparison path, not production - fine for
        // R&D model comparison. Model ids use dashes: claude-opus-5, claude-sonnet-5.
        new() { ProviderName = "Claude - Anthropic direct (Opus 5)",
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-opus-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY",
                SendTemperature = false, DisableReasoning = false },

        new() { ProviderName = "Claude - Anthropic direct (Sonnet 5, cheaper)",
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-sonnet-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY",
                SendTemperature = false, DisableReasoning = false },

        // Claude through the SAME OpenRouter key - no Anthropic account needed.
        new() { ProviderName = "OpenRouter - Claude Sonnet 5 (needs credit)",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "anthropic/claude-sonnet-5", ApiKeyEnvVar = "OPENROUTER_API_KEY",
                SendTemperature = false },

        new() { ProviderName = "Qwen - Alibaba Model Studio (Singapore)",
                BaseUrl = "https://WORKSPACE_ID.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1",
                Model = "qwen3.7-plus", ApiKeyEnvVar = "DASHSCOPE_API_KEY" },

        new() { ProviderName = "Qwen - Alibaba Model Studio (Virginia)",
                BaseUrl = "https://dashscope-us.aliyuncs.com/compatible-mode/v1",
                Model = "qwen3.7-plus", ApiKeyEnvVar = "DASHSCOPE_API_KEY" },
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

                var hint = (int)resp.StatusCode switch
                {
                    429 => " - the provider is rate-limited. Try another model, or wait a minute.",
                    402 => " - not enough credit for this model. It reserves MaxTokens x the output rate up front. Use a cheaper model, or add credit.",
                    401 => " - the key was rejected. Check it is for this provider and, on Alibaba, the right region.",
                    400 => " - the endpoint rejected a parameter. Some models require reasoning and refuse to have it disabled.",
                    _ => ""
                };
                return Fail(settings, sw,
                    $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}{hint} {Trim(raw, 500)}", raw);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail(settings, sw, $"Timed out after {settings.TimeoutSeconds}s.");
            }
            catch (OperationCanceledException)
            {
                return Fail(settings, sw, "Cancelled.");
            }
            catch (Exception ex)
            {
                return Fail(settings, sw, $"{ex.GetType().Name}: {ex.Message}");
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
            return Fail(settings, sw, $"Could not read the response envelope: {ex.Message}", raw);
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
            return Fail(settings, sw, $"Model returned empty content (finish_reason: {finish}).{advice}", raw);
        }

        // ---- parse the model's JSON answer ----
        TranslationResult? result;
        try
        {
            result = JsonSerializer.Deserialize<TranslationResult>(StripFences(content));
        }
        catch (Exception ex)
        {
            return Fail(settings, sw, $"Model did not return valid JSON: {ex.Message}", content);
        }

        if (result is null)
            return Fail(settings, sw, "Model returned an empty result.", content);

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
            RawResponse = content
        };
    }

    /// <summary>Some models wrap JSON in ```json fences despite being told not to.</summary>
    private static string StripFences(string s)
    {
        s = s.Trim();
        var m = Regex.Match(s, @"^```[a-zA-Z]*\s*(.*?)\s*```$", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : s;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static TranslationOutcome Fail(LlmSettings s, Stopwatch sw, string error, string? raw = null)
    {
        sw.Stop();
        return new TranslationOutcome
        {
            Provider = s.ProviderName,
            Model = s.Model,
            ElapsedMs = sw.ElapsedMilliseconds,
            Error = error,
            RawResponse = raw
        };
    }

    public void Dispose() => _http.Dispose();
}

