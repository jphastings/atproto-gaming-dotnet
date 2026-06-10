namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// How <see cref="PlayUpdate.UpdateProgress"/> combines its value with the
    /// existing integer progress value, resolved against the real record at write
    /// time (so it's correct even after an offline queue).
    /// </summary>
    public enum ProgressOp
    {
        /// <summary>existing + value (absent counts as 0).</summary>
        Add,

        /// <summary>existing - value (absent counts as 0).</summary>
        Subtract,

        /// <summary>min(existing, value); just value if absent. Low-water mark (e.g. lowest HP) — never increases it, even if an older queued value flushes late.</summary>
        Min,

        /// <summary>max(existing, value); just value if absent. High-water mark (e.g. highest score) — never decreases it, even if an older queued value flushes late.</summary>
        Max,
    }
}
