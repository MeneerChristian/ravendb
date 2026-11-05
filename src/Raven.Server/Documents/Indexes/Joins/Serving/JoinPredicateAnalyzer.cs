using Raven.Client.Documents.Indexes;
using System;
using System.Globalization;
using System.Text;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using AstQuery = Raven.Server.Documents.Queries.AST.Query;
using Sparrow;
using Sparrow.Json;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// Decides whether the lists of a join index can answer the sub-query of a join and with which lists. The sub-query has to be an AND of equalities on declared fields and groupings,
/// possibly negated: every document it matches then has the same score, which is what a join index reproduces as a constant. Everything else is read by the server.
/// </summary>
internal sealed class JoinPredicateAnalyzer
{
    private readonly JoinDefinition _definition;
    private readonly BlittableJsonReaderObject _parameters;
    private readonly Func<string, string, EncodedTerm> _encode;
    private readonly string _subQueryAlias;
    private readonly bool _scoresMayMatter;
    private string _fallback;

    private JoinPredicateAnalyzer(JoinDefinition definition, BlittableJsonReaderObject parameters, Func<string, string, EncodedTerm> encode, string subQueryAlias, bool scoresMayMatter)
    {
        _scoresMayMatter = scoresMayMatter;
        _definition = definition;
        _parameters = parameters;
        _encode = encode;
        _subQueryAlias = subQueryAlias;
    }

    /// <param name="encode">The term of a text value in a field of the joined index, as the index analyzes it.</param>
    /// <param name="scoresMayMatter">False when nothing uses the score of the join: then ranges, prefixes, text and OR are answered too, their documents have different scores in the join of the server.</param>
    /// <returns>True with the predicate, or false with the reason (a <see cref="JoinFallbacks"/> name).</returns>
    public static bool TryAnalyze(JoinClause join, JoinDefinition definition, BlittableJsonReaderObject parameters, Func<string, string, EncodedTerm> encode, bool scoresMayMatter,
        out JoinPredicate predicate, out string fallback)
    {
        predicate = null;
        AstQuery query = join.Query;

        string unsupported = null;
        if (query.Filter != null)
            unsupported = "SubQueryFilter";
        else if (query.IsDistinct)
            unsupported = "SubQueryDistinct";
        else if (query.Load is { Count: > 0 } || query.Include is { Count: > 0 })
            unsupported = "SubQueryLoad";

        if (unsupported != null)
        {
            fallback = JoinFallbacks.KindNotServed(unsupported);
            return false;
        }

        var analyzer = new JoinPredicateAnalyzer(definition, parameters, encode, query.From.Alias?.Value, scoresMayMatter);
        if (query.Where == null)
        {
            predicate = JoinPredicate.Any();
            fallback = null;
            return true;
        }

        predicate = analyzer.Visit(query.Where);
        fallback = predicate == null ? analyzer._fallback ?? JoinFallbacks.KindNotServed("Where") : null;
        return predicate != null;
    }

    private const string AnalyzerSplitsValue = "AnalyzerSplitsValue";

    private JoinPredicate Visit(QueryExpression expression)
    {
        switch (expression)
        {
            case BinaryExpression { Operator: OperatorType.And } and:
            {
                JoinPredicate left = Visit(and.Left);
                JoinPredicate right = left == null ? null : Visit(and.Right);
                return right == null ? null : JoinPredicate.And(left, right);
            }
            case BinaryExpression { Operator: OperatorType.Equal } equal:
                return Atom(equal);
            case BinaryExpression { Operator: OperatorType.NotEqual } notEqual:
            {
                JoinPredicate atom = Atom(notEqual);
                return atom == null ? null : JoinPredicate.Not(atom);
            }
            case NegatedExpression negated:
            {
                JoinPredicate operand = Visit(negated.Expression);
                return operand == null ? null : JoinPredicate.Not(operand);
            }
            case BinaryExpression { Operator: OperatorType.Or } or:
            {
                if (_scoresMayMatter)
                    return Fail("Or");

                JoinPredicate left = Visit(or.Left);
                JoinPredicate right = left == null ? null : Visit(or.Right);
                return right == null ? null : JoinPredicate.Or(left, right);
            }
            case BinaryExpression { IsRangeOperation: true } range:
                return Range(range);
            case MethodExpression { Arguments.Count: 1 } method when string.Equals(method.Name.Value, "not", StringComparison.OrdinalIgnoreCase):
            {
                JoinPredicate operand = Visit(method.Arguments[0]);
                return operand == null ? null : JoinPredicate.Not(operand);
            }
            case MethodExpression method:
                return Method(method);
            case InExpression @in:
                return In(@in);
            case BetweenExpression between:
                return Between(between);
            default:
                return Fail(expression?.GetType().Name ?? "Unknown");
        }
    }

