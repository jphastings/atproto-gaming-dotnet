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
    /// flush time — which is what makes an offline <c>updateProgress</c> resolve
    /// against the real value, and instance-keyed appends idempotent across a crash.
    /// </summary>
    internal static class PlayOps
    {
        public static void Apply(JsonObject record, JsonArray ops)
        {
            foreach (var node in ops)
            {
                var op = (JsonObject)node!;
                switch (op["op"]!.GetValue<string>())
                {
                    case "setProgress":
                        Obj(record, "progress")[op["name"]!.GetValue<string>()] = op["value"]!.DeepClone();
                        break;

                    case "updateProgress":
                        ApplyUpdateProgress(record, op);
                        break;

                    case "setAcquisitions":
                        record["acquisitions"] = op["items"]!.DeepClone();
                        break;

                    case "addAcquisition":
                        UpsertById(Arr(record, "acquisitions"), (JsonObject)op["item"]!, "instanceId");
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
                        Obj(record, "progress")["outcome"] = outcome;
                        break;
                    }

                    case "setSetting":
                        Obj(record, "settings")[op["name"]!.GetValue<string>()] = op["value"]!.DeepClone();
                        break;

                    case "setPlayingWith":
                    {
                        var array = new JsonArray();
                        foreach (var participant in (JsonArray)op["participants"]!)
                            array.Add(participant!.DeepClone());
                        record["playingWith"] = array;
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

        private static void ApplyUpdateProgress(JsonObject record, JsonObject op)
        {
            var name = op["name"]!.GetValue<string>();
            var value = op["value"]!.GetValue<long>();
            var operation = op["operation"]!.GetValue<string>();
            var progress = Obj(record, "progress");

            bool present = progress[name] is JsonNode;
            long current = 0;
            if (present && !TryGetInteger(progress[name]!, out current))
                throw new InvalidOperationException(
                    $"cannot update progress '{name}': existing value is not an integer");

            long result;
            switch (operation)
            {
                case "add": result = current + value; break;
                case "subtract": result = current - value; break;
                case "min": result = present ? Math.Min(current, value) : value; break;
                case "max": result = present ? Math.Max(current, value) : value; break;
                default: throw new InvalidOperationException($"unknown progress operation: {operation}");
            }

            // Stay int-backed for normal counters so callers can read GetValue<int>();
            // fall back to long only when it overflows.
            progress[name] = result >= int.MinValue && result <= int.MaxValue ? (JsonNode)(int)result : result;
        }

        private static void ApplyRouteArrive(JsonObject record, JsonObject op)
        {
            var route = Arr(Obj(record, "progress"), "route");
            var instanceId = op["instanceId"]?.GetValue<string>();

            var existing = instanceId != null ? FindByInstanceId(route, instanceId) : null;
            var stop = existing ?? NewStop(route, op);
            stop["arrivedAt"] = op["arrivedAt"]!.GetValue<string>();
            if (op["name"] is JsonNode name) stop["name"] = name.GetValue<string>();
        }

        private static void ApplyRouteLeave(JsonObject record, JsonObject op)
        {
            var route = Arr(Obj(record, "progress"), "route");
            var id = op["id"]!.GetValue<string>();
            var instanceId = op["instanceId"]?.GetValue<string>();

            var target = instanceId != null
                ? FindByInstanceId(route, instanceId)
                : FindLastOpenStop(route, id);

            if (target != null)
                target["leftAt"] = op["leftAt"]!.GetValue<string>();
            else if (instanceId != null)
                // Explicit leave-before-arrive: the instanceId pins which stop this is.
                NewStop(route, op)["leftAt"] = op["leftAt"]!.GetValue<string>();
            // else: no open stop and no instanceId to pin one — nothing to close.
            // No-op keeps re-apply (CAS retry / offline flush) from minting a phantom
            // stop with a leftAt but no arrivedAt.
        }

        // Appends a fresh routeStop seeded with id (+ instanceId) from the op.
        private static JsonObject NewStop(JsonArray route, JsonObject op)
        {
            var stop = new JsonObject { ["$type"] = PlaySession.RouteStopType, ["id"] = op["id"]!.GetValue<string>() };
            if (op["instanceId"] is JsonNode iid) stop["instanceId"] = iid.GetValue<string>();
            route.Add(stop);
            return stop;
        }

        // Append, or replace the entry whose key value matches (idempotent re-emit).
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

        private static JsonObject? FindByInstanceId(JsonArray array, string instanceId)
        {
            foreach (var node in array)
                if (node is JsonObject obj && obj["instanceId"]?.GetValue<string>() == instanceId)
                    return obj;
            return null;
        }

        private static JsonObject? FindLastOpenStop(JsonArray route, string id)
        {
            for (var i = route.Count - 1; i >= 0; i--)
                if (route[i] is JsonObject stop
                    && stop["id"]?.GetValue<string>() == id
                    && !(stop["leftAt"] is JsonNode))
                    return stop;
            return null;
        }

        private static JsonObject Obj(JsonObject parent, string key)
        {
            if (parent[key] is JsonObject existing) return existing;
            var created = new JsonObject();
            parent[key] = created;
            return created;
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
