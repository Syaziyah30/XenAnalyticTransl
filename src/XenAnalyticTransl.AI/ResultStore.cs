using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace XenAnalyticTransl.AI;

/// <summary>
/// Saves AI translations in SQL Server and loads them back without calling the AI. On a
/// free tier the daily token allowance is the limit on how much can be tested; loading
/// saved results stretches it. One row per scenario and output type: a new translation
/// replaces the old one.
///
/// Table: dbo.OutputXenTrans in the XenCreator database (Reference/sql/OutputXenTrans.sql).
///
/// The connection string comes from the XENTRANS_DB environment variable, like the API
/// keys, and is never written to the code, the repo or any message - it carries the
/// database password.
/// </summary>
public sealed class ResultStore
{
    public const string EnvVar = "XENTRANS_DB";
    private const string Table = "dbo.OutputXenTrans";

    private readonly string _connectionString;

    public ResultStore(string connectionString) => _connectionString = connectionString;

    /// <summary>Null when XENTRANS_DB is not set - the app then runs without saving.</summary>
    public static ResultStore? FromEnvironment()
    {
        var cs = Environment.GetEnvironmentVariable(EnvVar);
        return string.IsNullOrWhiteSpace(cs) ? null : new ResultStore(cs);
    }

    // ------------------------------------------------------------------ fingerprint

