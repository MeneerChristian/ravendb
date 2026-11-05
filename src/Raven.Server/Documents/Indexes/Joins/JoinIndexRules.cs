using System;
using System.Collections.Generic;
using Raven.Client.Documents.Indexes;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>The rules that an index must meet to be the outer or the inner side of a join index, and that the declared fields must meet. Pure functions of <see cref="JoinIndexFacts"/>.</summary>
public static class JoinIndexRules
{
    /// <summary>The rules of a side: Corax, a map index of documents, one entry per document, no loaded documents, no boosted fields on the inner side.</summary>
    /// <param name="index">The facts of the index.</param>
    /// <param name="role">"outer" or "inner".</param>
    /// <param name="errors">Receives the errors.</param>
    public static void ValidateIndex(JoinIndexFacts index, string role, List<JoinValidationError> errors)
    {
        string prefix = $"The {role} index '{index.Name}'";

        if (index.SearchEngine != SearchEngineType.Corax)
            errors.Add(new JoinValidationError(JoinValidationCode.UnsupportedSearchEngine, $"{prefix} uses the {index.SearchEngine} search engine, a join index needs Corax."));

        if (index.Type is not (IndexType.Map or IndexType.AutoMap or IndexType.JavaScriptMap))
            errors.Add(new JoinValidationError(JoinValidationCode.UnsupportedIndexKind, $"{prefix} is a {index.Type} index, a join index needs a map index."));

        if (index.SourceType != IndexSourceType.Documents)
            errors.Add(new JoinValidationError(JoinValidationCode.UnsupportedSource, $"{prefix} indexes {index.SourceType}, a join index needs an index of documents."));

        if (index.HasCompareExchange)
            errors.Add(new JoinValidationError(JoinValidationCode.UnsupportedSource, $"{prefix} reads compare exchange values, so its entries are not a function of its documents."));

        if (index.HasReferencedCollections)
            errors.Add(new JoinValidationError(JoinValidationCode.UnsupportedSource, $"{prefix} loads other documents (LoadDocument), so its entries are not a function of its own documents."));

        // the documents of an equality sub-query have one score only while no field of the joined index is boosted
        if (index.HasBoostedFields && role == "inner")
            errors.Add(new JoinValidationError(JoinValidationCode.BoostedFields, $"{prefix} has boosted fields, so the documents that a sub-query matches do not all have the same score, which a join index relies on."));

        if (index.MaxNumberOfOutputsPerDocument > 1)
            errors.Add(new JoinValidationError(JoinValidationCode.MultipleOutputs, $"{prefix} creates up to {index.MaxNumberOfOutputsPerDocument} entries for one document, a join index needs one entry per document."));
    }

    /// <summary>The join key must be indexed the way a JOIN on id() compares ids: lowercased.</summary>
    public static void ValidateOuterField(string outerField, JoinIndexFacts outer, List<JoinValidationError> errors)
    {
        if (TryGetField(outer, "outer", outerField, errors, out JoinIndexFieldFacts field) == false)
            return;

        // a JOIN on id() compares the lowercased ids, so the field must be indexed the same way
        bool lowercased = field.Indexing is FieldIndexing.Default or FieldIndexing.Key && field.HasCustomAnalyzer == false;
        if (lowercased == false)
        {
            string how = field.HasCustomAnalyzer ? $"analyzed with '{field.Analyzer}'" : $"indexed {field.Indexing}";
            errors.Add(new JoinValidationError(JoinValidationCode.OuterFieldNotSupported,
                $"The field '{outerField}' of the outer index '{outer.Name}' is {how}. A JOIN on id() compares the lowercased ids of the documents, " +
                "so index the field with the default indexing (no Exact option, no custom analyzer) or with FieldIndexing.Key."));
        }
    }

