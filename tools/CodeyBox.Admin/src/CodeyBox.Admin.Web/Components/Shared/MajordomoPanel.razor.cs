using System.Text.Json;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Admin.Web.Components.Shared;

/// <summary>
/// The majordomo conversation panel beside the fleet map: operator input,
/// the live conversation thread (majordomo turns plus the tool calls it
/// made, shown inline), the autonomy switch, and pending-proposal review.
/// Work-item references render by title with a link, never as a bare id.
/// </summary>
public sealed partial class MajordomoPanel : ComponentBase, IAsyncDisposable
{
    /// <summary>Resolves a work-item id to its title; null when unknown.</summary>
    [Parameter]
    public Func<string, string?> TitleFor { get; set; } = _ => null;

    /// <summary>Known id → title pairs primed from the map snapshot.</summary>
    [Parameter]
    public IReadOnlyDictionary<string, string>? TitleMap { get; set; }

    /// <summary>
    /// Fires after an approve, reject, or send the map should pick up: the
    /// panel reflects reality without a reload by asking the map to
    /// re-poll — an approved item appears on its next refresh.
    /// </summary>
    [Parameter]
    public EventCallback OnFleetChanged { get; set; }

    [Inject]
    private ICodeyBoxApiClient ApiClient { get; set; } = null!;

    [Inject]
    private ILogger<MajordomoPanel> Logger { get; set; } = null!;
    private List<MajordomoConversationEntryDto> _entries = [];
    private long _maxSequence;
    private string? _summary;
    private MajordomoModeDto? _mode;
    private List<MajordomoProposalDto> _proposals = [];
    private string _input = string.Empty;
    private bool _sending;
    private string? _sendError;
    private string? _loadError;
    private string? _modeError;
    private readonly Dictionary<string, string?> _decisionErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rejectReasons = new(StringComparer.Ordinal);

    // Titles the panel already knows: the map snapshot first, fetched items
    // after. Unknown GUID-shaped tokens are fetched once and cached (a null
    // value means "not a work item" — proposal ids and session ids must not
    // link to the work-item page).
    private readonly Dictionary<string, string?> _titles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _refreshLoop;

    private bool IsAutonomous => string.Equals(_mode?.Mode, "autonomous", StringComparison.OrdinalIgnoreCase);

    private string? ResolveTitle(string id)
    {
        if (_titles.TryGetValue(id, out var cached))
            return cached;
        var known = TitleFor(id);
        _titles[id] = known;
        if (known is null)
            _ = ResolveUnknownAsync(id);
        return known;
    }

