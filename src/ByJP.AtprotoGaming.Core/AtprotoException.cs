using System;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>Base type for failures talking to a PDS.</summary>
    public abstract class AtprotoException : Exception
    {
        protected AtprotoException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// The PDS rejected the request with a 4xx (bad payload, auth). Retrying
    /// offline won't help, so callers drop the record rather than queue it.
    /// </summary>
    public sealed class AtprotoPermanentException : AtprotoException
    {
        public int StatusCode { get; }

        /// <summary>The PDS's error body, surfaced verbatim (e.g. an XRPC <c>error</c>/<c>message</c>).</summary>
        public string? PdsError { get; }

        public AtprotoPermanentException(int statusCode, string? pdsError)
            : base($"PDS rejected request: HTTP {statusCode}{(string.IsNullOrEmpty(pdsError) ? "" : $" — {pdsError}")}")
        {
            StatusCode = statusCode;
            PdsError = pdsError;
        }
    }

    /// <summary>
    /// A 5xx, a network failure, or a timeout. The condition may clear, so
    /// callers queue the record for a later flush.
    /// </summary>
    public sealed class AtprotoTransientException : AtprotoException
    {
        /// <summary>HTTP status if there was a response; null for network/timeout failures.</summary>
        public int? StatusCode { get; }

        public AtprotoTransientException(string message, int? statusCode = null, Exception? inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
        }
    }
}
