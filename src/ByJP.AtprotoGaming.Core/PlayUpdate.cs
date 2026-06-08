using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// An open transaction over a <c>games.gamesgamesgamesgames.actor.play</c>
    /// record. The helper methods record changes as serializable ops in memory (no
    /// network); a single <see cref="CommitAsync"/> writes them all as one record
    /// update. While offline the ops are persisted and re-applied against the real
    /// record at flush, so an <see cref="IncrementProgress"/> resolves against the
    /// actual value rather than a stale one.
    /// </summary>
    /// <remarks>
    /// Open one with <see cref="PlaySession.BeginUpdate"/>. Helpers may be called
    /// across many frames/handlers before committing; calls are thread-safe and
    /// chainable. A transaction commits exactly once.
    /// </remarks>
    public sealed class PlayUpdate
    {
        private readonly Func<JsonArray, Task<PutResult>> _commit;
        private readonly object _lock = new object();
        private readonly JsonArray _ops = new JsonArray();
        private bool _committed;

        internal PlayUpdate(Func<JsonArray, Task<PutResult>> commit)
        {
            _commit = commit;
        }

        /// <summary>Number of changes recorded so far.</summary>
        public int Count { get { lock (_lock) return _ops.Count; } }

        /// <summary>Sets an open-ended <c>progress</c> indicator (e.g. <c>"score"</c>, <c>"hp"</c>, <c>"gold"</c>).</summary>
        public PlayUpdate SetProgress(string name, AtValue value)
        {
            GuardProgressName(name);
            return Record(new JsonObject { ["op"] = "setProgress", ["name"] = name, ["value"] = value.ToNode() });
        }

        /// <summary>
        /// Adds <paramref name="delta"/> to an integer <c>progress</c> indicator
        /// (an absent one counts as 0), resolved against the real value at write
        /// time. Fails at commit/flush if the existing value isn't an integer.
        /// </summary>
        public PlayUpdate IncrementProgress(string name, int delta)
        {
            GuardProgressName(name);
            return Record(new JsonObject { ["op"] = "increment", ["name"] = name, ["delta"] = delta });
        }

        /// <summary>Appends an acquired item to <c>acquisitions[]</c> (a <c>#gameItem</c>; <c>id</c> required).</summary>
        public PlayUpdate AddAcquisition(JsonObject item)
        {
            var snap = RequireId(item, "acquisition");
            EnsureType(snap, PlaySession.GameItemType);
            return Record(new JsonObject { ["op"] = "addAcquisition", ["item"] = snap });
        }

        /// <summary>Appends a stop to <c>progress.route[]</c> (a <c>#routeStop</c>; <c>id</c> required).</summary>
        public PlayUpdate AddRouteStop(JsonObject stop)
        {
            var snap = RequireId(stop, "route stop");
            EnsureType(snap, PlaySession.RouteStopType);
            return Record(new JsonObject { ["op"] = "addRouteStop", ["stop"] = snap });
        }

        /// <summary>Sets <c>progress.outcome</c> (the end-of-play marker).</summary>
        public PlayUpdate SetOutcome(string type, string? cause = null)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentNullException(nameof(type));
            var op = new JsonObject { ["op"] = "setOutcome", ["type"] = type };
            if (!string.IsNullOrEmpty(cause)) op["cause"] = cause;
            return Record(op);
        }

        /// <summary>Sets a pre-run value in <c>settings</c> (e.g. <c>"character"</c>, <c>"difficulty"</c>, <c>"seed"</c>).</summary>
        public PlayUpdate SetSetting(string name, AtValue value)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            return Record(new JsonObject { ["op"] = "setSetting", ["name"] = name, ["value"] = value.ToNode() });
        }

        /// <summary>Sets <c>playingWith[]</c> to the given participants, replacing any previous list.</summary>
        public PlayUpdate SetPlayingWith(IEnumerable<JsonObject> participants)
        {
            if (participants == null) throw new ArgumentNullException(nameof(participants));
            var array = new JsonArray();
            foreach (var participant in participants)
            {
                if (participant == null) throw new ArgumentException("participant cannot be null", nameof(participants));
                array.Add((JsonObject)participant.DeepClone());
            }
            return Record(new JsonObject { ["op"] = "setPlayingWith", ["participants"] = array });
        }

        /// <summary>Marks the play-through finished: sets <c>endedAt</c> and <c>duration</c>.</summary>
        public PlayUpdate Finish(string endedAtIso, int durationSeconds)
        {
            if (string.IsNullOrEmpty(endedAtIso)) throw new ArgumentNullException(nameof(endedAtIso));
            return Record(new JsonObject { ["op"] = "finish", ["endedAt"] = endedAtIso, ["duration"] = durationSeconds });
        }

        /// <summary>
        /// Writes every recorded change as one record update (online: a single PUT
        /// with optimistic locking; offline: persisted and flushed later), stamping
        /// <c>updatedAt</c>. A transaction commits exactly once.
        /// </summary>
        public Task<PutResult> CommitAsync()
        {
            JsonArray snapshot;
            lock (_lock)
            {
                if (_committed) throw new InvalidOperationException("this play update has already been committed");
                _committed = true;
                snapshot = (JsonArray)_ops.DeepClone();
            }
            return _commit(snapshot);
        }

        private PlayUpdate Record(JsonObject op)
        {
            lock (_lock)
            {
                if (_committed) throw new InvalidOperationException("this play update has already been committed");
                _ops.Add(op);
            }
            return this;
        }

        // progress.outcome and progress.route are structured and have dedicated
        // helpers — steer callers there rather than letting them clobber the shape.
        private static void GuardProgressName(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (name == "outcome")
                throw new ArgumentException("use SetOutcome(...) to set progress.outcome", nameof(name));
            if (name == "route")
                throw new ArgumentException("use AddRouteStop(...) to add to progress.route", nameof(name));
        }

        private static void EnsureType(JsonObject node, string type)
        {
            if (node["$type"] == null) node["$type"] = type;
        }

        private static JsonObject RequireId(JsonObject node, string what)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(node["id"]?.GetValue<string>()))
                throw new ArgumentException($"{what} requires a non-empty 'id'", nameof(node));
            return (JsonObject)node.DeepClone();
        }
    }
}
