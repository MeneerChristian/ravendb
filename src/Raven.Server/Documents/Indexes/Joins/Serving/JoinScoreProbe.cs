namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>The score that the sub-query of a JOIN gives to the documents it matches.</summary>
internal readonly struct JoinScoreProbe
{
    public JoinScoreProbe(bool hasMatches, bool isUniform, float score)
    {
        HasMatches = hasMatches;
        IsUniform = isUniform;
        Score = score;
    }

    /// <summary>The sub-query matches at least one document. When it does not, there is no score and the other members are false and 0.</summary>
    public bool HasMatches { get; }

    /// <summary>Every document that the sub-query matches has the same score.</summary>
    public bool IsUniform { get; }

    /// <summary>The score of the documents, which the server adds to the score of the main document (before the BOOST of the join).</summary>
    public float Score { get; }
}
