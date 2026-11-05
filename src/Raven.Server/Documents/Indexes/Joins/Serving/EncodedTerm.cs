namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>The term that a query on the joined index looks for when it asks for a text in a field: the term, no term when the index has no such field, or a split when the analyzer of the field does not make one term of the text.</summary>
public readonly record struct EncodedTerm(byte[] Term, bool AnalyzerSplitsValue = false)
{
    public static EncodedTerm Split => new(null, true);
}