    /// <summary>
    /// A SHA-256 fingerprint of everything that decides the answer: which AI is asked,
    /// the output type, the translation rules, and the exact message - scenario text,
    /// the scenarios it refers to, and the property list. Change any of it and the
    /// fingerprint changes, so a stale result is never reused.
    ///
    /// Built from the same PromptBuilder calls the translator sends, so it cannot drift
    /// from what the AI actually receives. Line endings are normalised so the same
    /// request gives the same key on any machine.
    /// </summary>
    public static string ComputeKey(ContextPack pack, LlmSettings settings)
    {
        var sb = new StringBuilder();
        sb.Append("v1\n");                                        // bump to invalidate every saved row
        sb.Append(settings.BaseUrl.Trim().TrimEnd('/')).Append('\n');
        sb.Append(settings.Model.Trim()).Append('\n');
        sb.Append(pack.TargetLanguage).Append('\n');
        sb.Append(PromptBuilder.SystemPromptFor(pack.TargetLanguage)).Append('\n');
        sb.Append(PromptBuilder.BuildUserMessage(pack));

        var text = sb.ToString().Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ health check

    /// <summary>
    /// Connects and confirms the table exists. Returns null when all is well, otherwise a
    /// plain-language reason. Run once per batch so an unreachable server costs one
    /// timeout, not one per scenario.
    /// </summary>
    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = new SqlCommand($"SELECT OBJECT_ID(N'{Table}', N'U')", cn);
            var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

            return id is null or DBNull
                ? $"Connected to database '{cn.Database}', but table {Table} does not exist. " +
                  "Run Reference/sql/OutputXenTrans.sql first."
                : null;
        }
        catch (SqlException ex)
        {
            return "Cannot reach the results database: " + Describe(ex);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // A malformed connection string lands here. Never echo the string itself -
            // it holds the password.
            return $"The {EnvVar} connection string is not valid: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ load

    /// <summary>
    /// The saved result for this scenario - same equipment, name and output type - or null
    /// if there is none. Matched by name, not fingerprint, so a result is shown even after
    /// the scenario changed; <paramref name="currentKey"/> marks it OutOfDate when it did.
    /// SCL results are re-checked against the current tag table, so the ✓ / ! marker stays honest.
    /// </summary>
    public async Task<TranslationOutcome?> TryLoadAsync(string currentKey, ContextPack pack, string scenarioName,
                                                        CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(
            $"""
            SELECT TOP (1) Provider, Model, Code, Explanation, VariablesUsed, Warnings,
                           PromptTokens, CompletionTokens, ElapsedMs, CreatedAt, CacheKey
            FROM {Table}
            WHERE Equipment = @equipment AND ScenarioName = @name AND OutputTarget = @target
            ORDER BY CreatedAt DESC, Id DESC
            """, cn);
        AddScenarioParams(cmd, pack, scenarioName);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;

        var result = new TranslationResult
        {
            Code = r.GetString(2),
            Explanation = r.IsDBNull(3) ? "" : r.GetString(3),
            VariablesUsed = FromJson(r.IsDBNull(4) ? null : r.GetString(4)),
            Warnings = FromJson(r.IsDBNull(5) ? null : r.GetString(5)),
        };

        var unknown = PromptBuilder.UsesNames(pack.TargetLanguage)
            ? new List<string>()
            : TagValidator.FindUnknown(result, pack).ToList();

        return new TranslationOutcome
        {
            Result = result,
            UnknownIdentifiers = unknown,
            Target = pack.TargetLanguage,
            Provider = r.GetString(0),
            Model = r.GetString(1),
            PromptTokens = r.IsDBNull(6) ? 0 : r.GetInt32(6),
            CompletionTokens = r.IsDBNull(7) ? 0 : r.GetInt32(7),
            ElapsedMs = r.IsDBNull(8) ? 0 : r.GetInt32(8),
            FromDatabase = true,
            SavedAt = DateTime.SpecifyKind(r.GetDateTime(9), DateTimeKind.Utc).ToLocalTime(),
            OutOfDate = !string.Equals(r.GetString(10).Trim(), currentKey, StringComparison.OrdinalIgnoreCase),
        };
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// Saves a fresh, successful result, REPLACING whatever was saved for this scenario -
    /// same equipment, name and output type. One row per scenario, always the latest; no
    /// history. Delete and insert run in one transaction, so a failed save leaves the old
    /// row in place. Failures and loaded results are never saved.
    /// </summary>
    public async Task SaveAsync(string key, ContextPack pack, string scenarioName,
                                TranslationOutcome outcome, CancellationToken ct = default)
    {
        if (!outcome.Ok || outcome.FromDatabase) return;
        var r = outcome.Result!;

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var del = new SqlCommand(
            $"DELETE FROM {Table} WHERE Equipment = @equipment AND ScenarioName = @name AND OutputTarget = @target",
            cn, tx))
        {
            AddScenarioParams(del, pack, scenarioName);
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = new SqlCommand(
            $"""
            INSERT INTO {Table}
                (CacheKey, Equipment, ScenarioName, ScenarioText, OutputTarget, Provider, Model,
                 Code, Explanation, VariablesUsed, Warnings, PromptTokens, CompletionTokens, ElapsedMs)
            VALUES
                (@key, @equipment, @name, @text, @target, @provider, @model,
                 @code, @explanation, @vars, @warnings, @pt, @ct, @ms)
            """, cn, tx);

        AddScenarioParams(cmd, pack, scenarioName);
        var p = cmd.Parameters;
        p.Add("@key", SqlDbType.Char, 64).Value = key;
        p.Add("@text", SqlDbType.NVarChar, -1).Value = pack.Scenario;
        p.Add("@provider", SqlDbType.NVarChar, 100).Value = Clip(outcome.Provider, 100);
        p.Add("@model", SqlDbType.NVarChar, 100).Value = Clip(outcome.Model, 100);
        p.Add("@code", SqlDbType.NVarChar, -1).Value = r.Code ?? "";
        p.Add("@explanation", SqlDbType.NVarChar, -1).Value = (object?)r.Explanation ?? DBNull.Value;
        p.Add("@vars", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(r.VariablesUsed);
        p.Add("@warnings", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(r.Warnings);
        p.Add("@pt", SqlDbType.Int).Value = outcome.PromptTokens;
        p.Add("@ct", SqlDbType.Int).Value = outcome.CompletionTokens;
        p.Add("@ms", SqlDbType.Int).Value = (int)Math.Min(int.MaxValue, outcome.ElapsedMs);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The three columns that identify a scenario's row.</summary>
    private static void AddScenarioParams(SqlCommand cmd, ContextPack pack, string scenarioName)
    {
        var p = cmd.Parameters;
        p.Add("@equipment", SqlDbType.NVarChar, 50).Value = Clip(pack.Equipment, 50);
        p.Add("@name", SqlDbType.NVarChar, 200).Value = Clip(scenarioName, 200);
        p.Add("@target", SqlDbType.NVarChar, 30).Value = Clip(pack.TargetLanguage, 30);
    }

    private static List<string> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static string Clip(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];

    /// <summary>A short, safe description of a SQL error - never the connection string.</summary>
    public static string Describe(SqlException ex)
    {
        var msg = ex.Message.Split('\n')[0].Trim();
        if (msg.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            msg += " Add TrustServerCertificate=True to the connection string if the server uses its own certificate.";
        else if (ex.Number == 18456)
            msg = "Login failed - check the user name and password in " + EnvVar + ".";
        return msg;
    }
}
