using System;
using Corax.Querying;
using Corax.Querying.Matches;
using Raven.Client.Exceptions;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Sparrow;
using Sparrow.Json;
using Voron;
using Voron.Data.CompactTrees;

namespace Raven.Server.Documents.Indexes.Persistence.Corax;

// Compares a field of the row with a field of its joined document, the way WHERE p.Salary > c.MaxSalary does
internal sealed class JoinCompareFilter : IEntryFilter
{
    private enum Kind : byte
    {
        None,
        Number,
        Text
    }

    private struct Value
    {
        public Kind Kind;
        public bool Whole;
        public long Long;
        public double Double;
        public int TextLength;
    }

    private readonly struct Hop(JoinResult result, IndexSearcher from, long keyRootPage)
    {
        public readonly JoinResult Result = result;
        public readonly IndexSearcher From = from;
        public readonly long KeyRootPage = keyRootPage;
    }

    private sealed class Operand
    {
        public Hop[] Hops;
        public IndexSearcher Searcher;
        public long FieldRootPage;
        public string Field;
        public Page[] Pages;
        public CompactKey Key = new();
        public byte[] Text = new byte[64];
    }

    private readonly OperatorType _operator;
    private readonly Operand _left, _right;
    private readonly string _queryText;
    private readonly BlittableJsonReaderObject _parameters;

    private JoinCompareFilter(OperatorType op, Operand left, Operand right, string queryText, BlittableJsonReaderObject parameters)
    {
        _operator = op;
        _left = left;
        _right = right;
        _queryText = queryText;
        _parameters = parameters;
    }

    public static JoinCompareFilter Create(IndexSearcher outer, QueryMetadata metadata, BlittableJsonReaderObject parameters, Index outerIndex, JoinResult root, JoinCompare compare)
    {
        return new JoinCompareFilter(compare.Operator, CreateOperand(compare.Left), CreateOperand(compare.Right), metadata.QueryText, parameters);

        Operand CreateOperand(JoinCompareOperand source)
        {
            var operand = new Operand { Hops = new Hop[source.Path.Count], Pages = new Page[source.Path.Count + 1] };
            var searcher = outer;
            var result = root;
            for (var i = 0; i < source.Path.Count; i++)
            {
                if (i > 0)
                    result = Find(result, source.Path[i]);

                var keyField = result.OnFieldName ?? throw new InvalidQueryException($"The JOIN with '{JoinAliasConditions.DisplayAlias(new StringSegment(result.Alias))}' cannot be compared: it does not join on the id.", metadata.QueryText, parameters);
                operand.Hops[i] = new Hop(result, searcher, searcher.FieldCache.GetLookupRootPage(keyField));
                searcher = result.Reader.Searcher;
            }

            operand.Searcher = searcher;
            if (source.Path.Count == 0)
            {
                operand.Field = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, parameters, source.Field, metadata).Value;
                if (outerIndex.Definition.IndexFields.ContainsKey(operand.Field) == false && outerIndex.Definition.HasDynamicFields == false)
                    throw new InvalidQueryException($"The comparison uses the field '{operand.Field}' of the main document, but that field is not indexed in the index '{outerIndex.Name}'.", metadata.QueryText, parameters);
            }
            else
            {
                operand.Field = source.Field.FieldValue;
                if (result.InnerIndex.Definition.IndexFields.ContainsKey(operand.Field) == false && result.InnerIndex.Definition.HasDynamicFields == false)
                    throw new InvalidQueryException($"The comparison uses the field '{operand.Field}' of '{JoinAliasConditions.DisplayAlias(new StringSegment(source.Path[^1]))}', but that field is not indexed in the index '{result.InnerIndexName}'.", metadata.QueryText, parameters);
            }

            operand.FieldRootPage = searcher.FieldCache.GetLookupRootPage(operand.Field);
            return operand;
        }

        static JoinResult Find(JoinResult parent, string alias)
        {
            foreach (var nested in parent.Nested ?? [])
            {
                if (nested.Alias == alias)
                    return nested;
            }

            throw new InvalidOperationException($"The join '{alias}' was not found below '{parent.Alias}'.");
        }
    }

    public int Filter(Span<long> entries)
    {
        var kept = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            if (Satisfies(entries[i]) == false)
                continue;

            entries[kept++] = entries[i];
        }

        return kept;
    }

    private bool Satisfies(long entry)
    {
        var left = Read(_left, entry);
        if (left.Kind == Kind.None)
            return false;

        var right = Read(_right, entry);
        if (right.Kind != left.Kind)
            return false;

        int compare;
        if (left.Kind == Kind.Number)
            compare = left.Whole && right.Whole ? left.Long.CompareTo(right.Long) : left.Double.CompareTo(right.Double);
        else
            compare = _left.Text.AsSpan(0, left.TextLength).SequenceCompareTo(_right.Text.AsSpan(0, right.TextLength));

        return _operator switch
        {
            OperatorType.Equal => compare == 0,
            OperatorType.NotEqual => compare != 0,
            OperatorType.LessThan => compare < 0,
            OperatorType.LessThanEqual => compare <= 0,
            OperatorType.GreaterThan => compare > 0,
            OperatorType.GreaterThanEqual => compare >= 0,
            _ => throw new InvalidOperationException("Not a comparison: " + _operator)
        };
    }

    private Value Read(Operand operand, long entry)
    {
        var current = entry;
        for (var level = 0; level < operand.Hops.Length; level++)
        {
            ref readonly var hop = ref operand.Hops[level];
            var keyValue = ReadSingle(operand, hop.From, current, hop.KeyRootPage, level, hop.Result.OnFieldName);
            if (keyValue.Kind == Kind.None)
                return default;

            if (hop.Result.Keys.TryFind(operand.Text.AsSpan(0, keyValue.TextLength), out var index) == false)
                return default;

            current = hop.Result.Keys.WinnerEntryId(index);
        }

        return ReadSingle(operand, operand.Searcher, current, operand.FieldRootPage, operand.Hops.Length, operand.Field);
    }

    private Value ReadSingle(Operand operand, IndexSearcher searcher, long entry, long rootPage, int level, string field)
    {
        if (rootPage == -1)
            return default;

        var reader = searcher.GetEntryTermsReader(entry, ref operand.Pages[level], operand.Key);
        Value value = default;
        while (reader.FindNext(rootPage))
        {
            if (reader.IsNull || reader.IsNonExisting)
                continue;

            if (value.Kind != Kind.None)
            {
                var owner = level == 0 ? "the main document" : $"'{JoinAliasConditions.DisplayAlias(new StringSegment(operand.Hops[level - 1].Result.Alias))}'";
                throw new InvalidQueryException($"The field '{field}' of {owner} holds several values in one document (an array, or a field with a full-text analyzer that splits the text into several terms), " +
                                                "but a comparison between the fields of different documents needs a field with exactly one value. Compare a field that is indexed with one value per document.", _queryText, _parameters);
            }

            // the term is copied: reading the next term of the entry (to see that there is none) reuses the key
            var term = reader.Current.Decoded();
            if (term.Length > operand.Text.Length)
                operand.Text = new byte[Math.Max(term.Length, operand.Text.Length * 2)];

            term.CopyTo(operand.Text);
            value.TextLength = term.Length;

            if (reader.HasNumeric)
            {
                value.Kind = Kind.Number;
                value.Long = reader.CurrentLong;
                value.Double = reader.CurrentDouble;
                value.Whole = (double)reader.CurrentLong == reader.CurrentDouble;
            }
            else
            {
                value.Kind = Kind.Text;
            }
        }

        return value;
    }
}
