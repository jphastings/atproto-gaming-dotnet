using System.Text.Json;
using System.Text.Json.Nodes;

namespace ByJP.AtprotoGaming.Sidecar;

/// <summary>
/// A parsed request line. Typed accessors raise <see cref="WireException"/> with
/// <c>missingField</c> / <c>invalidValue</c> so the processor can turn a malformed
/// command into a clean <c>ok:false</c> reply without crashing the connection.
/// </summary>
internal sealed class Request
{
    private readonly JsonObject _obj;

    private Request(JsonObject obj) => _obj = obj;

    /// <summary>Parses one NDJSON line. Throws <see cref="WireException"/> if it isn't a JSON object.</summary>
    public static Request Parse(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException ex) { throw new WireException(WireProtocol.Errors.InvalidValue, $"not valid JSON: {ex.Message}"); }
        if (node is not JsonObject obj)
            throw new WireException(WireProtocol.Errors.InvalidValue, "each message must be a JSON object");
        return new Request(obj);
    }

    public string Command => Str("cmd");

    /// <summary>The client's correlation id, echoed back as <c>re</c>. May be a number or string.</summary>
    public JsonNode? CorrelationId => _obj["id"]?.DeepClone();

    public bool Has(string field) => _obj.TryGetPropertyValue(field, out var v) && v is not null;

    public string Str(string field)
    {
        if (!_obj.TryGetPropertyValue(field, out var v) || v is null)
            throw new WireException(WireProtocol.Errors.MissingField, $"missing required field '{field}'");
        if (v is not JsonValue val || val.GetValueKind() != JsonValueKind.String)
            throw new WireException(WireProtocol.Errors.InvalidValue, $"field '{field}' must be a string");
        return val.GetValue<string>();
    }

    public string? OptStr(string field) => Has(field) ? Str(field) : null;

    public long Long(string field)
    {
        if (!_obj.TryGetPropertyValue(field, out var v) || v is null)
            throw new WireException(WireProtocol.Errors.MissingField, $"missing required field '{field}'");
        if (v is not JsonValue val || val.GetValueKind() != JsonValueKind.Number || !val.TryGetValue<long>(out var n))
            throw new WireException(WireProtocol.Errors.InvalidValue, $"field '{field}' must be an integer");
        return n;
    }

    public int Int(string field)
    {
        var n = Long(field);
        if (n is < int.MinValue or > int.MaxValue)
            throw new WireException(WireProtocol.Errors.InvalidValue, $"field '{field}' is out of range");
        return (int)n;
    }

    public int? OptInt(string field) => Has(field) ? Int(field) : null;

    public bool OptBool(string field, bool fallback)
    {
        if (!Has(field)) return fallback;
        var v = _obj[field];
        if (v is not JsonValue val || (val.GetValueKind() != JsonValueKind.True && val.GetValueKind() != JsonValueKind.False))
            throw new WireException(WireProtocol.Errors.InvalidValue, $"field '{field}' must be a boolean");
        return val.GetValue<bool>();
    }

    public JsonObject Obj(string field)
    {
        if (!_obj.TryGetPropertyValue(field, out var v) || v is null)
            throw new WireException(WireProtocol.Errors.MissingField, $"missing required field '{field}'");
        if (v is not JsonObject o)
            throw new WireException(WireProtocol.Errors.InvalidValue, $"field '{field}' must be an object");
        return (JsonObject)o.DeepClone();
    }

    public JsonArray Arr(string field)
    {
        if (!_obj.TryGetPropertyValue(field, out var v) || v is null)
            throw new WireException(WireProtocol.Errors.MissingField, $"missing required field '{field}'");
        if (v is not JsonArray a)
            throw new WireException(WireProtocol.Errors.InvalidValue, $"field '{field}' must be an array");
        return (JsonArray)a.DeepClone();
    }

    /// <summary>The raw value node of <paramref name="field"/> (detached clone), for setting values.</summary>
    public JsonNode Node(string field)
    {
        if (!_obj.TryGetPropertyValue(field, out var v) || v is null)
            throw new WireException(WireProtocol.Errors.MissingField, $"missing required field '{field}'");
        return v.DeepClone();
    }
}
