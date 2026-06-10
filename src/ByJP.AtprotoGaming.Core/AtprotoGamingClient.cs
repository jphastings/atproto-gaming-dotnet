using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
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
        private readonly IClock _clock;

        private readonly PlayWriter _playWriter;

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
            _clock = options.Clock ?? SystemClock.Instance;
            var clock = _clock;
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

            var stats = new StatsResolver(Client, _config, clock);
            var playQueue = new PlayQueue(fs, _log);
            _playWriter = new PlayWriter(Client, Auth, clock, _log, stats, playQueue, versions, signer);

            Stats = new RollingStats(Client, stats, clock);
            Achievements = new AchievementDeduper();
        }

        /// <summary>
        /// Opens a <see cref="PlaySession"/> for a play-through: you supply the id
        /// and the game/version metadata, and it builds the record for you. Declare
        /// changes through <see cref="PlaySession.BeginUpdate"/>; the read-modify-write
        /// with optimistic locking is handled underneath.
        /// </summary>
        /// <param name="playId">
        /// The play's id (record key). Used as-is if it's a valid record key
        /// (see <see cref="RecordKey"/>), otherwise sanitised deterministically.
        /// Use <see cref="PlaySession.DerivePlayID"/> for a TID-shaped id.
        /// </param>
        /// <param name="game">The game's AT URI (e.g. its cartridge.dev record). Must be a valid AT URI.</param>
        /// <param name="gameVersion">The game's version string, stored in <c>versions.game</c>.</param>
        /// <param name="source">The platform the run is on (see <see cref="StatsSource"/>), used to find/create the rolling stats record.</param>
        /// <param name="additionalVersions">Optional mod name → version entries for <c>versions.additional</c> (the package adds its own).</param>
        public PlaySession OpenPlay(string playId, string game, string gameVersion, string source,
            IReadOnlyDictionary<string, string>? additionalVersions = null)
        {
            if (string.IsNullOrEmpty(playId)) throw new ArgumentNullException(nameof(playId));
            if (string.IsNullOrEmpty(gameVersion)) throw new ArgumentNullException(nameof(gameVersion));
            if (!AtUri.IsValid(game)) throw new ArgumentException($"not a valid AT URI: {game}", nameof(game));

            if (string.IsNullOrEmpty(source)) throw new ArgumentNullException(nameof(source));

            var rkey = RecordKey.Sanitize(playId);
            var startedAt = _clock.UtcNow.ToUniversalTime().ToString("o");

            JsonObject Seed() => PlaySession.BuildSeed(game, gameVersion, additionalVersions, startedAt);
            return new PlaySession(_playWriter, rkey, Seed, source, _clock);
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
            await _playWriter.FlushAsync().ConfigureAwait(false);
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
