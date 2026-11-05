using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Sparrow.Json;

namespace Raven.Server.Documents.Indexes.Joins.Auto;

/// <summary>
/// The join index that a JOIN of a query asks for. A JOIN is eligible exactly when a join index could answer it: the definition that is derived from the JOIN is checked with the rules of every join index
/// (<see cref="JoinIndexValidator"/>) and the sub-query is analyzed with the declared fields by the code that serves it (<see cref="JoinPredicateAnalyzer"/>).
/// </summary>
internal static class AutoJoinShape
{
    public const string Prefix = "Auto/";

    /// <summary>The definition of the join index for the JOIN, or false with the reason why a join index cannot answer it (the name of a not-served kind, or of the rule that was not met).</summary>
    public static bool TryDerive(JoinClause join, IndexQueryServerSide outerQuery, Index outer, Index inner, IReadOnlyList<JoinDefinition> widenWith, out JoinDefinition definition, out string reason)
    {
        definition = null;

        // what the server asks of a join before it offers it to a join index (JoinSources.TryServe)
        if (outerQuery.IsJoinSource || join.IsComposite || join.Compare != null || join.Query.Joins != null || join.ProjectsDocument)
        {
            reason = "Shape";
            return false;
        }

        if (join.On.Left is not (FieldExpression or MethodExpression) || (join.On.Left is MethodExpression && join.InnerField == null))
        {
            reason = "Shape";
            return false;
        }

        if (join.Query.From.Index == false || outer.Type.IsJoin() || inner.Type.IsJoin())
        {
            reason = "Shape";
            return false;
        }

        List<(QueryExpression Expression, OrderByFieldType FieldType, bool Ascending, NullsOrderingType NullsOrdering)> innerOrder;
        try
        {
            innerOrder = JoinSources.InnerOrderOf(join, outerQuery);
        }
        catch (Exception e) when (e is Raven.Client.Exceptions.InvalidQueryException)
        {
            reason = "Order";
            return false;
        }

        bool scoresMayMatter = JoinSources.ScoresMayMatter(outerQuery, outer);
        JoinIndexFacts innerFacts = JoinIndexFactsReader.Read(inner);

        var fields = new SortedDictionary<string, JoinFieldKind>(StringComparer.Ordinal);
        var collector = new Collector(join.Query.From.Alias?.Value, outerQuery.QueryParameters, innerFacts, fields);
        if (join.Query.Where != null && collector.Visit(join.Query.Where) == false)
        {
            reason = "Where";
            return false;
        }

        if (innerOrder != null)
        {
            // the first page of a query that is ordered by the join is answered in that order by an Exact field of the join index, when the sub-query has no where
            if (join.Query.Where != null || innerOrder.Count != 1 || JoinSources.OrderedPageMayApply(join, outerQuery) == false ||
                innerOrder[0].FieldType is not (OrderByFieldType.Implicit or OrderByFieldType.String))
            {
                reason = "Order";
                return false;
            }

            if (collector.Add(((FieldExpression)innerOrder[0].Expression).FieldValue, JoinFieldKind.Exact) == false)
            {
                reason = "Order";
                return false;
            }
        }

        string outerField = QueryBuilderHelper.ExtractIndexFieldName(outerQuery.Metadata.Query, outerQuery.QueryParameters, join.On.Left, outerQuery.Metadata).Value;
        JoinIndexFacts outerFacts = JoinIndexFactsReader.Read(outer);
        var validator = new JoinIndexValidator(name => string.Equals(name, outer.Name, StringComparison.OrdinalIgnoreCase) ? outerFacts
            : string.Equals(name, inner.Name, StringComparison.OrdinalIgnoreCase) ? innerFacts : null, isShardedDatabase: false);

        List<JoinValidationError> errors = null;
        if (widenWith is { Count: > 0 } && TryWiden(fields, widenWith, out SortedDictionary<string, JoinFieldKind> widened))
        {
            definition = Define(outer, inner, join, outerField, widened);
            errors = validator.Validate(definition);
        }

        if (errors == null || errors.Count > 0)
        {
            definition = Define(outer, inner, join, outerField, fields);
            errors = validator.Validate(definition);
        }

        if (errors.Count > 0)
        {
            reason = "Rule:" + errors[0].Code;
            definition = null;
            return false;
        }

        // the sub-query is analyzed by the code that serves it: no encoded term is needed to decide which lists answer it
        if (JoinPredicateAnalyzer.TryAnalyze(join, definition, outerQuery.QueryParameters, static (_, text) => new EncodedTerm(Encoding.UTF8.GetBytes(text)), scoresMayMatter, out JoinPredicate predicate, out string fallback) == false)
        {
            reason = fallback;
            definition = null;
            return false;
        }

        if (innerOrder != null && predicate.Kind != JoinPredicateKind.Any)
        {
            reason = JoinFallbacks.KindNotServed("OrderedWhere");
            definition = null;
            return false;
        }

        reason = null;
        return true;
    }

