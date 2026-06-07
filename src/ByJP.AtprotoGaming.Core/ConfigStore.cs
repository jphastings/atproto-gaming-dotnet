using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ByJP.AtprotoGaming.Core.Adapters;
using ByJP.AtprotoGaming.Core.Internal;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Loads/saves a consumer config DTO (subclass of <see cref="CoreConfig"/>)
    /// as JSON. Creates a template with a visible "not configured" banner on first
    /// run; saves atomically; tolerates a corrupt file from a crashed session.
    /// Mutating a field does not auto-persist — call <see cref="Save"/>.
    /// </summary>
    public sealed class ConfigStore<T> : IConfigStore where T : CoreConfig, new()
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        };

        private readonly string _path;
        private readonly ILogSink _log;

        /// <summary>The live, mutable config.</summary>
        public T Current { get; }

        CoreConfig IConfigStore.Core => Current;

        private ConfigStore(T current, string path, ILogSink log)
        {
            Current = current;
            _path = path;
            _log = log;
        }

        /// <summary>
        /// Loads <c>config.json</c> from <see cref="IFileSystem.ConfigDirectory"/>,
        /// or writes a template (and logs the banner) if it's missing. Re-logs the
        /// banner if the handle/password are still blank.
        /// </summary>
        public static ConfigStore<T> LoadOrCreate(IFileSystem fs, ILogSink log)
        {
            if (fs == null) throw new ArgumentNullException(nameof(fs));
            if (log == null) throw new ArgumentNullException(nameof(log));

            Directory.CreateDirectory(fs.ConfigDirectory);
            var path = Path.Combine(fs.ConfigDirectory, "config.json");

            T config;
            if (File.Exists(path))
            {
                config = LoadExisting(path, log);
            }
            else
            {
                config = new T();
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(config, JsonOpts));
                LogFirstRunBanner(log, path);
            }

            var store = new ConfigStore<T>(config, path, log);
            if (string.IsNullOrWhiteSpace(config.Handle) || string.IsNullOrWhiteSpace(config.AppPassword))
                LogFirstRunBanner(log, path);
            return store;
        }

        private static T LoadExisting(string path, ILogSink log)
        {
            try
            {
                var text = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(text, JsonOpts) ?? new T();
            }
            catch (Exception ex)
            {
                // Don't overwrite — the user may have a fixable typo. Run with
                // defaults this session and leave their file for them to repair.
                log.Error($"config at {path} is unreadable ({ex.Message}); ignoring it this session", ex);
                return new T();
            }
        }

        public void Save() => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOpts));

        private static void LogFirstRunBanner(ILogSink log, string path)
        {
            log.Warn("========================================================================");
            log.Warn("atproto-gaming: not yet configured — nothing will be posted to your PDS.");
            log.Warn("Edit this file, then restart the game:");
            log.Warn($"  {path}");
            log.Warn("Set 'handle' to your atproto handle (e.g. you.bsky.social) and");
            log.Warn("'appPassword' to an app password from https://bsky.app/settings/app-passwords");
            log.Warn("========================================================================");
        }
    }
}
