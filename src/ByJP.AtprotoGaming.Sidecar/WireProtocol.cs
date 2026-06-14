namespace ByJP.AtprotoGaming.Sidecar;

/// <summary>Protocol-level constants and error codes — see <c>docs/wire-protocol.md</c>.</summary>
internal static class WireProtocol
{
    public const int Version = 1;
    public const int DefaultPort = 17872;

    public static class Errors
    {
        public const string UnsupportedProtocol = "unsupportedProtocol";
        public const string NotReady = "notReady";
        public const string UnknownCommand = "unknownCommand";
        public const string MissingField = "missingField";
        public const string InvalidValue = "invalidValue";
        public const string NoSession = "noSession";
        public const string Unavailable = "unavailable";
        public const string Internal = "internal";
    }
}

/// <summary>
/// A rejected command: carries the wire <see cref="Code"/> the emitter sees. Thrown
/// by request parsing/validation and turned into an <c>ok:false</c> reply.
/// </summary>
internal sealed class WireException : System.Exception
{
    public string Code { get; }

    public WireException(string code, string message) : base(message) => Code = code;
}
