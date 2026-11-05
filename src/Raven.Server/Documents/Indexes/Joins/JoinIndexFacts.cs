using System;
using System.Collections.Generic;
using Raven.Client.Documents.Indexes;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>What the validation of a definition needs to know about one index.</summary>
public sealed class JoinIndexFacts
{
    public string Name { get; set; }

    public string[] Collections { get; set; } = Array.Empty<string>();

    public IndexType Type { get; set; }

    public IndexSourceType SourceType { get; set; }

    public SearchEngineType SearchEngine { get; set; }

    public int MaxNumberOfOutputsPerDocument { get; set; }

    public bool HasReferencedCollections { get; set; }

    public bool HasCompareExchange { get; set; }

    public bool HasDynamicFields { get; set; }

    /// <summary>A field of the index is boosted: the documents of a sub-query that the join index answers would not all have the same score.</summary>
    public bool HasBoostedFields { get; set; }

    public Dictionary<string, JoinIndexFieldFacts> Fields { get; set; } = new(StringComparer.Ordinal);
}

public sealed class JoinIndexFieldFacts
{
    public FieldIndexing Indexing { get; set; }

    public bool HasCustomAnalyzer { get; set; }

    public string Analyzer { get; set; }

    /// <summary>One term per entry: the default indexing (lowercased), exact or key, without a custom analyzer.</summary>
    public bool IsSingleTerm => Indexing is FieldIndexing.Default or FieldIndexing.Exact or FieldIndexing.Key && HasCustomAnalyzer == false;

    public bool IsAnalyzed => Indexing == FieldIndexing.Search || HasCustomAnalyzer;
}
