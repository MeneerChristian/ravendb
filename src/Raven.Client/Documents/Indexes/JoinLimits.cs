namespace Raven.Client.Documents.Indexes;

/// <summary>The limits of a join index.</summary>
public static class JoinLimits
{
    /// <summary>The declared fields of one join index.</summary>
    public const int MaxFields = 8;

    /// <summary>The groupings of one join index.</summary>
    public const int MaxGroupings = 4;

    /// <summary>The length of the name of a join index: the limit of the name of a static index.</summary>
    public const int MaxNameLength = 206;

    /// <summary>The length of the value of a grouping in bytes.</summary>
    public const int MaxTermLength = 128;

    /// <summary>The terms that one entry of the joined index has in one field.</summary>
    public const int MaxTermsPerEntry = 32;

    /// <summary>The ids that the key field of one outer entry holds.</summary>
    public const int MaxKeysPerEntry = 256;
}
