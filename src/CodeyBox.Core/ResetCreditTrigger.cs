using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Core;

/// <summary>
/// Cost of one consumed rate-limit reset credit, in USD. The provider charges
/// irreversibly per <c>POST wham/rate-limit-reset-credits/consume</c> call.
/// Surfaced as a constant (not config) so every log line and cap description
/// prices the same value; the configurable part is how many credits per period
/// may be spent (<see cref="ResetCreditTriggerOptions.MaxCreditsPerPeriod"/>).
/// </summary>
public static class ResetCreditPricing
{
    /// <summary>Irreversible cost of a single consumed credit, in whole USD.</summary>
    public const int CostPerCreditUsd = 80;
}

/// <summary>
/// Operator config for the banked reset-credit consume trigger (5/5). The trigger
/// acts on a <c>shouldSpend=true</c> verdict from the reset-optimality advisor
/// (4/5) by calling the provider's consume endpoint — an irreversible ~80 USD
/// spend per call. Every field is re-read on each trigger attempt (via the
/// injected options provider), so the kill-switch and every other knob take
/// effect without a host restart.
/// </summary>
public sealed record ResetCreditTriggerOptions
{
    /// <summary>
    /// Master switch. Default false: when absent or false no consume request is
    /// issued under any circumstance, regardless of advisor verdicts.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Second, separate decision required for a live spend. Default false, which
    /// means dry-run: the intended request is logged in full and no HTTP request
    /// is issued. Enabling <see cref="Enabled"/> alone never authorises a live call.
    /// </summary>
    public bool AllowLiveSpend { get; init; }

    /// <summary>
    /// Kill-switch. Read on every trigger attempt (never captured at startup):
    /// while engaged, no request is issued regardless of any other setting.
    /// </summary>
    public bool KillSwitchEngaged { get; init; }

    /// <summary>
    /// Hard cap on credits consumed per <see cref="Period"/>, enforced against
    /// persisted history so a restart cannot reset the budget. Default 1 — the
    /// smallest useful value. Zero blocks all spends.
    /// </summary>
    public int MaxCreditsPerPeriod { get; init; } = 1;

    /// <summary>Rolling window the cap is enforced over. Default 30 days.</summary>
    public TimeSpan Period { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Maximum age of an <c>available_count</c> reading the trigger will spend
    /// against. An older reading is treated as stale and refused. Default 15 minutes.
    /// </summary>
    public TimeSpan MaxBalanceAge { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Human-readable cap priced in both credits and money, e.g.
    /// <c>"1 credit ($80 USD) per 30d"</c>. Used in logs and audit records.
    /// </summary>
    public string CapDescription =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{MaxCreditsPerPeriod} credit{(MaxCreditsPerPeriod == 1 ? string.Empty : "s")} " +
            $"(${MaxCreditsPerPeriod * ResetCreditPricing.CostPerCreditUsd} USD) per {FormatPeriod(Period)}");

    /// <summary>Binds trigger options from a configuration section. Absent keys keep safe defaults (off / dry-run).</summary>
    public static ResetCreditTriggerOptions FromConfiguration(IConfigurationSection section)
    {
        if (section is null)
            return new ResetCreditTriggerOptions();

        var defaults = new ResetCreditTriggerOptions();
        return new ResetCreditTriggerOptions
        {
            Enabled = ReadBool(section, "Enabled", defaults.Enabled),
            AllowLiveSpend = ReadBool(section, "AllowLiveSpend", defaults.AllowLiveSpend),
            KillSwitchEngaged = ReadBool(section, "KillSwitch", ReadBool(section, "KillSwitchEngaged", defaults.KillSwitchEngaged)),
            MaxCreditsPerPeriod = ReadInt(section, "MaxCreditsPerPeriod", defaults.MaxCreditsPerPeriod, minimum: 0),
            Period = ReadPeriodDays(section, "PeriodDays", defaults.Period),
            MaxBalanceAge = ReadMaxBalanceAge(section, defaults.MaxBalanceAge),
        };
    }

    private static bool ReadBool(IConfigurationSection section, string key, bool fallback)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return bool.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback, int minimum)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return fallback;
        return parsed < minimum ? minimum : parsed;
    }

    private static TimeSpan ReadPeriodDays(IConfigurationSection section, string key, TimeSpan fallback)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            return fallback;
        return TimeSpan.FromDays(Math.Min(parsed, 3650));
    }

    private static TimeSpan ReadMaxBalanceAge(IConfigurationSection section, TimeSpan fallback)
    {
        var raw = section["MaxBalanceAgeSeconds"];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            return fallback;
        return TimeSpan.FromSeconds(Math.Min(parsed, TimeSpan.FromDays(7).TotalSeconds));
    }

    private static string FormatPeriod(TimeSpan period)
    {
        if (period.TotalDays >= 1 && period.TotalDays % 1 == 0)
            return $"{period.TotalDays:0}d";
        if (period.TotalHours >= 1)
            return $"{period.TotalHours:0}h";
        return $"{period.TotalMinutes:0}m";
    }
}

