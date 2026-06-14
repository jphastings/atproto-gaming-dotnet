using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Sidecar;

/// <summary>
/// Decides whether a client may publish. Approval is by the client's self-chosen
/// <c>clientId</c>, granted once via a terminal prompt and remembered in config (so it
/// survives restarts and is revocable). Policy (the approved set) is separated from the
/// console UI (<see cref="RunConsoleLoopAsync"/>) so it's testable without a terminal.
/// </summary>
internal sealed class ApprovalService
{
    private readonly object _lock = new();
    private readonly HashSet<string> _approved = new();
    private readonly HashSet<string> _prompted = new();
    private readonly Channel<(string id, string name)> _queue = Channel.CreateUnbounded<(string, string)>();
    private readonly ConfigStore<SidecarConfig> _config;
    private readonly IClock _clock;
    private readonly ILogSink _log;

    public ApprovalService(ConfigStore<SidecarConfig> config, IClock clock, ILogSink log)
    {
        _config = config;
        _clock = clock;
        _log = log;
        foreach (var c in config.Current.ApprovedClients)
            if (!string.IsNullOrEmpty(c.Id)) _approved.Add(c.Id);
    }

    public bool IsApproved(string clientId)
    {
        lock (_lock) return _approved.Contains(clientId);
    }

    /// <summary>Queues a one-time terminal approval prompt for an unknown client (deduped per run).</summary>
    public void RequestApproval(string clientId, string name)
    {
        lock (_lock)
        {
            if (_approved.Contains(clientId) || !_prompted.Add(clientId)) return;
        }
        _queue.Writer.TryWrite((clientId, name));
    }

    /// <summary>Grants and persists approval for a client. Idempotent.</summary>
    public void Approve(string clientId, string name)
    {
        lock (_lock)
        {
            if (!_approved.Add(clientId)) return;
            _config.Current.ApprovedClients.Add(new ApprovedClient
            {
                Id = clientId,
                Name = name,
                ApprovedAt = _clock.UtcNow.ToString("o"),
            });
        }
        _config.Save();
        _log.Info($"approved client {name} ({Short(clientId)})");
    }

    /// <summary>The interactive prompt loop. Program runs this on its own thread; tests don't.</summary>
    public async Task RunConsoleLoopAsync(CancellationToken cancellation)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellation).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var req))
                {
                    if (IsApproved(req.id)) continue;
                    if (PromptYesNo(req.id, req.name)) Approve(req.id, req.name);
                    else _log.Warn($"denied client {req.name} ({Short(req.id)})");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private static bool PromptYesNo(string id, string name)
    {
        if (Console.IsInputRedirected) return false; // no terminal to ask — stays unapproved
        Console.WriteLine();
        Console.WriteLine("  ── approval request ────────────────────────────────────────");
        Console.WriteLine("  A client wants to publish play-throughs to your PDS:");
        Console.WriteLine($"      name: {name}");
        Console.WriteLine($"        id: {id}");
        Console.Write("  Approve and remember this client? [y/N]: ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        var ok = answer is "y" or "yes";
        Console.WriteLine(ok ? "  → approved.\n" : "  → denied.\n");
        return ok;
    }

    private static string Short(string id) => id.Length <= 12 ? id : id.Substring(0, 12) + "…";
}
