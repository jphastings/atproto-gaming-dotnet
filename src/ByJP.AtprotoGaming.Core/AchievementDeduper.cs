using System.Collections.Generic;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Session-scoped de-duplication for achievement-unlock writes: the same
    /// achievement id is a no-op after its first claim this session. Guards against
    /// the profile-load gotcha where a game re-registers its whole achievement set
    /// on start. In-memory by design — a restart re-claims, and consumers that also
    /// filter their own re-fires need nothing more.
    /// </summary>
    public sealed class AchievementDeduper
    {
        private readonly object _lock = new object();
        private readonly HashSet<string> _seen = new HashSet<string>();

        /// <summary>
        /// Returns true the first time an id is seen this session (publish it),
        /// false thereafter (skip).
        /// </summary>
        public bool TryClaim(string achievementId)
        {
            lock (_lock)
                return _seen.Add(achievementId);
        }
    }
}
