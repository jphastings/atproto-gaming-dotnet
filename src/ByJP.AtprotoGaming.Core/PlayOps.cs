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
    /// flush time — which is what makes an offline increment resolve against the
    /// real value rather than a stale one.
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

                    case "increment":
                    {
                        var name = op["name"]!.GetValue<string>();
                        var progress = Obj(record, "progress");
                        long current = 0;
                        if (progress[name] is JsonNode existing && !TryGetInteger(existing, out current))
                            throw new InvalidOperationException(
                                $"cannot increment progress '{name}': existing value is not an integer");
                        var sum = current + op["delta"]!.GetValue<int>();
                        // Stay int-backed for normal counters so callers can read
                        // GetValue<int>(); fall back to long only when it overflows.
                        progress[name] = sum >= int.MinValue && sum <= int.MaxValue ? (JsonNode)(int)sum : sum;
                        break;
                    }

                    case "addAcquisition":
                        Arr(record, "acquisitions").Add(op["item"]!.DeepClone());
                        break;

                    case "addRouteStop":
                        Arr(Obj(record, "progress"), "route").Add(op["stop"]!.DeepClone());
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