    private static JoinDefinition Define(Index outer, Index inner, JoinClause join, string outerField, SortedDictionary<string, JoinFieldKind> fields)
    {
        var definition = new JoinDefinition
        {
            OuterIndex = outer.Name,
            OuterField = outerField,
            InnerIndex = inner.Name,
            InnerField = join.InnerField
        };

        foreach ((string field, JoinFieldKind kind) in fields)
            definition.AddField(field, kind);

        return definition;
    }

    // The fields of the other auto join indexes of the join are added; false when one of them is declared with another kind than the query needs
    private static bool TryWiden(SortedDictionary<string, JoinFieldKind> fields, IReadOnlyList<JoinDefinition> others, out SortedDictionary<string, JoinFieldKind> widened)
    {
        widened = new SortedDictionary<string, JoinFieldKind>(fields, StringComparer.Ordinal);
        foreach (JoinDefinition other in others)
        {
            foreach (JoinField field in other.Fields)
            {
                if (widened.TryGetValue(field.Field, out JoinFieldKind kind))
                {
                    if (kind != field.Kind)
                        return false;

                    continue;
                }

                widened[field.Field] = field.Kind;
            }
        }

        return widened.Count > fields.Count;
    }

    /// <summary>What a JOIN needs to know before it is compared with the join indexes that exist: the order by of the sub-query and whether a score matters. False when no join index could answer it whatever it declares.</summary>
    public static bool TryPrepare(JoinClause join, IndexQueryServerSide outerQuery, Index outer,
        out List<(QueryExpression Expression, OrderByFieldType FieldType, bool Ascending, NullsOrderingType NullsOrdering)> innerOrder, out bool scoresMayMatter)
    {
        scoresMayMatter = false;
        try
        {
            innerOrder = JoinSources.InnerOrderOf(join, outerQuery);
        }
        catch (Exception e) when (e is Raven.Client.Exceptions.InvalidQueryException)
        {
            innerOrder = null;
            return false;
        }

        scoresMayMatter = JoinSources.ScoresMayMatter(outerQuery, outer);
        return true;
    }

