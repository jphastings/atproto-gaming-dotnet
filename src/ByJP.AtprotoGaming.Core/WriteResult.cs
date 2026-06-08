namespace ByJP.AtprotoGaming.Core
{
    /// <summary>The PDS's response to a create/put: the record's AT-URI and its new CID.</summary>
    public readonly struct WriteResult
    {
        public string Uri { get; }

        /// <summary>The record's content CID after the write — pass it as the next <c>swapRecord</c> for optimistic locking.</summary>
        public string Cid { get; }

        public WriteResult(string uri, string cid)
        {
            Uri = uri;
            Cid = cid;
        }
    }
}
