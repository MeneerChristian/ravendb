using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Analyzers;
using Raven.Server.Documents.Indexes.Static;
using Voron.Data.CompactTrees;
using Voron.Impl;
using Constants = Raven.Client.Constants;

namespace Raven.Server.Documents.Queries;

// A JOIN on the id of the joined documents can keep the ids as the joined index encoded them, when the outer tree holds them unchanged
// and its dictionary has the same table
internal static class JoinEncodedKeys
{
    public static volatile bool Enabled = true;

    // The table of the dictionary of the index of the transaction, null when the index has no dictionary yet
    public static ulong? TableOf(LowLevelTransaction transaction)
    {
        var dictionaryId = CompactTree.GetDictionaryId(transaction);
        return dictionaryId > 0 ? PersistentDictionary.GetTableHash(transaction, dictionaryId) : null;
    }

    // The analyzer of the outer field returns the ids of the joined index as they are: both lowercase them the same way, so the analysis of a key that was lowercased changes nothing
    public static bool KeysReachTreeUnchanged(Index inner, Index outer, string outerField)
    {
        if (Enabled == false || outerField == null || inner.Type.IsMap() == false || IdsAreLowerCased(inner) == false)
            return false;

        var outerLowersLikeInner = LowerCaseGeneration(outer) == LowerCaseGeneration(inner);
        if (outerField == Constants.Documents.Indexing.Fields.DocumentIdFieldName)
            return outerLowersLikeInner && outer.Type.IsMap() && IdsAreLowerCased(outer);

        return outerLowersLikeInner && outer.Definition.IndexFields.TryGetValue(outerField, out var field) && field.Indexing == FieldIndexing.Key;
    }

    // The id of a document is analyzed by the default analyzer of its index
    private static bool IdsAreLowerCased(Index index)
    {
        if (index.Definition is MapIndexDefinition map && map.IndexDefinition.Fields.ContainsKey(Constants.Documents.Indexing.Fields.AllFields))
            return false;

        return index.Configuration.DefaultAnalyzerType.Value.Type == typeof(LowerCaseKeywordAnalyzer);
    }

    // Indexes of older versions lower the case of other characters
    private static int LowerCaseGeneration(Index index)
    {
        var version = index.Definition.Version;
        if (version < IndexDefinitionBaseServerSide.IndexVersion.CoraxUnicodeAnalyzers_62)
            return 0;

        return version < IndexDefinitionBaseServerSide.IndexVersion.CoraxUnicodeLengthAnalyzers_62 ? 1 : 2;
    }
}
