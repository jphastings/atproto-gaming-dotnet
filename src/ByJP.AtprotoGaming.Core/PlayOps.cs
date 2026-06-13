using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Applies a serialized list of play-record changes (built by
    /// <see cref="PlayUpdate"/>) to a record. The ops are pure data so they can be
    /// persisted while offline and re-applied against the freshly-fetched record at
    /// flush time — which is what makes an offline <c>updateMetric</c> resolve
    /// against the real value, and instance-keyed appends idempotent across a crash.
    /// </summary>
    /// <remarks>
    /// The play record holds a single open-union <c>state[]</c> array of typed
    /// entries. Each entry's cardinality is set by the op that wrote it, mirroring
    /// the lexicon's shape convention: <c>singleton</c> (one entry per $type,
    /// replaced wholesale), <c>keyed</c> (unique per ($type, id), upserted), or
    /// <c>instanced</c> (unique per ($type, id, instanceId), appended). Every op
    /// here is safe to apply 2+ times against an evolving base.
    /// </remarks>
    internal static class PlayOps
    {
        public static void Apply(JsonObject record, JsonArray ops)
        {
            foreach (var node in ops)
            {
                var op = (JsonObject)node!;
                switch (op["op"]!.GetValue<string>())
                {
                    case "state":
                        ApplyState(record, op);
                        break;

                    case "bumpMetric":
                        ApplyBumpMetric(record, op);
                        break;

                    case "setAcquisitions":
                        ReplaceAllOfType(State(record), PlaySession.AcquisitionType, (JsonArray)op["items"]!);
                        break;

                    case "setSetup":
                        MergeSetup(record, (JsonObject)op["fields"]!);
                        break;

                    case "addModifier":
                        AddModifier(record, (JsonObject)op["modifier"]!);
                        break;

                    case "routeArrive":
                        ApplyRouteArrive(record, op);
                        break;

                    case "routeLeave":
                        ApplyRouteLeave(record, op);
                        break;

                    case "setOutcome":
                    {
                        var outcome = new JsonObject { ["type"] = op["type"]!.GetValue<string>() };
                        if (op["cause"] is JsonNode cause) outcome["cause"] = cause.GetValue<string>();
                        record["outcome"] = outcome;
                        break;
                    }

                    case "setParticipants":
                    {
                        var array = new JsonArray();
                        foreach (var participant in (JsonArray)op["participants"]!)
                            array.Add(participant!.DeepClone());
                        record["participants"] = array;
                        break;
                    }

                    case "finish":
                        record["endedAt"] = op["endedAt"]!.GetValue<string>();
                        record["duration"] = op["duration"]!.GetValue<int>();
                        break;

                    default:
                        throw new InvalidOperationException($"unknown play op: {op["op"]}");
                }
            }
        }

        // A generic state-entry write. The merge mode is set by the PlayUpdate method
        // that recorded it, matching the entry type's documented cardinality.
        private static void ApplyState(JsonObject record, JsonObject op)
        {
            var state = State(record);
            var type = op["type"]!.GetValue<string>();
            var entry = (JsonObject)op["entry"]!.DeepClone();
            entry["$type"] = type;

            switch (op["mode"]!.GetValue<string>())
            {
                case "singleton":
                    RemoveAllOfType(state, type);
                    state.Add(entry);
                    break;
                case "keyed":
                    UpsertKeyed(state, type, entry);
                    break;
                case "instanced":
                    AppendInstanced(state, type, entry);
                    break;
                default:
                    throw new InvalidOperationException($"unknown state mode: {op["mode"]}");
            }
        }

        private static void ApplyBumpMetric(JsonObject record, JsonObject op)
        {
            var state = State(record);
            var id = op["id"]!.GetValue<string>();
            var value = op["value"]!.GetValue<long>();
            var operation = op["operation"]!.GetValue<string>();

            var entry = FindKeyed(state, PlaySession.MetricType, id);
            bool present = entry?["value"] is JsonNode;
            long current = 0;
            if (present && !TryGetInteger(entry!["value"]!, out current))
                throw new InvalidOperationException(
                    $"cannot update metric '{id}': existing value is not an integer");

            long result;
            switch (operation)
            {
                case "add": result = current + value; break;
                case "subtract": result = current - value; break;
                case "min": result = present ? Math.Min(current, value) : value; break;
                case "max": result = present ? Math.Max(current, value) : value; break;
                default: throw new InvalidOperationException($"unknown metric operation: {operation}");
            }

            if (entry == null)
            {
                entry = new JsonObject { ["$type"] = PlaySession.MetricType, ["id"] = id };
                state.Add(entry);
            }
            // Stay int-backed for normal counters so callers can read GetValue<int>();
            // fall back to long only when it overflows.
            entry["value"] = result >= int.MinValue && result <= int.MaxValue ? (JsonNode)(int)result : result;
        }

        private static void MergeSetup(JsonObject record, JsonObject fields)
        {
            var setup = Singleton(State(record), PlaySession.SetupType);
            foreach (var field in fields)
                setup[field.Key] = field.Value?.DeepClone();
        }

        private static void AddModifier(JsonObject record, JsonObject modifier)
        {
            var setup = Singleton(State(record), PlaySession.SetupType);
            if (!(setup["modifiers"] is JsonArray modifiers))
            {
                modifiers = new JsonArray();
                setup["modifiers"] = modifiers;
            }
            UpsertById(modifiers, modifier, "id");
        }

        private static void ApplyRouteArrive(JsonObject record, JsonObject op)
        {
            var state = State(record);
            var instanceId = op["instanceId"]?.GetValue<string>();

            var existing = instanceId != null ? FindByInstanceId(state, PlaySession.RouteStopType, instanceId) : null;
            var stop = existing ?? NewStop(state, op);
            stop["arrivedAt"] = op["arrivedAt"]!.GetValue<string>();
            if (op["name"] is JsonNode name) stop["name"] = name.GetValue<string>();
        }

        private static void ApplyRouteLeave(JsonObject record, JsonObject op)
        {
            var state = State(record);
            var id = op["id"]!.GetValue<string>();
            var instanceId = op["instanceId"]?.GetValue<string>();

            var target = instanceId != null
                ? FindByInstanceId(state, PlaySession.RouteStopType, instanceId)
                : FindLastOpenStop(state, id);

            if (target != null)
                target["leftAt"] = op["leftAt"]!.GetValue<string>();
            else if (instanceId != null)
                // Explicit leave-before-arrive: the instanceId pins which stop this is.
                NewStop(state, op)["leftAt"] = op["leftAt"]!.GetValue<string>();
            // else: no open stop and no instanceId to pin one — nothing to close.
            // No-op keeps re-apply (CAS retry / offline flush) from minting a phantom
            // stop with a leftAt but no arrivedAt.
        }

        // Appends a fresh routeStop seeded with id (+ instanceId) from the op.
        private static JsonObject NewStop(JsonArray state, JsonObject op)
        {
            var stop = new JsonObject { ["$type"] = PlaySession.RouteStopType, ["id"] = op["id"]!.GetValue<string>() };
            if (op["instanceId"] is JsonNode iid) stop["instanceId"] = iid.GetValue<string>();
            state.Add(stop);
            return stop;
        }

        // ── state-array helpers ──────────────────────────────────────────────

        private static JsonArray State(JsonObject record) => Arr(record, "state");

        private static bool IsType(JsonNode? node, string type) =>
            node is JsonObject obj && obj["$type"]?.GetValue<string>() == type;

        // Append, or replace the entry of this type whose id matches (keyed upsert).
        private static void UpsertKeyed(JsonArray state, string type, JsonObject entry)
        {
            var id = entry["id"]?.GetValue<string>();
            if (id != null)
            {
                for (var i = 0; i < state.Count; i++)
                {
                    if (IsType(state[i], type) && state[i]!["id"]?.GetValue<string>() == id)
                    {
                        state[i] = entry;
                        return;
                    }
                }
            }
            state.Add(entry);
        }

        // Append, deduping by instanceId when present (so a re-emit after a crash
        // updates the same entry rather than duplicating it).
        private static void AppendInstanced(JsonArray state, string type, JsonObject entry)
        {
            var instanceId = entry["instanceId"]?.GetValue<string>();
            if (instanceId != null)
            {
                for (var i = 0; i < state.Count; i++)
                {
                    if (IsType(state[i], type) && state[i]!["instanceId"]?.GetValue<string>() == instanceId)
                    {
                        state[i] = entry;
                        return;
                    }
                }
            }
            state.Add(entry);
        }

        private static void ReplaceAllOfType(JsonArray state, string type, JsonArray items)
        {
            RemoveAllOfType(state, type);
            foreach (var item in items)
            {
                var entry = (JsonObject)item!.DeepClone();
                entry["$type"] = type;
                state.Add(entry);
            }
        }

        private static void RemoveAllOfType(JsonArray state, string type)
        {
            for (var i = state.Count - 1; i >= 0; i--)
                if (IsType(state[i], type)) state.RemoveAt(i);
        }

        // The lone entry of this type, created (and appended) if absent.
        private static JsonObject Singleton(JsonArray state, string type)
        {
            foreach (var node in state)
                if (IsType(node, type)) return (JsonObject)node!;
            var created = new JsonObject { ["$type"] = type };
            state.Add(created);
            return created;
        }

        private static JsonObject? FindKeyed(JsonArray state, string type, string id)
        {
            foreach (var node in state)
                if (IsType(node, type) && node!["id"]?.GetValue<string>() == id)
                    return (JsonObject)node;
            return null;
        }

        private static JsonObject? FindByInstanceId(JsonArray state, string type, string instanceId)
        {
            foreach (var node in state)
                if (IsType(node, type) && node!["instanceId"]?.GetValue<string>() == instanceId)
                    return (JsonObject)node;
            return null;
        }

        private static JsonObject? FindLastOpenStop(JsonArray state, string id)
        {
            for (var i = state.Count - 1; i >= 0; i--)
                if (IsType(state[i], PlaySession.RouteStopType)
                    && state[i]!["id"]?.GetValue<string>() == id
                    && !(state[i]!["leftAt"] is JsonNode))
                    return (JsonObject)state[i]!;
            return null;
        }

        // Append, or replace the entry whose key value matches (idempotent re-emit).
        // Used for the setup.modifiers sub-array, which is keyed by id.
        private static void UpsertById(JsonArray array, JsonObject item, string key)
        {
            var id = item[key]?.GetValue<string>();
            if (id != null)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonObject existing && existing[key]?.GetValue<string>() == id)
                    {
                        array[i] = item.DeepClone();
                        return;
                    }
                }
            }
            array.Add(item.DeepClone());
        }

        private static JsonArray Arr(JsonObject parent, string key)
        {
            if (parent[key] is JsonArray existing) return existing;
            var created = new JsonArray();
            parent[key] = created;
            return created;
        }

        private static bool TryGetInteger(JsonNode node, out long value)
        {
            value = 0;
            return node is JsonValue jv
                && jv.GetValueKind() == JsonValueKind.Number
                && long.TryParse(jv.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}
