namespace Raven.Server.Documents.Queries;

// When a JOIN that needs only the first entries of its matches in the order of the ids tests the entries of the query one by one
internal static class JoinFirstEntriesSettings
{
    // the page is read from the first entries: past this many it is not chosen
    public const int DefaultMaximumTake = 10_000;

    public static volatile bool Enabled = true;

    public static volatile int MaximumTake = DefaultMaximumTake;
}