/// <summary>Balance reading backing a spend decision: the provider's <c>available_count</c> with its observation time.</summary>
/// <param name="AvailableCount">Banked credits available, or null when unreadable.</param>
/// <param name="SampledAt">When the count was observed, or null when unknown (treated as stale).</param>
public readonly record struct ResetCreditBalance(int? AvailableCount, DateTimeOffset? SampledAt);

/// <summary>Request for one consume call. <c>RedeemRequestId</c> is the idempotency key.</summary>
public sealed record ResetCreditConsumeRequest
{
    /// <summary>Caller-supplied idempotency key, derived deterministically from the authorising decision.</summary>
    public required string RedeemRequestId { get; init; }

    /// <summary>Optional specific credit to consume. Null spends the provider default (soonest-expiring).</summary>
    public string? CreditId { get; init; }
}

/// <summary>Result of one consume call.</summary>
public sealed record ResetCreditConsumeResult
{
    /// <summary>True when the provider consumed a credit for this idempotency key.</summary>
    public required bool Consumed { get; init; }

    /// <summary>Provider echo of the consumed credit, when supplied.</summary>
    public string? CreditId { get; init; }

    /// <summary>Provider message, when supplied. Never contains secrets.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Injected transport for the consume path. Production uses
/// <see cref="HttpResetCreditConsumeTransport"/>; every automated test uses a
/// fake. The trigger holds no other route to the network, so a test suite can
/// never spend a credit — including under misconfiguration or a leaked live
/// credential — unless a test explicitly constructs the HTTP transport.
/// </summary>
public interface IResetCreditConsumeTransport
{
    /// <summary>Reads the current banked-credit balance with its observation time.</summary>
    Task<ResetCreditBalance> ReadBalanceAsync(CancellationToken ct);

    /// <summary>
    /// Consumes one credit. Implementations MUST send <see cref="ResetCreditConsumeRequest.RedeemRequestId"/>
    /// as the provider idempotency key so a retry with the same key cannot consume twice.
    /// </summary>
    Task<ResetCreditConsumeResult> ConsumeAsync(ResetCreditConsumeRequest request, CancellationToken ct);
}

/// <summary>Transport failure carrying no provider verdict — the outcome is ambiguous and must be reconciled, never retried blind.</summary>
public sealed class ResetCreditTransportException : Exception
{
    /// <summary>Creates a transport failure with the given message.</summary>
    public ResetCreditTransportException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a transport failure wrapping its cause.</summary>
    public ResetCreditTransportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Live HTTP transport for the reset-credit endpoints. Built only by production
/// wiring with an explicitly provided <see cref="HttpClient"/> and bearer-token
/// provider; tests never construct it. The base address is restricted to the
/// provider's exact backend host — any other value is rejected at construction.
/// </summary>
public sealed class HttpResetCreditConsumeTransport : IResetCreditConsumeTransport
{
    /// <summary>Exact provider backend origin. Requests to any other host are refused.</summary>
    public const string AllowedBaseAddress = "https://chatgpt.com/backend-api";

    private const int MaxResponseChars = 64 * 1024;

    private readonly HttpClient _http;
    private readonly Func<string?> _bearerTokenProvider;
    private readonly string _baseAddress;

    /// <summary>Creates the live transport. Throws for any base address outside the provider origin.</summary>
    /// <param name="http">Caller-owned HTTP client (lifetime belongs to the caller).</param>
    /// <param name="bearerTokenProvider">Returns the current bearer token, or null when unconfigured.</param>
    /// <param name="baseAddress">Must be exactly the provider backend origin.</param>
    public HttpResetCreditConsumeTransport(
        HttpClient http,
        Func<string?> bearerTokenProvider,
        string baseAddress = AllowedBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bearerTokenProvider);
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!string.Equals(baseAddress.TrimEnd('/'), AllowedBaseAddress, StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(baseAddress), baseAddress, $"Reset-credit transport refuses non-provider host.");
        _http = http;
        _bearerTokenProvider = bearerTokenProvider;
        _baseAddress = baseAddress.TrimEnd('/');
    }

