using System;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>Coarse login lifecycle a consumer UI can render without polling.</summary>
    public enum AuthStatus
    {
        /// <summary>No handle/password in config.</summary>
        Unconfigured,

        /// <summary>Identity resolution or login in flight.</summary>
        Checking,

        /// <summary>Logged in; PDS, DID and handle are known.</summary>
        Ok,

        /// <summary>Credentials rejected or refresh failed; <see cref="AuthState.Error"/> is set.</summary>
        Failed,

        /// <summary>Network unreachable; DID known from cache; records queue.</summary>
        Offline,
    }

    /// <summary>
    /// Observable, thread-safe auth-state singleton (one per
    /// <see cref="AtprotoGamingClient"/>). The consumer's UI thread reads it while
    /// background tasks mutate it; every transition fires <see cref="Changed"/>.
    /// </summary>
    public sealed class AuthState
    {
        private readonly object _lock = new object();
        private AuthStatus _status = AuthStatus.Unconfigured;
        private string? _handle;
        private string? _did;
        private string? _pds;
        private string? _error;

        public AuthStatus Status { get { lock (_lock) return _status; } }
        public string? Handle { get { lock (_lock) return _handle; } }
        public string? Did { get { lock (_lock) return _did; } }
        public string? Pds { get { lock (_lock) return _pds; } }
        public string? Error { get { lock (_lock) return _error; } }

        /// <summary>Raised after every state transition. Handlers run on the mutating thread.</summary>
        public event Action? Changed;

        /// <summary>
        /// Sets the status and any supplied fields. Non-null <paramref name="handle"/>,
        /// <paramref name="did"/> and <paramref name="pds"/> overwrite; passing null
        /// leaves the prior value intact (so a transient <see cref="AuthStatus.Checking"/>
        /// doesn't wipe a cached DID). <paramref name="error"/> is always replaced —
        /// it's cleared on any non-failure transition.
        /// </summary>
        public void Set(AuthStatus status, string? handle = null, string? did = null,
            string? pds = null, string? error = null)
        {
            lock (_lock)
            {
                _status = status;
                _handle = handle ?? _handle;
                _did = did ?? _did;
                _pds = pds ?? _pds;
                _error = error;
            }
            Changed?.Invoke();
        }
    }
}
