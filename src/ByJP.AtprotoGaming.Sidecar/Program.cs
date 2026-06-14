using System;
using System.Threading;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Adapters;
using ByJP.AtprotoGaming.Sidecar;

var log = new ConsoleLogSink("atproto-sidecar");
var fs = FileSystem.At(AppContext.BaseDirectory);
// Suppress ConfigStore's "edit this file" banner — the sidecar runs its own
// interactive setup instead of asking the player to hand-edit config.json.
var configStore = ConfigStore<SidecarConfig>.LoadOrCreate(fs, NullLogSink.Instance);
var config = configStore.Current;

SigningKey? signingKey = null;
if (!string.IsNullOrWhiteSpace(config.SigningDidKey))
{
    try
    {
        signingKey = SigningKey.FromDidKey(config.SigningDidKey, config.AttestationType);
    }
    catch (Exception ex)
    {
        log.Error($"signing disabled — {ex.Message}");
    }
}

var client = new AtprotoGamingClient(new AtprotoGamingOptions
{
    FileSystem = fs,
    Log = log,
    Config = configStore,
    SigningKey = signingKey,
});

// Prompt for (and verify) credentials in the terminal rather than via a config file.
await TerminalSetup.EnsureCredentialsAsync(client, configStore, log);

var approvals = new ApprovalService(configStore, SystemClock.Instance, log);
var initialDelay = TimeSpan.FromSeconds(config.InitialPublishDelaySeconds >= 0 ? config.InitialPublishDelaySeconds : 15);
var publishInterval = TimeSpan.FromSeconds(config.PublishIntervalSeconds > 0 ? config.PublishIntervalSeconds : 60);
var processor = new CommandProcessor(client, approvals, SystemClock.Instance, initialDelay, publishInterval, signingKey != null, log);
var server = new WireServer(config.Port, processor, log);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    log.Info("shutting down");
    cancellation.Cancel();
};

// Fire-and-forget: a blocking prompt mustn't hold up shutdown.
_ = Task.Run(() => approvals.RunConsoleLoopAsync(cancellation.Token));
try
{
    await server.RunAsync(cancellation.Token);
}
catch (OperationCanceledException)
{
}
