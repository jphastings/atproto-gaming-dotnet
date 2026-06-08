namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// The platform values the <c>games.gamesgamesgamesgames.actor.stats</c>
    /// lexicon recognises for its <c>source</c> field. The consumer passes the one
    /// matching the running platform (the package can't auto-detect it without a
    /// platform SDK, which it deliberately doesn't depend on).
    /// </summary>
    public static class StatsSource
    {
        public const string Steam = "steam";
        public const string Gog = "gog";
        public const string Epic = "epic";
        public const string PlayStation = "playstation";
        public const string Xbox = "xbox";
        public const string Nintendo = "nintendo";
        public const string Itchio = "itchio";
        public const string Humble = "humble";
    }
}
