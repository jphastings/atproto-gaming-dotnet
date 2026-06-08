using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using ByJP.AtprotoGaming.Core.Adapters;
using ByJP.AtprotoGaming.Core.Internal;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>One pending play-record's worth of changes waiting to be delivered.</summary>
    internal sealed class PlayQueueEntry
    {
        public string Rkey { get; }
        public JsonObject Seed { get; }
        public string Source { get; }
        public JsonArray Ops { get; }

        public PlayQueueEntry(string rkey, JsonObject seed, string source, JsonArray ops)
        {
            Rkey = rkey;
            Seed = seed;
            Source = source;
            Ops = ops;
        }
    }

    /// <summary>
    /// On-disk store of pending play-record operations, bucketed by DID. Lives under
    /// <c>&lt;outbox&gt;/play-queue/</c> — outside the generic outbox's per-DID
    /// collection tree, so the byte-outbox never tries to replay these op-envelopes
    /// as records. Each entry holds the seed (to create the record if absent), the
    /// platform source (to resolve stats), and the accumulated ops.
    /// </summary>
    internal sealed class PlayQueue
    {
        private readonly IFileSystem _fs;
        private readonly ILogSink _log;

        public PlayQueue(IFileSystem fs, ILogSink log)
        {
            _fs = fs;
            _log = log;
        }

        private string Root => Path.Combine(_fs.OutboxRoot, "play-queue");
        private string DidDir(string did) => Path.Combine(Root, DidPath.EncodeDid(did));
        private string FilePath(string did, string rkey) => Path.Combine(DidDir(did), rkey + ".json");

        /// <summary>Appends ops for (did, rkey), merging with any already pending. Atomic.</summary>
        public void Append(string did, string rkey, JsonObject seed, string source, JsonArray ops)
        {
            var existing = Read(did, rkey);
            JsonArray merged;
            if (existing != null)
            {
                merged = existing.Ops;
                foreach (var op in ops) merged.Add(op!.DeepClone());
            }
            else
            {
                merged = (JsonArray)ops.DeepClone();
            }

            var envelope = new JsonObject
            {
                ["seed"] = (existing?.Seed ?? seed).DeepClone(),
                ["source"] = source,
                ["ops"] = merged,
            };
            AtomicFile.WriteAllText(FilePath(did, rkey), envelope.ToJsonString());
        }

        public PlayQueueEntry? Read(string did, string rkey)
        {
            var path = FilePath(did, rkey);
            if (!File.Exists(path)) return null;
            try
            {
                if (!(JsonNode.Parse(File.ReadAllText(path)) is JsonObject env)) return null;
                return new PlayQueueEntry(
                    rkey,
                    (JsonObject)env["seed"]!,
                    env["source"]?.GetValue<string>() ?? "",
                    (JsonArray)env["ops"]!);
            }
            catch (Exception ex)
            {
                _log.Warn($"play-queue: corrupt entry {path} ({ex.Message}) — discarding");
                Remove(did, rkey);
                return null;
            }
        }

        public IEnumerable<PlayQueueEntry> List(string did)
        {
            var dir = DidDir(did);
            if (!Directory.Exists(dir)) yield break;
            foreach (var path in Directory.GetFiles(dir, "*.json"))
            {
                var entry = Read(did, Path.GetFileNameWithoutExtension(path));
                if (entry != null) yield return entry;
            }
        }

        public void Remove(string did, string rkey)
        {
            try
            {
                var path = FilePath(did, rkey);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _log.Warn($"play-queue: couldn't remove {rkey}: {ex.Message}");
            }
        }
    }
}
