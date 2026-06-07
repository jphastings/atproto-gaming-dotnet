using System;
using System.Net.Http;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;
using ByJP.AtprotoGaming.Core.Signing;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>Construction inputs for <see cref="AtprotoGamingClient"/>.</summary>
    public sealed class AtprotoGamingOptions
    {
        /// <summary>Where config + outbox live. Required.</summary>
        public IFileSystem FileSystem { get; set; } = null!;

        /// <summary>The package's only output channel. Required.</summary>
        public ILogSink Log { get; set; } = null!;

        /// <summary>The loaded config store (a <see cref="ConfigStore{T}"/>). Required.</summary>
        public IConfigStore Config { get; set; } = null!;

        /// <summary>Time source; defaults to wall-clock.</summary>
        public IClock Clock { get; set; } = SystemClock.Instance;

        /// <summary>Optional ECDSA signing key; when set, published records are signed.</summary>
        public SigningKey? SigningKey { get; set; }

        /// <summary>Override the package version stamped into <c>versions.additional</c> (defaults to the assembly version).</summary>
        public string? PackageVersionOverride { get; set; }

        /// <summary>Optional shared <see cref="HttpClient"/>. One is created if omitted.</summary>
        public HttpClient? HttpClient { get; set; }
    }

    /// <summary>
    /// The one object a consumer constructs and holds. Wires the XRPC client,
    /// identity resolution, auth state, outbox, publisher, rolling stats and
    /// achievement de-dup from a few adapters, and owns the boot/login flow. Starts
    /// no background threads — drive <see cref="LoginAsync"/> from your own task.
    /// </summary>
    public sealed class AtprotoGamingClient
    {
        private readonly IConfigStore _config;
        private readonly ILogSink _log;

        public AuthState Auth { get; }
        public AtprotoClient Client { get; }
        public IdentityResolver Identity { get; }
        public SteamDidResolver Steam { get; }
        public Outbox Outbox { get; }
        public RecordPublisher Records { get; }
        public RollingStats Stats { get; }
        public AchievementDeduper Achievements { get; }

        public AtprotoGamingClient(AtprotoGamingOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _log = options.Log ?? throw new ArgumentException("Log is required", nameof(options));
            _config = options.Config ?? throw new ArgumentException("Config is required", nameof(options));
            var fs = options.FileSystem ?? throw new ArgumentException("FileSystem is required", nameof(options));
            var clock = options.Clock ?? SystemClock.Instance;
            var http = options.HttpClient ?? new HttpClient();

            Auth = new AuthState();
            Client = new AtprotoClient(Auth, clock, _log, http);
            Identity = new IdentityResolver(http);
            Steam = new SteamDidResolver(_log, http);
            Outbox = new Outbox(fs, _log, Auth, Client);

            var versions = new VersionsInjector(options.PackageVersionOverride);
            var signer = options.SigningKey != null ? new RecordSigner(options.SigningKey) : null;
            if (signer != null)
                _log.Info($"atproto record signing enabled ({options.SigningKey!.PublicDidKey})");

            Records = new RecordPublisher(Client, Auth, Outbox, _log, versions, signer);
            Stats = new RollingStats(Client, _config, _log, clock);
            Achievements = new AchievementDeduper();
        }

        /// <summary>
        /// Resolves the configured handle, logs in, and drains the outbox on
        /// success — driving <see cref="Auth"/> through Checking → Ok/Failed/Offline.
        /// Safe to call again after the user edits their config. Run it from a task
        /// (e.g. <c>Task.Run</c>) on game load.
        /// </summary>
        public async Task LoginAsync()
        {
            var cfg = _config.Core;
            if (string.IsNullOrWhiteSpace(cfg.Handle) || string.IsNullOrWhiteSpace(cfg.AppPassword))
            {
                Auth.Set(AuthStatus.Unconfigured);
                return;
            }

            Auth.Set(AuthStatus.Checking, handle: cfg.Handle);

            MiniDoc doc;
            try
            {
                doc = await Identity.ResolveAsync(cfg.Handle).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SeedOfflineOrFail(cfg, ex);
                return;
            }

            // Persist the resolved identity for offline-boot seeding next time.
            cfg.CachedHandle = doc.Handle;
            cfg.CachedDid = doc.Did;
            cfg.CachedPds = doc.Pds;
            _config.Save();

            try
            {
                await Client.LoginAsync(doc.Pds, cfg.Handle, cfg.AppPassword).ConfigureAwait(false);
            }
            catch (AtprotoPermanentException ex)
            {
                // Bad credentials: keep the cached DID so records can still bucket.
                Auth.Set(AuthStatus.Failed, handle: doc.Handle, did: doc.Did, pds: doc.Pds,
                    error: ex.PdsError ?? ex.Message);
                _log.Error($"atproto login rejected for {doc.Handle}: {ex.PdsError ?? ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                // PDS unreachable: go Offline with the known DID so records queue.
                Auth.Set(AuthStatus.Offline, handle: doc.Handle, did: doc.Did, pds: doc.Pds, error: ex.Message);
                _log.Warn($"PDS unreachable for {doc.Handle}; queueing offline ({ex.Message})");
                return;
            }

            Auth.Set(AuthStatus.Ok, handle: doc.Handle, did: doc.Did, pds: doc.Pds);
            _log.Info($"logged in to atproto as {doc.Handle} ({doc.Did})");
            await Outbox.FlushAsync().ConfigureAwait(false);
        }

        private void SeedOfflineOrFail(CoreConfig cfg, Exception ex)
        {
            // Seed AuthState from cache so queued records have a DID bucket — but
            // only if the cache matches the current handle (invalidate on change).
            var cacheUsable = !string.IsNullOrEmpty(cfg.CachedDid)
                && string.Equals(cfg.CachedHandle, cfg.Handle, StringComparison.OrdinalIgnoreCase);

            if (cacheUsable)
            {
                Auth.Set(AuthStatus.Offline, handle: cfg.CachedHandle, did: cfg.CachedDid, pds: cfg.CachedPds,
                    error: ex.Message);
                _log.Warn($"offline at boot; seeding cached DID {cfg.CachedDid} so records queue");
            }
            else
            {
                Auth.Set(AuthStatus.Failed, handle: cfg.Handle,
                    error: $"couldn't resolve identity and no usable cache: {ex.Message}");
                _log.Error($"identity resolution failed and no cached DID for {cfg.Handle}", ex);
            }
        }
    }
}
