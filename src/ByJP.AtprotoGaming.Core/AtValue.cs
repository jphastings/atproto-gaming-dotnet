using System;
using System.Text.Json.Nodes;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// A value in the atproto data model — a string, integer, boolean, nested
    /// object, or array. Implicitly converts from the common CLR types so you can
    /// write <c>SetScore("kills", 42)</c> or <c>SetScore("seed", "AXK36RTM")</c>;
    /// pass a <see cref="JsonObject"/> / <see cref="JsonArray"/> for nested values.
    /// </summary>
    public readonly struct AtValue
    {
        private readonly JsonNode? _node;

        private AtValue(JsonNode? node) => _node = node;

        public static implicit operator AtValue(string value) => new AtValue(JsonValue.Create(value));
        public static implicit operator AtValue(int value) => new AtValue(JsonValue.Create(value));
        public static implicit operator AtValue(long value) => new AtValue(JsonValue.Create(value));
        public static implicit operator AtValue(bool value) => new AtValue(JsonValue.Create(value));

        /// <summary>Wraps an already-built node — use for objects, arrays, and nested structures.</summary>
        public static implicit operator AtValue(JsonNode value) => new AtValue(value);

        /// <summary>Returns a detached copy of the value as a <see cref="JsonNode"/>.</summary>
        internal JsonNode ToNode()
        {
            if (_node == null) throw new InvalidOperationException("AtValue has no value");
            return _node.DeepClone();
        }
    }
}