    /// <summary>
    /// The keys of a join on a field are compared as the terms the two indexes hold: both fields are indexed the same way (default, exact or key), without a custom analyzer, so that no folding of the case
    /// or analysis separates what the join of the server would compare. Whether a field holds one value per entry is a fact of the data, which the build and the refresh check.
    /// </summary>
    public static void ValidateJoinKeys(string outerField, JoinIndexFacts outer, string innerField, JoinIndexFacts inner, List<JoinValidationError> errors)
    {
        if (string.Equals(outerField, JoinDefinition.OuterIdKey, StringComparison.Ordinal))
        {
            ValidateJoinKeyOfOuterId(inner, innerField, errors);
            return;
        }

        bool outerFound = TryGetField(outer, "outer", outerField, errors, out JoinIndexFieldFacts outerFacts);
        bool innerFound = TryGetField(inner, "inner", innerField, errors, out JoinIndexFieldFacts innerFacts);

        if (outerFound && IsKeyIndexing(outerFacts) == false)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.OuterFieldNotSupported,
                $"The field '{outerField}' of the outer index '{outer.Name}' is {Describe(outerFacts)}. The key of a join on a field is indexed with the default indexing, with FieldIndexing.Exact or with FieldIndexing.Key, without a custom analyzer."));
        }

        if (innerFound && IsKeyIndexing(innerFacts) == false)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.InnerFieldNotSupported,
                $"The field '{innerField}' of the inner index '{inner.Name}' is {Describe(innerFacts)}. The key of a join on a field is indexed with the default indexing, with FieldIndexing.Exact or with FieldIndexing.Key, without a custom analyzer."));
        }

        if (outerFound && innerFound && IsKeyIndexing(outerFacts) && IsKeyIndexing(innerFacts) && outerFacts.Indexing != innerFacts.Indexing)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.InnerFieldNotSupported,
                $"The key '{outerField}' of the outer index '{outer.Name}' is indexed {outerFacts.Indexing} and the key '{innerField}' of the inner index '{inner.Name}' is indexed {innerFacts.Indexing}. " +
                "A join index compares the terms of the two indexes as they are, so both keys have to be indexed the same way."));
        }
    }

    /// <summary>
    /// The outer key is the lowercased id of the document. The join of the server lowercases the keys of an inner field that keeps the case (Exact) and a join index compares the terms of the index as they are,
    /// so the inner key is indexed with the default indexing or with FieldIndexing.Key, which lowercase, and has no custom analyzer.
    /// </summary>
    private static void ValidateJoinKeyOfOuterId(JoinIndexFacts inner, string innerField, List<JoinValidationError> errors)
    {
        if (TryGetField(inner, "inner", innerField, errors, out JoinIndexFieldFacts facts) == false)
            return;

        if (IsKeyIndexing(facts) == false)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.InnerFieldNotSupported,
                $"The field '{innerField}' of the inner index '{inner.Name}' is {Describe(facts)}. The key of a join on a field is indexed with the default indexing, with FieldIndexing.Exact or with FieldIndexing.Key, without a custom analyzer."));
            return;
        }

        if (facts.Indexing == FieldIndexing.Exact)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.InnerFieldNotSupported,
                $"The field '{innerField}' of the inner index '{inner.Name}' is indexed Exact and keeps the case, but the key of a join on id() is the lowercased id of the outer document. " +
                "Index the field with the default indexing or with FieldIndexing.Key so that its terms are lowercase."));
        }
    }

    private static bool IsKeyIndexing(JoinIndexFieldFacts field) => field.Indexing is FieldIndexing.Default or FieldIndexing.Exact or FieldIndexing.Key && field.HasCustomAnalyzer == false;

    private static string Describe(JoinIndexFieldFacts field) => field.HasCustomAnalyzer ? $"analyzed with '{field.Analyzer}'" : $"indexed {field.Indexing}";

    /// <summary>The declared fields and groupings must be indexed fields of the inner index of a matching kind.</summary>
    public static void ValidateInnerFields(IReadOnlyList<JoinField> fields, IReadOnlyList<JoinGrouping> groupings, JoinIndexFacts inner, List<JoinValidationError> errors)
    {
        foreach (JoinField declared in fields)
        {
            if (TryGetField(inner, "inner", declared.Field, errors, out JoinIndexFieldFacts field) == false)
                continue;

            switch (declared.Kind)
            {
                case JoinFieldKind.Exact:
                case JoinFieldKind.Long:
                    if (field.IsSingleTerm == false)
                        errors.Add(KindMismatch(inner, declared, field, "has several terms per entry, declare it as Text"));
                    break;
                case JoinFieldKind.Text:
                    if (field.IsAnalyzed == false)
                        errors.Add(KindMismatch(inner, declared, field, "is not analyzed, declare it as Exact"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fields), declared.Kind, null);
            }
        }

        foreach (JoinGrouping grouping in groupings)
        {
            if (TryGetField(inner, "inner", grouping.Field, errors, out JoinIndexFieldFacts field) == false)
                continue;

            if (field.IsSingleTerm == false)
            {
                errors.Add(new JoinValidationError(JoinValidationCode.FieldKindMismatch,
                    $"The grouping on '{grouping.Field}' needs a field with one term per entry, but the field of the inner index '{inner.Name}' is analyzed."));
            }
        }
    }

    private static JoinValidationError KindMismatch(JoinIndexFacts index, JoinField declared, JoinIndexFieldFacts field, string reason)
    {
        string how = field.HasCustomAnalyzer ? $"analyzed with '{field.Analyzer}'" : $"indexed {field.Indexing}";
        return new JoinValidationError(JoinValidationCode.FieldKindMismatch,
            $"The field '{declared.Field}' is declared as {declared.Kind}, but in the inner index '{index.Name}' it is {how} and {reason}.");
    }

    private static bool TryGetField(JoinIndexFacts index, string role, string name, List<JoinValidationError> errors, out JoinIndexFieldFacts field)
    {
        if (index.Fields.TryGetValue(name, out field) == false)
        {
            string hint = index.HasDynamicFields ? " Fields that are created dynamically cannot be declared." : string.Empty;
            errors.Add(new JoinValidationError(JoinValidationCode.FieldNotIndexed, $"The field '{name}' is not a field of the {role} index '{index.Name}'.{hint}"));
            return false;
        }

        if (field.Indexing == FieldIndexing.No)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.FieldNotIndexed, $"The field '{name}' of the {role} index '{index.Name}' is not indexed."));
            return false;
        }

        return true;
    }
}
