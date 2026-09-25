using System.Net.Http.Headers;
using System.Text.Json;

namespace XenAnalyticTransl.AI;

/// <summary>What the key has spent and what is left. OpenRouter only.</summary>
public sealed class AccountUsage
{
    public decimal Limit { get; init; }
    public decimal Used { get; init; }
    public decimal Remaining { get; init; }
    public bool HasLimit { get; init; }

    public int FreeRequestsUsed { get; init; }
    public int FreeRequestsLimit { get; init; }

    public DateTime? ExpiresAt { get; init; }
    public string? Error { get; init; }

    public bool Ok => Error is null;

    /// <summary>0-1 for a progress bar. Zero when the key has no limit set.</summary>
    public double UsedFraction =>
        HasLimit && Limit > 0 ? Math.Clamp((double)(Used / Limit), 0, 1) : 0;

    /// <summary>0-1 of today's free-model request allowance.</summary>
    public double FreeUsedFraction =>
        FreeRequestsLimit > 0
            ? Math.Clamp((double)FreeRequestsUsed / FreeRequestsLimit, 0, 1)
            : 0;

    /// <summary>"0.2% used" - shows more decimals while the number is tiny.</summary>
    public static string Percent(double fraction)
    {
        var pct = fraction * 100;
        if (pct <= 0) return "0% used";
        if (pct < 0.1) return "under 0.1% used";
        return pct < 10 ? $"{pct:0.0}% used" : $"{pct:0}% used";
    }

    public string Summary()
    {
        if (!Ok) return Error!;

        var money = HasLimit
            ? $"${Used:0.000} of ${Limit:0.00} used  -  ${Remaining:0.000} left"
            : $"${Used:0.000} used  -  no key limit set";

        var free = FreeRequestsLimit > 0
            ? $"   |   free models: {FreeRequestsLimit - FreeRequestsUsed} of {FreeRequestsLimit} left today"
            : "";

        return money + free;
    }
}

public static class AccountInfo
{
    public static bool Supports(LlmSettings s) =>
        s.BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads GET /key. Never throws - failures come back on Error.</summary>
    public static async Task<AccountUsage> FetchAsync(
        LlmSettings settings, HttpClient http, CancellationToken ct = default)
    {
        if (!Supports(settings))
            return new AccountUsage { Error = "Usage reporting is only available for OpenRouter." };

        var key = settings.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return new AccountUsage { Error = "No API key." };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{settings.BaseUrl.TrimEnd('/')}/key");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var resp = await http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return new AccountUsage { Error = $"Usage lookup failed: HTTP {(int)resp.StatusCode}" };

            using var doc = JsonDocument.Parse(body);
            var d = doc.RootElement.GetProperty("data");

            decimal Num(string name) =>
                d.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number
                    ? e.GetDecimal() : 0m;

            var hasLimit = d.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number;

            int freeUsed = 0, freeLimit = 0;
            if (d.TryGetProperty("free_model_daily_requests", out var fr) && fr.ValueKind == JsonValueKind.Object)
            {
                if (fr.TryGetProperty("used", out var u)) freeUsed = u.GetInt32();
                if (fr.TryGetProperty("limit", out var l)) freeLimit = l.GetInt32();
            }

            DateTime? expires = null;
            if (d.TryGetProperty("expires_at", out var ex) && ex.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ex.GetString(), out var parsed)) expires = parsed;

            return new AccountUsage
            {
                HasLimit = hasLimit,
                Limit = hasLimit ? lim.GetDecimal() : 0m,
                Used = Num("usage"),
                Remaining = Num("limit_remaining"),
                FreeRequestsUsed = freeUsed,
                FreeRequestsLimit = freeLimit,
                ExpiresAt = expires
            };
        }
        catch (Exception ex)
        {
            return new AccountUsage { Error = $"Usage lookup failed: {ex.Message}" };
        }
    }
}