    private async Task ResolveUnknownAsync(string id)
    {
        lock (_resolving)
        {
            if (!_resolving.Add(id))
                return;
        }
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            var item = await ApiClient.GetWorkItemAsync(id, ct);
            _titles[id] = item?.Title;
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Majordomo panel: title lookup for {Id} failed", id);
        }
        finally
        {
            lock (_resolving)
                _resolving.Remove(id);
        }
    }

    private IReadOnlyList<LinkedSegment> Linkify(string text) =>
        MajordomoItemLinker.Linkify(text, ResolveTitle);

    protected override async Task OnInitializedAsync()
    {
        _cts = new CancellationTokenSource();
        foreach (var primed in PrimedTitles())
            _titles[primed.Key] = primed.Value;
        await RefreshAllAsync(first: true);
        // Stored, not fire-and-forget: disposal cancels the scope and
        // observes the loop so a fault is logged, never unobserved.
        _refreshLoop = RefreshLoopAsync(_cts.Token);
    }

    private IReadOnlyDictionary<string, string> PrimedTitles()
    {
        try
        {
            return TitleMap ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RefreshAllAsync(first: false);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Re-reads mode, new conversation rows, and pending proposals.</summary>
    public async Task RefreshAllAsync(bool first = false)
    {
        var ct = _cts?.Token ?? default;
        try
        {
            var mode = await ApiClient.GetMajordomoModeAsync(ct);
            if (mode is not null)
                _mode = mode;
            var page = await ApiClient.GetMajordomoConversationAsync(_maxSequence, 100, ct);
            if (page is not null)
            {
                if (first)
                {
                    _entries = page.Entries.ToList();
                    _summary = page.Summary;
                    _maxSequence = _entries.Count == 0 ? 0 : _entries.Max(e => e.Sequence);
                }
                else if (page.Entries.Count > 0)
                {
                    _entries.AddRange(page.Entries);
                    _maxSequence = _entries.Max(e => e.Sequence);
                }
            }
            _proposals = await ApiClient.GetMajordomoProposalsAsync("pending", ct);
            foreach (var p in _proposals)
                _rejectReasons.TryAdd(p.Id, string.Empty);
            foreach (var stale in _rejectReasons.Keys
                .Where(k => _proposals.All(p => !string.Equals(p.Id, k, StringComparison.Ordinal))).ToList())
                _rejectReasons.Remove(stale);
            _loadError = null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Majordomo panel: refresh failed");
            _loadError ??= "The majordomo panel could not reach the server. It will keep retrying.";
        }
    }

    private async Task SendAsync()
    {
        var text = _input.Trim();
        if (text.Length == 0 || _sending)
            return;
        _sending = true;
        _sendError = null;
        try
        {
            var ct = _cts?.Token ?? default;
            var entry = await ApiClient.PostMajordomoMessageAsync(text, ct);
            if (entry is null)
            {
                _sendError = "The message was not recorded. The server may be unreachable — try again.";
                return;
            }
            _input = string.Empty;
            _entries.Add(entry);
            _maxSequence = Math.Max(_maxSequence, entry.Sequence);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Majordomo panel: send failed");
            _sendError = "The message was not recorded. The server may be unreachable — try again.";
        }
        finally
        {
            _sending = false;
        }
    }

    private async Task SwitchModeAsync(string mode)
    {
        _modeError = null;
        try
        {
            var ct = _cts?.Token ?? default;
            var next = await ApiClient.SetMajordomoModeAsync(mode, ct);
            if (next is null)
            {
                _modeError = $"The mode was not changed to {mode}. The server refused the switch — try again.";
                return;
            }
            _mode = next;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Majordomo panel: mode switch failed");
            _modeError = $"The mode was not changed to {mode}. The server may be unreachable — try again.";
        }
    }

    private async Task ApproveAsync(MajordomoProposalDto proposal)
    {
        _decisionErrors.Remove(proposal.Id);
        try
        {
            var ct = _cts?.Token ?? default;
            var outcome = await ApiClient.ApproveMajordomoProposalAsync(proposal.Id, ct);
            if (outcome.Ok)
            {
                _proposals.RemoveAll(p => string.Equals(p.Id, proposal.Id, StringComparison.Ordinal));
                await OnFleetChanged.InvokeAsync();
                return;
            }
            _decisionErrors[proposal.Id] = DescribeRefusal(outcome);
            // A refusal may mean the queue moved under the panel; re-list so
            // decided rows disappear instead of lingering as pending.
            _proposals = await ApiClient.GetMajordomoProposalsAsync("pending", ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Majordomo panel: approve failed");
            _decisionErrors[proposal.Id] = "The approval did not reach the server — try again.";
        }
    }

    private async Task RejectAsync(MajordomoProposalDto proposal)
    {
        _decisionErrors.Remove(proposal.Id);
        try
        {
            var ct = _cts?.Token ?? default;
            _rejectReasons.TryGetValue(proposal.Id, out var reason);
            var outcome = await ApiClient.RejectMajordomoProposalAsync(proposal.Id, reason, ct);
            if (outcome.Ok)
            {
                _proposals.RemoveAll(p => string.Equals(p.Id, proposal.Id, StringComparison.Ordinal));
                _rejectReasons.Remove(proposal.Id);
                await OnFleetChanged.InvokeAsync();
                return;
            }
            _decisionErrors[proposal.Id] = DescribeRefusal(outcome);
            _proposals = await ApiClient.GetMajordomoProposalsAsync("pending", ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Majordomo panel: reject failed");
            _decisionErrors[proposal.Id] = "The rejection did not reach the server — try again.";
        }
    }

    private static string DescribeRefusal(MajordomoProposalDecisionDto outcome)
    {
        var reason = string.IsNullOrWhiteSpace(outcome.Reason) ? "refused" : outcome.Reason;
        var detail = string.IsNullOrWhiteSpace(outcome.Detail) ? null : $" — {outcome.Detail}";
        return $"Not applied ({reason}){detail} The proposal was left untouched; refresh the queue if the fleet moved.";
    }

    /// <summary>
    /// One human line per planned change: what it does, with affected items
    /// as titles (resolved through the same title cache as the thread).
    /// Unparseable payloads fall back to the tool name so review never shows
    /// a blank change.
    /// </summary>
    public IReadOnlyList<string> ChangeLines(MajordomoProposalDto proposal) =>
        MajordomoChangeSummarizer.Summarize(proposal, ResolveTitle);

    /// <summary>Item ids the change set names, for title links under the summary.</summary>
    public IReadOnlyList<string> ChangeItemIds(MajordomoProposalDto proposal) =>
        MajordomoChangeSummarizer.ItemIds(proposal);

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);
        if (_refreshLoop is not null)
        {
            try
            {
                await _refreshLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: disposal cancels the poll scope.
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Majordomo panel: refresh loop ended unexpectedly");
            }
        }
        _cts?.Dispose();
    }
}
