namespace Raven.Server.Documents.Queries;

// When a JOIN that orders by the joined index reads only the first entries of that order
internal static class JoinOrderedWalkSettings
{
    // the walk reads as many entries as the page needs: past this many it is not chosen
    public const int DefaultMaximumTake = 10_000;

    // a join side that matches fewer documents is ranked as a whole, which costs less than the walk
    public const int DefaultMinimumKeys = 8_192;

    public static volatile bool Enabled = true;

    public static volatile int MinimumKeys = DefaultMinimumKeys;

    public static volatile int MaximumTake = DefaultMaximumTake;
}
