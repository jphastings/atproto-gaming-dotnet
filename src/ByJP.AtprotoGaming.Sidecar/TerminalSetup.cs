using System;
using System.Text;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Sidecar;

/// <summary>
/// First-run / re-auth flow: instead of asking the player to hand-edit
/// <c>config.json</c>, prompt for the atproto handle + app password in the terminal,
/// persist them, and verify by logging in — re-prompting if the credentials are
/// missing or rejected. Falls back to running unconfigured when there's no terminal.
/// </summary>
internal static class TerminalSetup
{
    private const int MaxAttempts = 5;

    public static async Task EnsureCredentialsAsync(
        AtprotoGamingClient client, ConfigStore<SidecarConfig> config, ILogSink log)
    {
        var cfg = config.Current;
        var interactive = !Console.IsInputRedirected;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (string.IsNullOrWhiteSpace(cfg.Handle) || string.IsNullOrWhiteSpace(cfg.AppPassword))
            {
                if (!interactive)
                {
                    log.Warn("no atproto credentials and no terminal to prompt; " +
                             "running unconfigured — records will not publish");
                    return;
                }
                Prompt(cfg);
                config.Save();
            }

            await client.LoginAsync().ConfigureAwait(false);

            switch (client.Auth.Status)
            {
                case AuthStatus.Ok:
                    log.Info($"signed in as {client.Auth.Handle}");
                    return;

                case AuthStatus.Offline:
                    log.Warn("PDS unreachable right now; starting anyway — records will queue until you reconnect");
                    return;

                case AuthStatus.Failed:
                    log.Error($"sign-in failed: {client.Auth.Error}");
                    if (!interactive) return;
                    cfg.AppPassword = ""; // force a re-prompt (handle is offered as the default)
                    config.Save();
                    break;

                default: // Unconfigured / Checking — only reached if a prompt yielded blanks
                    if (!interactive) return;
                    cfg.AppPassword = "";
                    break;
            }
        }

        log.Error("giving up after several failed sign-in attempts; starting unconfigured");
    }

    private static void Prompt(SidecarConfig cfg)
    {
        Console.WriteLine();
        Console.WriteLine("  ── atproto sign-in ─────────────────────────────────────────");
        Console.WriteLine("  Publish your play-throughs to your atproto PDS.");
        var handlePrompt = string.IsNullOrEmpty(cfg.Handle)
            ? "  handle (e.g. you.bsky.social): "
            : $"  handle [{cfg.Handle}]: ";
        Console.Write(handlePrompt);
        var handle = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(handle)) cfg.Handle = handle;
        cfg.AppPassword = ReadSecret("  app password (bsky.app/settings/app-passwords): ");
        Console.WriteLine();
    }

    /// <summary>Reads a line without echoing it (shows <c>*</c> per character).</summary>
    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