    /// <inheritdoc/>
    public async Task<ResetCreditBalance> ReadBalanceAsync(CancellationToken ct)
    {
        var token = _bearerTokenProvider();
        if (string.IsNullOrWhiteSpace(token))
            return new ResetCreditBalance(null, null);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _baseAddress + "/wham/usage");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ResetCreditBalance(null, null);
            var body = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            if (body is null)
                return new ResetCreditBalance(null, null);
            var count = ParseAvailableCount(body);
            return count is null
                ? new ResetCreditBalance(null, null)
                : new ResetCreditBalance(count, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ResetCreditTransportException("Reset-credit balance read failed.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<ResetCreditConsumeResult> ConsumeAsync(ResetCreditConsumeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RedeemRequestId))
            throw new ArgumentOutOfRangeException(nameof(request), "RedeemRequestId is required.");
        var token = _bearerTokenProvider();
        if (string.IsNullOrWhiteSpace(token))
            throw new ResetCreditTransportException("No bearer token configured for reset-credit consume.");

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _baseAddress + "/wham/rate-limit-reset-credits/consume");
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var payload = new Dictionary<string, string> { ["redeem_request_id"] = request.RedeemRequestId };
            if (!string.IsNullOrWhiteSpace(request.CreditId))
                payload["credit_id"] = request.CreditId;
            httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
            var body = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ResetCreditTransportException($"Consume endpoint returned {(int)response.StatusCode}.");
            return new ResetCreditConsumeResult { Consumed = true, CreditId = request.CreditId, Message = Truncate(body) };
        }
        catch (ResetCreditTransportException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ResetCreditTransportException("Reset-credit consume call failed with an ambiguous outcome.", ex);
        }
    }

    private static int? ParseAvailableCount(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("rate_limit_reset_credits", out var credits)
                || credits.ValueKind != JsonValueKind.Object
                || !credits.TryGetProperty("available_count", out var count)
                || count.ValueKind != JsonValueKind.Number
                || !count.TryGetInt32(out var value))
                return null;
            return value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxResponseChars + 1];
        var total = 0;
        int chunk;
        do
        {
            chunk = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            total += chunk;
        }
        while (chunk > 0 && total < buffer.Length);
        if (total > MaxResponseChars)
            return null;
        return new string(buffer, 0, total);
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        const int max = 512;
        return value.Length <= max ? value : value.Substring(0, max);
    }
}

/// <summary>Machine-readable outcome of one trigger attempt. Every attempt — spend or refusal — is audited.</summary>
public enum ResetCreditTriggerDecision
{
    /// <summary>Advisor did not report optimal (or no advice): never spend.</summary>
    RefusedAdvisorHold,
    /// <summary>Feature flag off: no request under any circumstance.</summary>
    RefusedFeatureDisabled,
    /// <summary>Kill-switch engaged: blocked without restart semantics.</summary>
    RefusedKillSwitch,
    /// <summary>Balance unreadable: unknown balance is not permission to spend.</summary>
    RefusedBalanceUnknown,
    /// <summary>Balance reading too old to spend against.</summary>
    RefusedBalanceStale,
    /// <summary>No banked credit available (count not positive).</summary>
    RefusedBalanceEmpty,
    /// <summary>Per-period cap already reached.</summary>
    RefusedCapExceeded,
    /// <summary>Transport failure with no ambiguous consumption: retry with the same key is allowed later.</summary>
    RefusedTransportFailed,
    /// <summary>Feature enabled but live spend not authorised: intended request logged, nothing issued.</summary>
    DryRun,
    /// <summary>A credit was consumed for this idempotency key.</summary>
    Consumed,
    /// <summary>Repeat trigger for an already-consumed decision: no new request.</summary>
    AlreadyConsumed,
    /// <summary>Ambiguous failure reconciled as consumed via the read endpoints: no blind retry.</summary>
    ReconciledConsumed,
}

/// <summary>Outcome of one <see cref="ResetCreditConsumeTrigger.TryTriggerAsync"/> call.</summary>
public sealed record ResetCreditTriggerOutcome
{
    /// <summary>What the trigger decided.</summary>
    public required ResetCreditTriggerDecision Decision { get; init; }

    /// <summary>Human-readable reason naming the gate that allowed or refused.</summary>
    public required string Reason { get; init; }

    /// <summary>Idempotency key for the authorising decision. Present on every outcome, including refusals.</summary>
    public required string RedeemRequestId { get; init; }

    /// <summary>True when a live consume request was issued on this call.</summary>
    public required bool RequestIssued { get; init; }

    /// <summary>True when a credit was consumed (this call or a reconciled prior attempt).</summary>
    public required bool Consumed { get; init; }

    /// <summary>Credits consumed in the current period after this decision.</summary>
    public required int PeriodSpend { get; init; }

    /// <summary>Configured cap, echoed for attribution.</summary>
    public required int PeriodCap { get; init; }
}

/// <summary>
/// Audit record for one trigger decision. Persisted for spends AND refusals so a
/// consumed credit — or a decision not to spend — is attributable afterwards.
/// </summary>
public sealed record ResetCreditTriggerAuditRecord
{
    /// <summary>When the decision was made.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Agent the authorising advice concerned (empty when no advice was present).</summary>
    public required string Agent { get; init; }

    /// <summary>Advisor reason name driving the attempt (empty when no advice was present).</summary>
    public required string AdviceReason { get; init; }