    /// <summary>True when the join index declares what the join needs: the predicate of the sub-query is analyzed and, for an ordered join, ordered by an Exact field, with the code that serves the join. The state of the join index is not looked at.</summary>
    public static bool CouldServe(JoinClause join, IndexQueryServerSide outerQuery, JoinDefinition joinIndex,
        List<(QueryExpression Expression, OrderByFieldType FieldType, bool Ascending, NullsOrderingType NullsOrdering)> innerOrder, bool scoresMayMatter)
    {
        if (JoinPredicateAnalyzer.TryAnalyze(join, joinIndex, outerQuery.QueryParameters, static (_, text) => new EncodedTerm(Encoding.UTF8.GetBytes(text)), scoresMayMatter, out JoinPredicate predicate, out _) == false)
            return false;

        if (innerOrder == null)
            return true;

        if (innerOrder.Count != 1 || predicate.Kind != JoinPredicateKind.Any || innerOrder[0].FieldType is not (OrderByFieldType.Implicit or OrderByFieldType.String) ||
            innerOrder[0].Expression is not FieldExpression order)
            return false;

        foreach (JoinField declared in joinIndex.Fields)
        {
            if (declared.Kind == JoinFieldKind.Exact && string.Equals(declared.Field, order.FieldValue, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The name of the auto join index of a definition: it tells the join (the indexes and the keys). The first auto join index of a join has that name; one that is made because the existing ones lack fields of the query
    /// (<paramref name="withFields"/>) also has a hash of its fields, so every node derives the same name for the same definition.
    /// </summary>
    public static string NameOf(JoinDefinition joinIndex, bool withFields = false)
    {
        string outerKey = joinIndex.JoinsOnOuterId ? "IdOfOuter" : joinIndex.OuterField;
        string innerKey = joinIndex.InnerField ?? "Id";
        string name = $"{Prefix}{joinIndex.OuterIndex}/Join/{joinIndex.InnerIndex}/On{outerKey}To{innerKey}";
        if (withFields == false || joinIndex.Fields.Count == 0)
            return name;

        var fields = new StringBuilder();
        foreach (JoinField field in joinIndex.Fields.OrderBy(x => x.Field, StringComparer.Ordinal))
            fields.Append(field.Field).Append('\0').Append((int)field.Kind).Append('\n');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fields.ToString()));
        return $"{name}/With{Convert.ToHexString(hash, 0, 6)}";
    }

    /// <summary>Walks the where clause of the sub-query the way the analyzer does and collects the fields it filters by and the kind to declare for each.</summary>
    private sealed class Collector(string alias, BlittableJsonReaderObject parameters, JoinIndexFacts inner, SortedDictionary<string, JoinFieldKind> fields)
    {
        public bool Visit(QueryExpression expression)
        {
            switch (expression)
            {
                case BinaryExpression { Operator: OperatorType.And or OperatorType.Or } logical:
                    return Visit(logical.Left) && Visit(logical.Right);
                case BinaryExpression { Operator: OperatorType.Equal or OperatorType.NotEqual } equality:
                    return equality.Left is FieldExpression field && equality.Right is ValueExpression value && Atom(field, value);
                case NegatedExpression negated:
                    return Visit(negated.Expression);
                case BinaryExpression { IsRangeOperation: true } range:
                    return range.Left is FieldExpression rangeField && range.Right is ValueExpression rangeValue && Bound(rangeField, rangeValue);
                case MethodExpression { Arguments.Count: 1 } method when string.Equals(method.Name.Value, "not", StringComparison.OrdinalIgnoreCase):
                    return Visit(method.Arguments[0]);
                case MethodExpression method:
                    return Method(method);
                case InExpression { All: false } @in:
                    return @in.Source is FieldExpression inField && @in.Values.Count > 0 && @in.Values.All(x => x is ValueExpression value && Atom(inField, value));
                case BetweenExpression between:
                    return between.Source is FieldExpression betweenField && Bound(betweenField, between.Min) && Bound(betweenField, between.Max);
                default:
                    return false;
            }
        }

        private bool Atom(FieldExpression field, ValueExpression value)
        {
            if (JoinPredicateAnalyzer.TryTextOf(value, parameters, out _, out bool isLong) == false)
                return false;

            string name = FieldNameOf(field);
            if (isLong)
                return Add(name, JoinFieldKind.Long);

            // an analyzed field is declared as text, any other as exact terms
            return Add(name, inner.Fields.TryGetValue(name, out JoinIndexFieldFacts facts) && facts.IsAnalyzed ? JoinFieldKind.Text : JoinFieldKind.Exact);
        }

        private bool Bound(FieldExpression field, ValueExpression value)
        {
            if (JoinPredicateAnalyzer.TryTextOf(value, parameters, out _, out bool isLong) == false)
                return false;

            return Add(FieldNameOf(field), isLong ? JoinFieldKind.Long : JoinFieldKind.Exact);
        }

        private bool Method(MethodExpression method)
        {
            MethodType type = QueryMethod.GetMethodType(method.Name.Value, throwIfNoMatch: false);
            if ((type != MethodType.StartsWith && type != MethodType.Search) || method.Arguments.Count != 2 || method.Arguments[0] is not FieldExpression field || method.Arguments[1] is not ValueExpression)
                return false;

            return Add(FieldNameOf(field), type == MethodType.StartsWith ? JoinFieldKind.Exact : JoinFieldKind.Text);
        }

        public bool Add(string name, JoinFieldKind kind)
        {
            if (fields.TryGetValue(name, out JoinFieldKind existing))
                return existing == kind;

            fields[name] = kind;
            return true;
        }

        private string FieldNameOf(FieldExpression field)
        {
            if (field.Compound.Count > 1 && alias != null && field.Compound[0].Value == alias)
                return field.FieldValueWithoutAlias;

            return field.FieldValue;
        }
    }
}
