using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// A high-level handle for one <c>games.gamesgamesgamesgames.actor.play</c>
    /// record across a play-through. You don't write through it directly — you
    /// <see cref="BeginUpdate"/> a transaction, record changes ("score is now N",
    /// "item acquired", "finished") on it, then <see cref="PlayUpdate.CommitAsync"/>
    /// to write them in one optimistic read-modify-write (queued and re-applied
    /// against the real record if offline).
    /// </summary>
    /// <remarks>
    /// Obtain one via <see cref="AtprotoGamingClient.OpenPlay"/>. It caches the
    /// record's CID across commits, so each commit is a single PUT on the happy path.
    /// </remarks>
    public sealed class PlaySession
    {
        public const string Collection = "games.gamesgamesgamesgames.actor.play";
        public const string GameItemType = "games.gamesgamesgamesgames.actor.play#gameItem";
        public const string RouteStopType = "games.gamesgamesgamesgames.actor.play#routeStop";

        private readonly PlayWriter _writer;
        private readonly string _rkey;
        private readonly Func<JsonObject> _seed;
        private readonly string _source;
        private readonly IClock _clock;

        private JsonObject? _value; // last-delivered record value (domain shape)
        private string? _cid;       // its CID, for the next optimistic swap

        internal PlaySession(PlayWriter writer, string rkey, Func<JsonObject> seed, string source, IClock clock)
        {
            _writer = writer;
            _rkey = rkey;
            _seed = seed;
            _source = source;
            _clock = clock;
        }

        /// <summary>The play's record key.</summary>
        public string Rkey => _rkey;

        /// <summary>
        /// Derives a TID-shaped play id from a start time and a seed string — the
        /// same <c>(startedAt, seed)</c> yields the same id on every run and every
        /// multiplayer participant. Pass the result as <c>playId</c> to
        /// <see cref="AtprotoGamingClient.OpenPlay"/>.
        /// </summary>
        public static string DerivePlayID(DateTimeOffset startedAt, string seed) =>
            Tid.FromPlayThrough(startedAt.ToUnixTimeSeconds(), seed);

        /// <summary>
        /// Opens a transaction. Record changes on it, then call
        /// <see cref="PlayUpdate.CommitAsync"/> to write them all as one record
        /// update. Nothing is delivered until commit; the first commit creates the
        /// record (from the seed) if it doesn't exist yet.
        /// </summary>
        public PlayUpdate BeginUpdate() => new PlayUpdate(CommitOpsAsync, _clock);

        /// <summary>
        /// Forks this play into a new record: clones the current values, sets
        /// <c>forkedFrom</c> to a strongRef of this record, and drops the terminal
        /// markers (<c>endedAt</c>/<c>duration</c>/<c>outcome</c>) so the fork resumes
        /// in-progress. Requires this play to have been committed at least once.
        /// </summary>
        /// <param name="id">
        /// The fork's id; used as-is if a valid record key, otherwise sanitised.
        /// When omitted, a fresh id is derived.
        /// </param>
        public PlaySession ForkPlay(string? id = null)
        {
            if (_value == null)
                throw new InvalidOperationException("commit the play before forking it");
            return _writer.Fork(_rkey, _value, _cid, id, _source);
        }

        private async Task<PutResult> CommitOpsAsync(JsonArray ops)
        {
            var outcome = await _writer.CommitAsync(_rkey, _seed(), _source, ops, _value, _cid).ConfigureAwait(false);
            if (outcome.Value != null)
            {
                _value = outcome.Value;
                _cid = outcome.Cid;
            }
            // Best-effort drain of any other plays queued while offline.
            if (outcome.Result.Status == PutStatus.Published)
                await _writer.FlushAsync().ConfigureAwait(false);
            return outcome.Result;
        }

        /// <summary>
        /// Builds the initial play-record body used when the record doesn't exist
        /// yet. Carries <c>startedAt</c> (only written on create, since the seed is
        /// the base only when absent) and the consumer's versions; <c>updatedAt</c>
        /// is stamped at commit, the package's own version entry is injected at write
        /// time, and <c>stats</c> is resolved at write time — so none belong here.
        /// </summary>
        internal static JsonObject BuildSeed(string game, string gameVersion,
            IReadOnlyDictionary<string, string>? additionalVersions, string startedAtIso)
        {
            var additional = new JsonArray();
            if (additionalVersions != null)
                foreach (var entry in additionalVersions)
                    additional.Add(new JsonObject { ["name"] = entry.Key, ["version"] = entry.Value });

            return new JsonObject
            {
                ["$type"] = Collection,
                ["game"] = game,
                ["startedAt"] = startedAtIso,
                ["versions"] = new JsonObject
                {
                    ["game"] = gameVersion,
                    ["additional"] = additional,
                },
            };
        }
    }
}
