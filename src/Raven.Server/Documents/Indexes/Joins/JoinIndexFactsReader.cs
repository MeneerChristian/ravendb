using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>Reads what the validation of a join index needs to know from an index of this node.</summary>
public static class JoinIndexFactsReader
{
    /// <summary>The facts of the index, null for null.</summary>
    public static JoinIndexFacts Read(Index index)
    {
        if (index == null)
            return null;

        var fields = new Dictionary<string, JoinIndexFieldFacts>(StringComparer.Ordinal);
        IndexDefinitionBaseServerSide definition = index.Definition;
        if (definition.IndexFields != null)
        {
            foreach (KeyValuePair<string, IndexField> pair in definition.IndexFields)
            {
                fields[pair.Key] = new JoinIndexFieldFacts
                {
                    Indexing = pair.Value.Indexing,
                    HasCustomAnalyzer = string.IsNullOrWhiteSpace(pair.Value.Analyzer) == false,
                    Analyzer = pair.Value.Analyzer
                };
            }
        }

        Dictionary<string, HashSet<CollectionName>> referenced = index.GetReferencedCollections();

        return new JoinIndexFacts
        {
            Name = index.Name,
            Collections = index.Collections.ToArray(),
            Type = index.Type,
            SourceType = index.SourceType,
            SearchEngine = index.SearchEngineType,
            MaxNumberOfOutputsPerDocument = index.MaxNumberOfOutputsPerDocument,
            HasReferencedCollections = referenced is { Count: > 0 },
            HasCompareExchange = definition.HasCompareExchange,
            HasDynamicFields = definition.HasDynamicFields,
            HasBoostedFields = index.HasBoostedFields,
            Fields = fields
        };
    }
}
