namespace Corax.Querying.Matches.Meta;

/// <summary>
/// A match whose entries all have the same score, which is what <see cref="IQueryMatch.Score"/> adds to every entry it is asked about.
/// A sort by score may then take the first entries of <see cref="IQueryMatch.Fill"/> as the best ones, because a score sort keeps entries of equal scores in the order of their ids.
/// </summary>
/// <remarks>
/// Implementations promise that <see cref="IQueryMatch.Count"/> is exact (its <see cref="IQueryMatch.Confidence"/> is <see cref="QueryCountConfidence.High"/> when it is),
/// that <see cref="IQueryMatch.Fill"/> returns the entries ascending across calls and without duplicates, and that <see cref="IQueryMatch.Score"/> can be called after
/// <see cref="IQueryMatch.Fill"/> for any of the entries returned and gives the same value as for all of them.
/// </remarks>
public interface IConstantScoreMatch : IQueryMatch
{
}