    /// <summary>What the advisor reported (ShouldSpend bit), when advice was present.</summary>
    public bool? AdvisorShouldSpend { get; init; }

    /// <summary>Gate that allowed or refused: the <see cref="ResetCreditTriggerDecision"/> name.</summary>
    public required string Gate { get; init; }

    /// <summary>Idempotency key for the decision.</summary>
    public required string RedeemRequestId { get; init; }

    /// <summary>True when a live request was issued on this attempt.</summary>
    public required bool RequestIssued { get; init; }

    /// <summary>True when the decision leaves a credit consumed.</summary>
    public required bool Consumed { get; init; }

    /// <summary>Running total spent in the current period after this decision.</summary>
    public required int PeriodSpend { get; init; }

    /// <summary>Configured cap at decision time.</summary>
    public required int PeriodCap { get; init; }

    /// <summary>Human-readable detail (gate, balance, money). Never contains secrets or tokens.</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// Persisted trigger state: idempotency keys, consumption history (the cap
/// budget), and the audit trail. Backed by a file so a restart cannot reset
/// the budget; every mutating step is atomic under an async gate.
/// </summary>
public interface IResetCreditTriggerStore
{
    /// <summary>Returns the persisted redeem key for a decision fingerprint, if any.</summary>
    Task<string?> FindRedeemKeyAsync(string decisionFingerprint, CancellationToken ct);

    /// <summary>Persists a fingerprint-to-key mapping BEFORE the consume request is issued. Idempotent.</summary>
    Task PersistRedeemKeyAsync(string decisionFingerprint, string redeemRequestId, CancellationToken ct);

    /// <summary>True when a consumption is already recorded for this redeem key.</summary>
    Task<bool> IsConsumedAsync(string redeemRequestId, CancellationToken ct);

    /// <summary>Records a consumption. Re-recording the same key is a no-op (at-most-once).</summary>
    Task RecordConsumptionAsync(string redeemRequestId, DateTimeOffset consumedAt, CancellationToken ct);

    /// <summary>Counts consumptions within the rolling <paramref name="period"/> ending at <paramref name="now"/>.</summary>
    Task<int> PeriodSpendAsync(DateTimeOffset now, TimeSpan period, CancellationToken ct);

    /// <summary>Appends an audit record. The trail is bounded; oldest entries are dropped first.</summary>
    Task AppendAuditAsync(ResetCreditTriggerAuditRecord record, CancellationToken ct);

    /// <summary>Returns persisted audit records, newest last.</summary>
    Task<IReadOnlyList<ResetCreditTriggerAuditRecord>> ListAuditsAsync(CancellationToken ct);
}

/// <summary>
/// File-backed <see cref="IResetCreditTriggerStore"/>. State is re-read from
/// disk on every operation and written atomically (temp file + move), so a new
/// instance over the same path observes the same budget — a restart cannot
/// reset the cap. Concurrent attempts within one process are serialised by an
/// async gate; the trigger additionally serialises its own consume path.
/// </summary>
public sealed class FileResetCreditTriggerStore : IResetCreditTriggerStore
{
    /// <summary>Maximum audit records retained. Bounds the state file against unbounded growth.</summary>
    public const int MaxAuditRecords = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a file-backed store. The parent directory must exist.</summary>
    /// <param name="filePath">Absolute path of the JSON state file.</param>
    public FileResetCreditTriggerStore(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentOutOfRangeException(nameof(filePath), "Store file path is required.");
        _filePath = filePath;
    }

    /// <inheritdoc/>
    public async Task<string?> FindRedeemKeyAsync(string decisionFingerprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decisionFingerprint);
        var state = await LoadAsync(ct).ConfigureAwait(false);
        return state.Keys.TryGetValue(decisionFingerprint, out var key) ? key : null;
    }