    private int DeclaredField(string fieldName, bool? isLong)
    {
        for (int f = 0; f < _definition.Fields.Count; f++)
        {
            JoinField declared = _definition.Fields[f];
            if (string.Equals(declared.Field, fieldName, StringComparison.Ordinal) == false)
                continue;

            if (declared.Kind == JoinFieldKind.Text)
                return -1;

            if (isLong == null || (declared.Kind == JoinFieldKind.Long) == isLong)
                return f;
        }

        return -1;
    }

    private JoinPredicate Range(BinaryExpression range)
    {
        if (_scoresMayMatter)
            return Fail("Range");

        if (range.Left is not FieldExpression field || range.Right is not ValueExpression value)
            return Fail("Atom");

        bool greater = range.Operator is OperatorType.GreaterThan or OperatorType.GreaterThanEqual;
        bool inclusive = range.Operator is OperatorType.GreaterThanEqual or OperatorType.LessThanEqual;
        return Bound(FieldNameOf(field), value, greater ? value : null, greater ? inclusive : true, greater ? null : value, greater ? true : inclusive);
    }

    private JoinPredicate Between(BetweenExpression between)
    {
        if (_scoresMayMatter)
            return Fail("Between");

        if (between.Source is not FieldExpression field)
            return Fail("Atom");

        return Bound(FieldNameOf(field), between.Min, between.Min, between.MinInclusive, between.Max, between.MaxInclusive);
    }

    private JoinPredicate Bound(string fieldName, ValueExpression anyValue, ValueExpression lower, bool lowerInclusive, ValueExpression upper, bool upperInclusive)
    {
        if (TryTextOf(anyValue, out _, out bool isLong) == false)
            return Fail("ValueType");

        int f = DeclaredField(fieldName, isLong);
        if (f < 0)
            return Fail(DeclaredField(fieldName, null) >= 0 ? "ValueType" : "UndeclaredField:" + fieldName);

        if (isLong)
        {
            long lowNumber = 0, highNumber = 0;
            if ((lower != null && TryNumber(lower, out lowNumber) == false) || (upper != null && TryNumber(upper, out highNumber) == false))
                return Fail("ValueType");

            return JoinPredicate.OfNumbers(f, lower != null, lowNumber, lowerInclusive, upper != null, highNumber, upperInclusive);
        }

        byte[] lowTerm = null, highTerm = null;
        if (lower != null)
        {
            if (TryTextOf(lower, out string text, out bool lowerIsLong) == false || lowerIsLong || (lowTerm = Encode(fieldName, text)) == null)
                return Fail("ValueType");
        }

        if (upper != null)
        {
            if (TryTextOf(upper, out string text, out bool upperIsLong) == false || upperIsLong || (highTerm = Encode(fieldName, text)) == null)
                return Fail("ValueType");
        }

        return JoinPredicate.OfTerms(f, lowTerm, lowerInclusive, highTerm, upperInclusive);
    }

