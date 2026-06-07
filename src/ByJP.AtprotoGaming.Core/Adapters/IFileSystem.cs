namespace ByJP.AtprotoGaming.Core.Adapters
{
    /// <summary>
    /// Where the package keeps its two on-disk artefacts: <c>config.json</c> and
    /// the outbox tree. Typically the directory next to the consumer's plugin DLL.
    /// Use <see cref="FileSystem"/> for the common cases.
    /// </summary>
    public interface IFileSystem
    {
        /// <summary>Directory that holds <c>config.json</c>.</summary>
        string ConfigDirectory { get; }

        /// <summary>Root of the per-DID outbox tree (<c>&lt;root&gt;/&lt;did&gt;/&lt;collection&gt;/&lt;rkey&gt;.json</c>).</summary>
        string OutboxRoot { get; }
    }
}
