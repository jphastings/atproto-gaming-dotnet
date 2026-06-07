using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ByJP.AtprotoGaming.Core.Signing
{
    /// <summary>
    /// Minimal canonical DAG-CBOR encoder for the JsonNode subset records use:
    /// objects, arrays, strings, 64-bit integers, booleans, null, 64-bit floats.
    /// Canonical rules: definite lengths, map keys sorted by their CBOR-encoded
    /// bytes, shortest-form integers, 8-byte floats, no CBOR tags.
    /// </summary>
    internal static class DagCbor
    {
        public static byte[] Encode(JsonNode node)
        {
            using (var ms = new MemoryStream())
            {
                WriteNode(ms, node);
                return ms.ToArray();
            }
        }

        private static void WriteNode(Stream s, JsonNode? node)
        {
            switch (node)
            {
                case null:
                    s.WriteByte(0xf6); // null
                    return;
                case JsonObject obj:
                    WriteObject(s, obj);
                    return;
                case JsonArray arr:
                    WriteArray(s, arr);
                    return;
                case JsonValue v:
                    WriteValue(s, v);
                    return;
                default:
                    throw new NotSupportedException($"unsupported JsonNode type: {node.GetType().Name}");
            }
        }

        private static void WriteObject(Stream s, JsonObject obj)
        {
            var entries = new List<KeyValuePair<byte[], JsonNode?>>(obj.Count);
            foreach (var kvp in obj)
                entries.Add(new KeyValuePair<byte[], JsonNode?>(Encoding.UTF8.GetBytes(kvp.Key), kvp.Value));
            entries.Sort((a, b) => CompareBytes(a.Key, b.Key));

            WriteTypeAndLength(s, 5, (ulong)entries.Count);
            foreach (var e in entries)
            {
                WriteTypeAndLength(s, 3, (ulong)e.Key.Length);
                s.Write(e.Key, 0, e.Key.Length);
                WriteNode(s, e.Value);
            }
        }

        private static void WriteArray(Stream s, JsonArray arr)
        {
            WriteTypeAndLength(s, 4, (ulong)arr.Count);
            foreach (var item in arr) WriteNode(s, item);
        }

        private static void WriteValue(Stream s, JsonValue v)
        {
            var kind = v.GetValueKind();
            switch (kind)
            {
                case JsonValueKind.String:
                    var utf8 = Encoding.UTF8.GetBytes(v.GetValue<string>());
                    WriteTypeAndLength(s, 3, (ulong)utf8.Length);
                    s.Write(utf8, 0, utf8.Length);
                    return;
                case JsonValueKind.True:
                    s.WriteByte(0xf5);
                    return;
                case JsonValueKind.False:
                    s.WriteByte(0xf4);
                    return;
                case JsonValueKind.Null:
                    s.WriteByte(0xf6);
                    return;
                case JsonValueKind.Number:
                    WriteNumber(s, v);
                    return;
                default:
                    throw new NotSupportedException($"unsupported JSON value kind: {kind}");
            }
        }

        private static void WriteNumber(Stream s, JsonValue v)
        {
            // Round-trip through JSON text for a uniform representation: integer
            // when possible, otherwise a 64-bit float.
            var text = v.ToJsonString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i64))
            {
                if (i64 >= 0) WriteTypeAndLength(s, 0, (ulong)i64);
                else WriteTypeAndLength(s, 1, (ulong)(-(i64 + 1)));
                return;
            }
            if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u64))
            {
                WriteTypeAndLength(s, 0, u64);
                return;
            }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw new NotSupportedException($"cannot encode JSON number: {text}");
            s.WriteByte(0xfb);
            var bits = BitConverter.DoubleToInt64Bits(d);
            var be = new byte[8];
            for (var i = 0; i < 8; i++) be[i] = (byte)(bits >> (56 - 8 * i));
            s.Write(be, 0, 8);
        }

        private static void WriteTypeAndLength(Stream s, byte major, ulong length)
        {
            var prefix = (byte)(major << 5);
            if (length < 24) { s.WriteByte((byte)(prefix | (byte)length)); }
            else if (length <= byte.MaxValue) { s.WriteByte((byte)(prefix | 24)); s.WriteByte((byte)length); }
            else if (length <= ushort.MaxValue) { s.WriteByte((byte)(prefix | 25)); WriteBe(s, length, 2); }
            else if (length <= uint.MaxValue) { s.WriteByte((byte)(prefix | 26)); WriteBe(s, length, 4); }
            else { s.WriteByte((byte)(prefix | 27)); WriteBe(s, length, 8); }
        }

        private static void WriteBe(Stream s, ulong value, int bytes)
        {
            var buf = new byte[bytes];
            for (var i = 0; i < bytes; i++) buf[i] = (byte)(value >> (8 * (bytes - 1 - i)));
            s.Write(buf, 0, bytes);
        }

        // Sort by the key's CBOR-encoded bytes: shortest length first, then
        // bytewise lexicographic within the same length.
        private static int CompareBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return a.Length - b.Length;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            return 0;
        }
    }
}