    /// <inheritdoc/>
    public async Task PersistRedeemKeyAsync(string decisionFingerprint, string redeemRequestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decisionFingerprint);
        ArgumentNullException.ThrowIfNull(redeemRequestId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadLockedAsync(ct).ConfigureAwait(false);
            state.Keys[decisionFingerprint] = redeemRequestId;
            await SaveLockedAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsConsumedAsync(string redeemRequestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(redeemRequestId);
        var state = await LoadAsync(ct).ConfigureAwait(false);
        return state.Consumptions.Any(c => string.Equals(c.RedeemRequestId, redeemRequestId, StringComparison.Ordinal));
    }

    /// <inheritdoc/>
    public async Task RecordConsumptionAsync(string redeemRequestId, DateTimeOffset consumedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(redeemRequestId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadLockedAsync(ct).ConfigureAwait(false);
            if (state.Consumptions.Any(c => string.Equals(c.RedeemRequestId, redeemRequestId, StringComparison.Ordinal)))
                return;
            state.Consumptions.Add(new PersistedConsumption { RedeemRequestId = redeemRequestId, ConsumedAt = consumedAt });
            await SaveLockedAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<int> PeriodSpendAsync(DateTimeOffset now, TimeSpan period, CancellationToken ct)
    {
        var state = await LoadAsync(ct).ConfigureAwait(false);
        var cutoff = now - period;
        return state.Consumptions.Count(c => c.ConsumedAt > cutoff);
    }

    /// <inheritdoc/>
    public async Task AppendAuditAsync(ResetCreditTriggerAuditRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadLockedAsync(ct).ConfigureAwait(false);
            state.Audits.Add(PersistedAudit.FromRecord(record));
            while (state.Audits.Count > MaxAuditRecords)
                state.Audits.RemoveAt(0);
            await SaveLockedAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ResetCreditTriggerAuditRecord>> ListAuditsAsync(CancellationToken ct)
    {
        var state = await LoadAsync(ct).ConfigureAwait(false);
        return state.Audits.Select(a => a.ToRecord()).ToList();
    }

    private async Task<PersistedState> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PersistedState> LoadLockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return new PersistedState();
        // Fail-closed: a present-but-unreadable state file surfaces its error
        // instead of degrading to an empty budget. An empty budget would reset
        // the per-period cap (fail-open on an $80 spend path); throwing blocks
        // the attempt with no spend, and the host logs and retries next tick.
        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException($"Reset-credit trigger state file is empty: {_filePath}");
        return JsonSerializer.Deserialize<PersistedState>(json, JsonOptions)
            ?? throw new InvalidDataException($"Reset-credit trigger state file deserialised to null: {_filePath}");
    }

    private async Task SaveLockedAsync(PersistedState state, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private sealed class PersistedState
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
        public List<PersistedConsumption> Consumptions { get; set; } = new();
        public List<PersistedAudit> Audits { get; set; } = new();
    }

    private sealed class PersistedConsumption
    {
        public string RedeemRequestId { get; set; } = string.Empty;
        public DateTimeOffset ConsumedAt { get; set; }
    }

    private sealed class PersistedAudit
    {
        public DateTimeOffset OccurredAt { get; set; }
        public string Agent { get; set; } = string.Empty;
        public string AdviceReason { get; set; } = string.Empty;
        public bool? AdvisorShouldSpend { get; set; }
        public string Gate { get; set; } = string.Empty;
        public string RedeemRequestId { get; set; } = string.Empty;
        public bool RequestIssued { get; set; }
        public bool Consumed { get; set; }
        public int PeriodSpend { get; set; }
        public int PeriodCap { get; set; }
        public string Detail { get; set; } = string.Empty;

        public static PersistedAudit FromRecord(ResetCreditTriggerAuditRecord record) => new()
        {
            OccurredAt = record.OccurredAt,
            Agent = record.Agent,
            AdviceReason = record.AdviceReason,
            AdvisorShouldSpend = record.AdvisorShouldSpend,
            Gate = record.Gate,
            RedeemRequestId = record.RedeemRequestId,
            RequestIssued = record.RequestIssued,
            Consumed = record.Consumed,
            PeriodSpend = record.PeriodSpend,
            PeriodCap = record.PeriodCap,
            Detail = record.Detail,
        };

        public ResetCreditTriggerAuditRecord ToRecord() => new()
        {
            OccurredAt = OccurredAt,
            Agent = Agent,
            AdviceReason = AdviceReason,
            AdvisorShouldSpend = AdvisorShouldSpend,
            Gate = Gate,
            RedeemRequestId = RedeemRequestId,
            RequestIssued = RequestIssued,
            Consumed = Consumed,
            PeriodSpend = PeriodSpend,
            PeriodCap = PeriodCap,
            Detail = Detail,
        };
    }
}

/// <summary>
/// The banked reset-credit consume trigger (5/5). Acts on a
/// <c>shouldSpend=true</c> advisor verdict by calling the provider consume
/// endpoint — an irreversible ~80 USD spend per call — behind a chain of
/// fail-closed gates. Safety properties:
/// <list type="bullet">
/// <item>Disabled by default; off means no request under any circumstance.</item>
/// <item>Dry-run by default; a live call needs the second <c>AllowLiveSpend</c> decision.</item>
/// <item>Deterministic idempotency key per authorising decision, persisted BEFORE the request.</item>
/// <item>Per-period cap enforced against persisted history (restart-safe).</item>
/// <item>Kill-switch re-read on every attempt (no restart needed).</item>
/// <item>Unknown, stale, or non-positive balances refuse.</item>
/// <item>Ambiguous failures reconcile via the read endpoints instead of retrying blind.</item>
/// <item>The consume path is serialised: concurrent attempts cannot spend twice.</item>
/// </list>
/// </summary>
public sealed class ResetCreditConsumeTrigger
{
    private readonly Func<ResetCreditTriggerOptions> _optionsProvider;
    private readonly IResetCreditConsumeTransport _transport;
    private readonly IResetCreditTriggerStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _consumeGate = new(1, 1);

    /// <summary>
    /// Creates the trigger. All collaborators are injected — including the
    /// options provider (a delegate so hot-reloaded values take effect without
    /// a restart) and the transport (a fake under test, so no test can reach
    /// the live endpoint).
    /// </summary>
    /// <param name="optionsProvider">Returns current options on every attempt. Must never be null-returning.</param>
    /// <param name="transport">Consume transport. Tests inject a fake.</param>
    /// <param name="store">Persisted idempotency/cap/audit store.</param>
    /// <param name="clock">Clock. Defaults to system.</param>
    /// <param name="logger">Logger. Defaults to null logger.</param>
    public ResetCreditConsumeTrigger(
        Func<ResetCreditTriggerOptions> optionsProvider,
        IResetCreditConsumeTransport transport,
        IResetCreditTriggerStore store,
        TimeProvider? clock = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(optionsProvider);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(store);
        _optionsProvider = optionsProvider;
        _transport = transport;
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Attempts one trigger for an advisor verdict. Serialised against
    /// concurrent attempts; every path (spend or refusal) writes an audit record.
    /// </summary>
    /// <param name="advice">Latest advisor verdict for the agent. Null/ShouldSpend=false refuses.</param>
    public async Task<ResetCreditTriggerOutcome> TryTriggerAsync(ResetSpendAdvice? advice, CancellationToken ct = default)
    {
        await _consumeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await TryTriggerLockedAsync(advice, ct).ConfigureAwait(false);
        }
        finally
        {
            _consumeGate.Release();
        }
    }

    /// <summary>
    /// Derives the stable decision fingerprint for an advisor verdict. Pure:
    /// the same authorising decision always yields the same fingerprint, across
    /// restarts, so retries reuse one idempotency key.
    /// </summary>
    public static string ComputeDecisionFingerprint(ResetSpendAdvice advice)
    {
        ArgumentNullException.ThrowIfNull(advice);
        var window = advice.OptimalWindow;
        var canonical = string.Join(
            "|",
            "v1",
            advice.Agent.Trim().ToLowerInvariant(),
            window?.OpensAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "-",
            window?.ClosesAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "-",
            advice.DecisionDeadline?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "-",
            advice.NextCreditExpiresAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "-",
            advice.Reason.ToString());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Derives the idempotency key for a decision fingerprint. Pure and stable.
    /// </summary>
    public static string ComputeRedeemRequestId(string decisionFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionFingerprint);
        return "rcr_" + decisionFingerprint;
    }

    private async Task<ResetCreditTriggerOutcome> TryTriggerLockedAsync(ResetSpendAdvice? advice, CancellationToken ct)
    {
        var options = _optionsProvider();
        ArgumentNullException.ThrowIfNull(options, nameof(options));
        var now = _clock.GetUtcNow();

        if (advice is null || !advice.ShouldSpend)
        {
            var key = advice is null ? "rcr_refused_no_advice" : ComputeRedeemRequestId(ComputeDecisionFingerprint(advice));
            return await RefuseAsync(
                advice, options, now, key,
                ResetCreditTriggerDecision.RefusedAdvisorHold,
                advice is null
                    ? "Advisor produced no verdict — refusing: no authorising decision."
                    : $"Advisor holds ({advice.Reason}) — refusing: trigger is reachable only on shouldSpend=true.",
                ct).ConfigureAwait(false);
        }

        var redeemKey = ComputeRedeemRequestId(ComputeDecisionFingerprint(advice));

        if (!options.Enabled)
        {
            return await RefuseAsync(
                advice, options, now, redeemKey,
                ResetCreditTriggerDecision.RefusedFeatureDisabled,
                "Feature flag is off — refusing: off means no request under any circumstance.",
                ct).ConfigureAwait(false);
        }

        if (options.KillSwitchEngaged)
        {
            return await RefuseAsync(
                advice, options, now, redeemKey,
                ResetCreditTriggerDecision.RefusedKillSwitch,
                "Kill-switch is engaged — refusing regardless of any other setting.",
                ct).ConfigureAwait(false);
        }

        if (await _store.IsConsumedAsync(redeemKey, ct).ConfigureAwait(false))
        {
            var spend = await _store.PeriodSpendAsync(now, options.Period, ct).ConfigureAwait(false);
            return await AuditAndReturnAsync(
                advice, options, now, redeemKey, ResetCreditTriggerDecision.AlreadyConsumed,
                "Decision already consumed — idempotent replay: no new request.",
                requestIssued: false, consumed: true, periodSpend: spend, ct).ConfigureAwait(false);
        }

        ResetCreditBalance balance;
        try
        {
            balance = await _transport.ReadBalanceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await RefuseAsync(
                advice, options, now, redeemKey,
                ResetCreditTriggerDecision.RefusedBalanceUnknown,
                $"Balance read threw ({ex.GetType().Name}) — refusing: an unknown balance is not permission to spend.",
                ct).ConfigureAwait(false);
        }

        if (balance.AvailableCount is not { } count)
        {
            return await RefuseAsync(
                advice, options, now, redeemKey,
                ResetCreditTriggerDecision.RefusedBalanceUnknown,
                "Balance is unreadable (available_count missing) — refusing: an unknown balance is not permission to spend.",
                ct).ConfigureAwait(false);
        }

        if (balance.SampledAt is not { } sampledAt || now - sampledAt > options.MaxBalanceAge)
        {
            return await RefuseAsync(
                advice, options, now, redeemKey,
                ResetCreditTriggerDecision.RefusedBalanceStale,
                $"Balance reading is stale (sampled {(balance.SampledAt is { } s ? s.ToString("u") : "unknown")}, max age {options.MaxBalanceAge}) — refusing.",
                ct).ConfigureAwait(false);
        }

        if (count <= 0)
        {
            return await RefuseAsync(
                advice, options, now, redeemKey,
                ResetCreditTriggerDecision.RefusedBalanceEmpty,
                $"Balance is {count} — refusing: no banked credit to spend.",
                ct).ConfigureAwait(false);
        }

        var periodSpend = await _store.PeriodSpendAsync(now, options.Period, ct).ConfigureAwait(false);
        if (periodSpend >= options.MaxCreditsPerPeriod)
        {
            return await RefuseAsync(
                advice, options, now, redeemKey,
                ResetCreditTriggerDecision.RefusedCapExceeded,
                $"Cap reached ({periodSpend}/{options.MaxCreditsPerPeriod} credits, " +
                $"${periodSpend * ResetCreditPricing.CostPerCreditUsd} of ${options.MaxCreditsPerPeriod * ResetCreditPricing.CostPerCreditUsd} USD per {options.CapDescription}) — refusing.",
                ct, periodSpendOverride: periodSpend).ConfigureAwait(false);
        }

        if (!options.AllowLiveSpend)
        {
            _logger.LogInformation(
                "Reset-credit trigger dry-run: would POST {{base}}/wham/rate-limit-reset-credits/consume " +
                "redeem_request_id={RedeemRequestId} credit_id={CreditId} balance={Balance} agent={Agent} " +
                "spend={Spend}/{Cap} ({CapDescription}). No request issued (AllowLiveSpend=false).",
                redeemKey,
                "server-default (soonest-expiring)",
                count,
                advice.Agent,
                periodSpend,
                options.MaxCreditsPerPeriod,
                options.CapDescription);
            return await AuditAndReturnAsync(
                advice, options, now, redeemKey, ResetCreditTriggerDecision.DryRun,
                $"Dry-run: intended POST wham/rate-limit-reset-credits/consume redeem_request_id={redeemKey} " +
                $"credit_id=server-default (soonest-expiring) balance={count} agent={advice.Agent} " +
                $"spend={periodSpend}/{options.MaxCreditsPerPeriod} ({options.CapDescription}). No request issued.",
                requestIssued: false, consumed: false, periodSpend: periodSpend, ct).ConfigureAwait(false);
        }

        await _store.PersistRedeemKeyAsync(ComputeDecisionFingerprint(advice), redeemKey, ct).ConfigureAwait(false);

        var request = new ResetCreditConsumeRequest { RedeemRequestId = redeemKey, CreditId = null };
        try
        {
            var result = await _transport.ConsumeAsync(request, ct).ConfigureAwait(false);
            if (!result.Consumed)
            {
                return await AuditAndReturnAsync(
                    advice, options, now, redeemKey, ResetCreditTriggerDecision.RefusedTransportFailed,
                    "Provider reported no consumption — recording nothing; a later retry reuses the same key.",
                    requestIssued: true, consumed: false, periodSpend: periodSpend, ct).ConfigureAwait(false);
            }

            await _store.RecordConsumptionAsync(redeemKey, now, ct).ConfigureAwait(false);
            var after = await _store.PeriodSpendAsync(now, options.Period, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Reset-credit trigger consumed 1 credit (${CostUsd} USD) redeem_request_id={RedeemRequestId} agent={Agent} spend={Spend}/{Cap} ({CapDescription}).",
                ResetCreditPricing.CostPerCreditUsd, redeemKey, advice.Agent, after, options.MaxCreditsPerPeriod, options.CapDescription);
            return await AuditAndReturnAsync(
                advice, options, now, redeemKey, ResetCreditTriggerDecision.Consumed,
                $"Consumed 1 credit (${ResetCreditPricing.CostPerCreditUsd} USD) redeem_request_id={redeemKey} agent={advice.Agent} spend={after}/{options.MaxCreditsPerPeriod} ({options.CapDescription}).",
                requestIssued: true, consumed: true, periodSpend: after, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await ReconcileAmbiguousFailureAsync(advice, options, now, redeemKey, count, ex, periodSpend, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reconciles a transport failure, timeout, or crash-window ambiguity against
    /// the read endpoints instead of retrying blind. A decremented or unreadable
    /// balance is treated as consumed (fail-closed against double-spend); an
    /// provably unchanged balance leaves the key unconsumed so a later retry
    /// with the SAME key is safe.
    /// </summary>
    private async Task<ResetCreditTriggerOutcome> ReconcileAmbiguousFailureAsync(
        ResetSpendAdvice advice,
        ResetCreditTriggerOptions options,
        DateTimeOffset now,
        string redeemKey,
        int preCount,
        Exception failure,
        int periodSpendBefore,
        CancellationToken ct)
    {
        ResetCreditBalance probe;
        try
        {
            probe = await _transport.ReadBalanceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            probe = new ResetCreditBalance(null, null);
        }

        if (probe.AvailableCount is { } post && post >= preCount && probe.SampledAt is { } sampled && now - sampled <= options.MaxBalanceAge)
        {
            return await AuditAndReturnAsync(
                advice, options, now, redeemKey, ResetCreditTriggerDecision.RefusedTransportFailed,
                $"Consume call failed ({failure.GetType().Name}) but the balance is provably unchanged " +
                $"({preCount} -> {post}) — recording nothing; a later retry reuses redeem_request_id={redeemKey}.",
                requestIssued: true, consumed: false, periodSpend: periodSpendBefore, ct).ConfigureAwait(false);
        }

        await _store.RecordConsumptionAsync(redeemKey, now, ct).ConfigureAwait(false);
        var after = await _store.PeriodSpendAsync(now, options.Period, ct).ConfigureAwait(false);
        var why = probe.AvailableCount is null
            ? "the post-failure balance is unreadable"
            : $"the balance moved ({preCount} -> {probe.AvailableCount})";
        _logger.LogWarning(
            "Reset-credit trigger ambiguous failure reconciled as consumed: {Failure} ({Why}); redeem_request_id={RedeemRequestId}. No blind retry.",
            failure.GetType().Name, why, redeemKey);
        return await AuditAndReturnAsync(
            advice, options, now, redeemKey, ResetCreditTriggerDecision.ReconciledConsumed,
            $"Consume call failed ({failure.GetType().Name}) and {why} — treating the outcome as consumed " +
            $"for redeem_request_id={redeemKey}. No blind retry. Spend={after}/{options.MaxCreditsPerPeriod} ({options.CapDescription}).",
            requestIssued: true, consumed: true, periodSpend: after, ct).ConfigureAwait(false);
    }

    private async Task<ResetCreditTriggerOutcome> RefuseAsync(
        ResetSpendAdvice? advice,
        ResetCreditTriggerOptions options,
        DateTimeOffset now,
        string redeemKey,
        ResetCreditTriggerDecision decision,
        string reason,
        CancellationToken ct,
        int? periodSpendOverride = null)
    {
        var spend = periodSpendOverride ?? await _store.PeriodSpendAsync(now, options.Period, ct).ConfigureAwait(false);
        _logger.LogInformation("Reset-credit trigger refused ({Decision}): {Reason}", decision, reason);
        return await AuditAndReturnAsync(advice, options, now, redeemKey, decision, reason, requestIssued: false, consumed: false, periodSpend: spend, ct).ConfigureAwait(false);
    }

    private async Task<ResetCreditTriggerOutcome> AuditAndReturnAsync(
        ResetSpendAdvice? advice,
        ResetCreditTriggerOptions options,
        DateTimeOffset now,
        string redeemKey,
        ResetCreditTriggerDecision decision,
        string reason,
        bool requestIssued,
        bool consumed,
        int periodSpend,
        CancellationToken ct)
    {
        var record = new ResetCreditTriggerAuditRecord
        {
            OccurredAt = now,
            Agent = advice?.Agent ?? string.Empty,
            AdviceReason = advice?.Reason.ToString() ?? string.Empty,
            AdvisorShouldSpend = advice?.ShouldSpend,
            Gate = decision.ToString(),
            RedeemRequestId = redeemKey,
            RequestIssued = requestIssued,
            Consumed = consumed,
            PeriodSpend = periodSpend,
            PeriodCap = options.MaxCreditsPerPeriod,
            Detail = reason.Length <= 2000 ? reason : reason.Substring(0, 2000),
        };
        await _store.AppendAuditAsync(record, ct).ConfigureAwait(false);
        return new ResetCreditTriggerOutcome
        {
            Decision = decision,
            Reason = reason,
            RedeemRequestId = redeemKey,
            RequestIssued = requestIssued,
            Consumed = consumed,
            PeriodSpend = periodSpend,
            PeriodCap = options.MaxCreditsPerPeriod,
        };
    }
}