    private bool TryNumber(ValueExpression value, out long number)
    {
        number = 0;
        if (TryTextOf(value, out string text, out bool isLong) == false || isLong == false)
            return false;

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private JoinPredicate In(InExpression @in)
    {
        if (_scoresMayMatter || @in.All)
            return Fail("In");

        if (@in.Source is not FieldExpression field)
            return Fail("Atom");

        JoinPredicate result = null;
        foreach (QueryExpression item in @in.Values)
        {
            if (item is not ValueExpression value)
                return Fail("Atom");

            JoinPredicate atom = Atom(field, value);
            if (atom == null)
                return null;

            result = result == null ? atom : JoinPredicate.Or(result, atom);
        }

        return result ?? Fail("In");
    }

    private JoinPredicate Method(MethodExpression method)
    {
        MethodType type = QueryMethod.GetMethodType(method.Name.Value, throwIfNoMatch: false);
        if ((type != MethodType.StartsWith && type != MethodType.Search) || method.Arguments.Count != 2 || method.Arguments[0] is not FieldExpression field || method.Arguments[1] is not ValueExpression value)
            return Fail("Method:" + method.Name.Value);

        if (_scoresMayMatter)
            return Fail("Method:" + method.Name.Value);

        string fieldName = FieldNameOf(field);
        if (TryTextOf(value, out string text, out bool isLong) == false || isLong)
            return Fail("ValueType");

        for (int f = 0; f < _definition.Fields.Count; f++)
        {
            JoinField declared = _definition.Fields[f];
            if (string.Equals(declared.Field, fieldName, StringComparison.Ordinal) == false)
                continue;

            if (type == MethodType.StartsWith)
            {
                if (declared.Kind != JoinFieldKind.Exact)
                    return Fail("Method:" + method.Name.Value);

                byte[] prefix = Encode(fieldName, text);
                return prefix == null ? Fail("FieldNotIndexed:" + fieldName) : JoinPredicate.OfPrefix(f, prefix);
            }

            if (declared.Kind != JoinFieldKind.Text || IsOneWord(text) == false)
                return Fail("Method:" + method.Name.Value);

            byte[] term = Encode(fieldName, text);
            return term == null ? Fail("FieldNotIndexed:" + fieldName) : JoinPredicate.OfTerm(f, term, scoresDiffer: true);
        }

        return Fail("UndeclaredField:" + fieldName);
    }

    private static bool IsOneWord(string text)
    {
        if (text.Length == 0)
            return false;

        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) == false)
                return false;
        }

        return true;
    }

    // The term of the text, null when the index has no such field or when its analyzer does not make one term of the text: then the join is read as usual
    private byte[] Encode(string fieldName, string text)
    {
        EncodedTerm encoded = _encode(fieldName, text);
        if (encoded.AnalyzerSplitsValue)
            Fail(AnalyzerSplitsValue);

        return encoded.Term;
    }

    private JoinPredicate Fail(string detail)
    {
        _fallback ??= JoinFallbacks.KindNotServed(detail);
        return null;
    }

    private JoinPredicate Atom(BinaryExpression equality)
    {
        if (equality.Left is not FieldExpression field || equality.Right is not ValueExpression value)
            return Fail("Atom");

        return Atom(field, value);
    }

    private JoinPredicate Atom(FieldExpression field, ValueExpression value)
    {
        string fieldName = FieldNameOf(field);
        if (TryTextOf(value, out string text, out bool isLong) == false)
            return Fail("ValueType");

        byte[] encoded = null;
        if (IsDeclared(fieldName) && Encode(fieldName, text) == null)
            return Fail("FieldNotIndexed:" + fieldName);

        for (int g = 0; g < _definition.Groupings.Count; g++)
        {
            JoinGrouping grouping = _definition.Groupings[g];
            if (string.Equals(grouping.Field, fieldName, StringComparison.Ordinal) == false)
                continue;

            encoded ??= Encode(fieldName, text);
            if (encoded.AsSpan().SequenceEqual(Encode(fieldName, grouping.Value)))
                return JoinPredicate.OfGrouping(g);
        }

        for (int f = 0; f < _definition.Fields.Count; f++)
        {
            JoinField declared = _definition.Fields[f];
            if (string.Equals(declared.Field, fieldName, StringComparison.Ordinal) == false)
                continue;

            switch (declared.Kind)
            {
                case JoinFieldKind.Text:
                    if (_scoresMayMatter || isLong || IsOneWord(text) == false)
                        return Fail("Text");

                    encoded ??= Encode(fieldName, text);
                    return encoded == null ? Fail("FieldNotIndexed:" + fieldName) : JoinPredicate.OfTerm(f, encoded, scoresDiffer: true);
                case JoinFieldKind.Long:
                    return isLong ? JoinPredicate.OfTerm(f, Encoding.UTF8.GetBytes(text)) : Fail("ValueType");
                default:
                    if (isLong)
                        return Fail("ValueType");

                    encoded ??= Encode(fieldName, text);
                    return encoded == null ? Fail("FieldNotIndexed:" + fieldName) : JoinPredicate.OfTerm(f, encoded);
            }
        }

        return Fail("UndeclaredField:" + fieldName);
    }

    private bool IsDeclared(string field)
    {
        foreach (JoinGrouping grouping in _definition.Groupings)
        {
            if (string.Equals(grouping.Field, field, StringComparison.Ordinal))
                return true;
        }

        foreach (JoinField declared in _definition.Fields)
        {
            if (string.Equals(declared.Field, field, StringComparison.Ordinal) && declared.Kind != JoinFieldKind.Long)
                return true;
        }

        return false;
    }

    private string FieldNameOf(FieldExpression field)
    {
        if (field.Compound.Count > 1 && _subQueryAlias != null && field.Compound[0].Value == _subQueryAlias)
            return field.FieldValueWithoutAlias;

        return field.FieldValue;
    }

    private bool TryTextOf(ValueExpression value, out string text, out bool isLong)
    {
        isLong = false;
        switch (value.Value)
        {
            case ValueTokenType.String:
                text = value.Token.Value;
                return true;
            case ValueTokenType.Long:
                isLong = true;
                text = ((long)value.GetValue(_parameters)).ToString(CultureInfo.InvariantCulture);
                return true;
            case ValueTokenType.True:
                text = "true";
                return true;
            case ValueTokenType.False:
                text = "false";
                return true;
            case ValueTokenType.Parameter:
                if (_parameters == null || _parameters.TryGetMember(value.Token.Value, out object parameter) == false)
                    break;

                switch (parameter)
                {
                    case LazyStringValue lazy:
                        text = lazy.ToString();
                        return true;
                    case LazyCompressedStringValue compressed:
                        text = compressed.ToString();
                        return true;
                    case string s:
                        text = s;
                        return true;
                    case long l:
                        isLong = true;
                        text = l.ToString(CultureInfo.InvariantCulture);
                        return true;
                    case bool b:
                        text = b ? "true" : "false";
                        return true;
                }

                break;
        }

        text = null;
        return false;
    }
}
