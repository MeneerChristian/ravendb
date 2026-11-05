using System.Collections.Generic;
using Raven.Client.Exceptions;
using Raven.Server.Documents.Queries.AST;
using Sparrow;
using Sparrow.Json;

namespace Raven.Server.Documents.Queries
{
    // Moves the conditions on the fields of a JOIN alias out of the where clause of the main query
    internal sealed class JoinAliasConditions
    {
        private enum Kind
        {
            Outer,
            Pure,
            Mixed,
            Compare
        }

        private sealed class State
        {
            public readonly List<QueryExpression> Top = new();
            public readonly List<(string Alias, QueryExpression Predicate)> Virtuals = new();
            public readonly List<(string Alias, JoinCompare Compare)> Compares = new();
            public bool CompareUnderOr;
        }

        private readonly Query _query;
        private readonly BlittableJsonReaderObject _parameters;
        private readonly Dictionary<string, JoinClause> _byAlias = new();
        private readonly Dictionary<JoinClause, State> _states = new();
        private readonly List<JoinClause> _stateOrder = new();
        private readonly Dictionary<JoinClause, string> _rootOf = new();
        private readonly Dictionary<JoinClause, List<string>> _pathOf = new();
        private readonly HashSet<JoinClause> _filtering = new();
        private readonly List<QueryExpression> _kept = new();
        private readonly HashSet<string> _orderedAliases = new();
        private int _counter;

        private JoinAliasConditions(Query query, BlittableJsonReaderObject parameters)
        {
            _query = query;
            _parameters = parameters;
            Register(query.Joins, null, null);
            foreach (var order in query.OrderBy ?? [])
            {
                if (order.Expression is FieldExpression { Compound.Count: > 1 } field && _byAlias.ContainsKey(field.Compound[0].Value))
                    _orderedAliases.Add(field.Compound[0].Value);
            }
        }

        private void Register(List<JoinClause> joins, string root, List<string> parentPath)
        {
            if (joins == null)
                return;

            foreach (var join in joins)
            {
                var rootAlias = root ?? join.Alias.Value;
                _byAlias[join.Alias.Value] = join;
                _rootOf[join] = rootAlias;
                var path = parentPath == null ? [join.Alias.Value] : new List<string>(parentPath) { join.Alias.Value };
                _pathOf[join] = path;
                Register(join.Query.Joins, rootAlias, path);
            }
        }

        public static void Apply(Query query, BlittableJsonReaderObject parameters)
        {
            if (query.Where == null || query.Joins == null)
                return;

            new JoinAliasConditions(query, parameters).Run();
        }

        // The filter clause runs as a script over the documents of the main query
        public static void RejectFilterOnAliases(Query query, BlittableJsonReaderObject parameters)
        {
            if (query.Filter == null || query.Joins == null)
                return;

            var aliases = new HashSet<string>();
            CollectAliases(query.Joins, aliases);
            var found = FindAlias(query.Filter, aliases, idArgument: false);
            if (found != null)
                throw new InvalidQueryException($"The filter clause cannot use the fields of '{found}', the alias of a JOIN: a filter only sees the main document. Move the condition to the where clause.", query.QueryText, parameters);
        }

        private static void CollectAliases(List<JoinClause> joins, HashSet<string> into)
        {
            foreach (var join in joins)
            {
                into.Add(join.Alias.Value);
                if (join.Query.Joins != null)
                    CollectAliases(join.Query.Joins, into);
            }
        }

        private static string FindAlias(QueryExpression expression, HashSet<string> aliases, bool idArgument)
        {
            switch (expression)
            {
                case FieldExpression { Compound.Count: > 0 } field when (field.Compound.Count > 1 || idArgument) && aliases.Contains(field.Compound[0].Value):
                    return field.Compound[0].Value;
                case MethodExpression method:
                    {
                        var isId = QueryMethod.GetMethodType(method.Name.Value, throwIfNoMatch: false) == MethodType.Id;
                        foreach (var argument in method.Arguments)
                        {
                            if (FindAlias(argument, aliases, isId) is { } found)
                                return found;
                        }

                        return null;
                    }
                case BinaryExpression binary:
                    return FindAlias(binary.Left, aliases, false) ?? FindAlias(binary.Right, aliases, false);
                case NegatedExpression not:
                    return FindAlias(not.Expression, aliases, false);
                case InExpression @in:
                    {
                        if (FindAlias(@in.Source, aliases, false) is { } source)
                            return source;

                        foreach (var value in @in.Values)
                        {
                            if (FindAlias(value, aliases, false) is { } found)
                                return found;
                        }

                        return null;
                    }
                case BetweenExpression between:
                    return FindAlias(between.Source, aliases, false) ?? FindAlias(between.Min, aliases, false) ?? FindAlias(between.Max, aliases, false);
                default:
                    return null;
            }
        }

        // The alias of a virtual join as the user knows it
        internal static string DisplayAlias(StringSegment alias)
        {
            var value = alias.Value;
            var hash = value.IndexOf('#');
            return hash == -1 ? value : value.Substring(0, hash);
        }

        private void Run()
        {
            if (Classify(_query.Where).Kind == Kind.Outer)
                return;

            var items = new List<QueryExpression>();
            Flatten(_query.Where, OperatorType.And, items);

            foreach (var item in items)
            {
                var (kind, node) = Classify(item);
                switch (kind)
                {
                    case Kind.Outer:
                        _kept.Add(item);
                        break;
                    case Kind.Pure:
                        StateOf(node).Top.Add(Strip(item, node));
                        break;
                    case Kind.Compare:
                        TryCompare(item, out var compare, out _);
                        _kept.Add(Matches(AddCompare(node, compare, underOr: false)));
                        break;
                    default:
                        _kept.Add(Rewrite(item));
                        break;
                }
            }

            // a condition merged into the sub-query of a join makes that join an inner join
            foreach (var node in _stateOrder)
            {
                var state = _states[node];
                if (state.Top.Count == 0 || ForcedVirtual(node, state))
                    continue;

                foreach (var alias in _pathOf[node])
                    _filtering.Add(_byAlias[alias]);
            }

            _query.Joins = Rebuild(_query.Joins);
            if (_query.Joins is { Count: 0 })
                _query.Joins = null;

            _query.Where = Combine(_kept, OperatorType.And);
        }

        private State StateOf(JoinClause node)
        {
            if (_states.TryGetValue(node, out var state) == false)
            {
                _states[node] = state = new State();
                _stateOrder.Add(node);
            }

            return state;
        }

        private (Kind Kind, JoinClause Node) Classify(QueryExpression expression)
        {
            switch (expression)
            {
                case BinaryExpression { Operator: OperatorType.And or OperatorType.Or } logical:
                    {
                        var left = Classify(logical.Left);
                        var right = Classify(logical.Right);
                        if (left.Kind == Kind.Outer && right.Kind == Kind.Outer)
                            return (Kind.Outer, null);

                        if (left.Kind == Kind.Pure && right.Kind == Kind.Pure && left.Node == right.Node)
                            return left;

                        return (Kind.Mixed, null);
                    }
                case NegatedExpression not:
                    return (Classify(not.Expression).Kind == Kind.Outer ? Kind.Outer : Kind.Mixed, null);
                default:
                    {
                        if (TryCompare(expression, out _, out var compareRoot))
                            return (Kind.Compare, compareRoot);

                        JoinClause node = null;
                        var outer = false;
                        Scan(expression, ref node, ref outer, operand: false);
                        if (node == null)
                            return (Kind.Outer, null);

                        if (outer)
                            throw new InvalidQueryException($"The condition '{expression}' compares a field of the main document with a field of '{DisplayAlias(node.Alias)}', the alias of a JOIN. " +
                                                            "A condition of the where clause can only use the fields of one of them.", _query.QueryText, _parameters);

                        return (Kind.Pure, node);
                    }
            }
        }

        // Finds the alias the leaf refers to
        private void Scan(QueryExpression expression, ref JoinClause node, ref bool outer, bool operand)
        {
            switch (expression)
            {
                case FieldExpression field:
                    if (field.Compound.Count > 1 && _byAlias.TryGetValue(field.Compound[0].Value, out var found))
                        Note(found, ref node, expression);
                    else if (operand && field.Compound.Count > 0)
                        outer = true;
                    break;
                case MethodExpression method:
                    {
                        var type = QueryMethod.GetMethodType(method.Name.Value, throwIfNoMatch: false);
                        if (type == MethodType.Matches)
                            break;

                        if (type == MethodType.Id && method.Arguments is [FieldExpression { Compound.Count: 1 } idOf] && _byAlias.TryGetValue(idOf.Compound[0].Value, out var idNode))
                        {
                            Note(idNode, ref node, expression);
                            break;
                        }

                        for (var i = 0; i < method.Arguments.Count; i++)
                            Scan(method.Arguments[i], ref node, ref outer, operand: i == 0);
                        break;
                    }
                case BinaryExpression binary:
                    Scan(binary.Left, ref node, ref outer, operand: true);
                    Scan(binary.Right, ref node, ref outer, operand: true);
                    break;
                case InExpression @in:
                    Scan(@in.Source, ref node, ref outer, operand: true);
                    break;
                case BetweenExpression between:
                    Scan(between.Source, ref node, ref outer, operand: true);
                    break;
            }
        }

        private void Note(JoinClause found, ref JoinClause node, QueryExpression expression)
        {
            if (node != null && node != found)
                throw new InvalidQueryException($"The condition '{expression}' uses the fields of two JOIN aliases ('{DisplayAlias(node.Alias)}' and '{DisplayAlias(found.Alias)}'). " +
                                                "A condition of the where clause can only use the fields of one of them.", _query.QueryText, _parameters);

            node = found;
        }

        // the groups under an OR, a NOT or next to the conditions of other aliases become virtual joins

        private QueryExpression Rewrite(QueryExpression expression)
        {
            var (kind, node) = Classify(expression);
            switch (kind)
            {
                case Kind.Outer:
                    return expression;
                case Kind.Pure:
                    return Matches(AddVirtual(node, Strip(expression, node)));
                case Kind.Compare:
                    TryCompare(expression, out var compare, out _);
                    return Matches(AddCompare(node, compare, underOr: true));
            }

            switch (expression)
            {
                case NegatedExpression not:
                    {
                        var inner = Classify(not.Expression);
                        var operand = inner.Kind == Kind.Pure ? Matches(AddVirtual(inner.Node, Strip(not.Expression, inner.Node))) : Rewrite(not.Expression);
                        return new NegatedExpression(Paren(operand));
                    }
                case BinaryExpression { Operator: OperatorType.And or OperatorType.Or } logical:
                    {
                        var op = logical.Operator;
                        var items = new List<QueryExpression>();
                        Flatten(logical, op, items);

                        // the pure parts of one alias that are combined with the same operator are one group
                        var groups = new Dictionary<JoinClause, List<QueryExpression>>();
                        var rewritten = new List<QueryExpression>();
                        var slots = new List<JoinClause>();
                        foreach (var item in items)
                        {
                            var (itemKind, itemNode) = Classify(item);
                            if (itemKind == Kind.Pure)
                            {
                                if (groups.TryGetValue(itemNode, out var group) == false)
                                {
                                    groups[itemNode] = group = new List<QueryExpression>();
                                    rewritten.Add(null);
                                    slots.Add(itemNode);
                                }

                                group.Add(Strip(item, itemNode));
                            }
                            else
                            {
                                rewritten.Add(itemKind == Kind.Outer ? item : Rewrite(item));
                                slots.Add(null);
                            }
                        }

                        for (var i = 0; i < rewritten.Count; i++)
                        {
                            if (slots[i] != null)
                                rewritten[i] = Matches(AddVirtual(slots[i], Combine(groups[slots[i]], op)));
                        }

                        return Combine(rewritten, op);
                    }
                default:
                    return expression;
            }
        }

        private static bool IsComparison(OperatorType op) => op is OperatorType.Equal or OperatorType.NotEqual or OperatorType.LessThan or OperatorType.LessThanEqual
            or OperatorType.GreaterThan or OperatorType.GreaterThanEqual;

        // A comparison of two fields that are not fields of the same document
        private bool TryCompare(QueryExpression expression, out JoinCompare compare, out JoinClause root)
        {
            compare = null;
            root = null;
            if (expression is not BinaryExpression { Left: FieldExpression left, Right: FieldExpression right } binary || IsComparison(binary.Operator) == false)
                return false;

            var leftOperand = Operand(left, out var leftNode);
            var rightOperand = Operand(right, out var rightNode);
            if (leftNode == null && rightNode == null)
                return false;

            if (leftNode != null && rightNode != null && _rootOf[leftNode] != _rootOf[rightNode])
                throw new InvalidQueryException($"The condition '{expression}' compares fields of two JOIN aliases ('{DisplayAlias(leftNode.Alias)}' and '{DisplayAlias(rightNode.Alias)}') that are not joined through each other. " +
                                                "Only the fields of the main document and of the joined documents on one chain of joins (a JOIN inside the sub-query of another) can be compared.", _query.QueryText, _parameters);

            foreach (var node in new[] { leftNode, rightNode })
            {
                if (node == null)
                    continue;

                foreach (var alias in _pathOf[node])
                {
                    if (_byAlias[alias].InnerField != null)
                        throw new InvalidQueryException($"The condition '{expression}' compares a field of '{DisplayAlias(node.Alias)}', but the JOIN with '{alias}' matches on a field (ON ... = {alias}.{_byAlias[alias].InnerField}). " +
                                                        "Several documents can then be joined to one row, so it is ambiguous which one is compared. Compare the fields of documents that are joined on the id (ON ... = id(alias)).", _query.QueryText, _parameters);
                }
            }

            compare = new JoinCompare { Operator = binary.Operator, Left = leftOperand, Right = rightOperand };
            root = _byAlias[_rootOf[leftNode ?? rightNode]];
            return true;

            JoinCompareOperand Operand(FieldExpression field, out JoinClause node)
            {
                if (field.Compound.Count > 1 && _byAlias.TryGetValue(field.Compound[0].Value, out node))
                    return new JoinCompareOperand { Path = _pathOf[node], Field = new FieldExpression(field.Compound.GetRange(1, field.Compound.Count - 1)) { IsQuoted = field.IsQuoted } };

                node = null;
                return new JoinCompareOperand { Path = [], Field = field };
            }
        }

        private string AddCompare(JoinClause root, JoinCompare compare, bool underOr)
        {
            var name = $"{root.Alias.Value}#{++_counter}";
            var state = StateOf(root);
            state.Compares.Add((name, compare));
            state.CompareUnderOr |= underOr;
            return name;
        }

        private string AddVirtual(JoinClause node, QueryExpression predicate)
        {
            var name = $"{node.Alias.Value}#{++_counter}";
            StateOf(node).Virtuals.Add((name, predicate));
            return name;
        }

        private static MethodExpression Matches(string alias) =>
            new(new StringSegment("matches"), new List<QueryExpression> { new FieldExpression(new List<StringSegment> { new StringSegment(alias) }) });

        private bool OwnVirtual(JoinClause node)
        {
            if (_states.TryGetValue(node, out var state) == false)
                return false;

            return state.Virtuals.Count > 0 || state.CompareUnderOr || ForcedVirtual(node, state);
        }

        // the conditions next to a join that is mentioned by matches(), or that has virtual joins below it
        private bool ForcedVirtual(JoinClause node, State state) => state.Top.Count > 0 && (node.ReferencedInWhere || AnyBelow(node));

        private bool AnyBelow(JoinClause node)
        {
            foreach (var child in node.Query.Joins ?? [])
            {
                if (OwnVirtual(child) || AnyBelow(child))
                    return true;
            }

            return false;
        }

        private List<JoinClause> Rebuild(List<JoinClause> joins)
        {
            var result = new List<JoinClause>(joins.Count);
            foreach (var join in joins)
            {
                var current = join;
                if (join.Query.Joins != null)
                {
                    var children = Rebuild(join.Query.Joins);
                    if (SameJoins(children, join.Query.Joins) == false)
                    {
                        current = join.ShallowCopy();
                        current.Query = join.Query.ShallowCopy();
                        current.Query.Joins = children.Count == 0 ? null : children;
                    }
                }

                _states.TryGetValue(join, out var state);
                var merged = false;
                var drop = false;
                var forced = false;
                if (state != null)
                {
                    forced = ForcedVirtual(join, state);
                    if (state.Top.Count > 0 && forced == false)
                    {
                        merged = true;
                        current = WithWhere(current, Combine(state.Top, OperatorType.And));
                    }

                    drop = merged == false && join.ReferencedInWhere == false && (state.Virtuals.Count > 0 || state.CompareUnderOr || forced) && AnyBelow(join) == false && join.IsLeft == false;
                }

                // ORDER BY c.Field needs the rows of the original join
                if (drop && _orderedAliases.Contains(join.Alias.Value))
                {
                    drop = false;
                    current = current.ShallowCopy();
                    current.IsLeft = true;
                }

                if (current.IsLeft && _filtering.Contains(join))
                {
                    if (ReferenceEquals(current, join))
                        current = join.ShallowCopy();

                    current.IsLeft = false;
                }

                if (drop == false)
                    result.Add(current);

                if (state == null)
                    continue;

                if (forced)
                    _kept.Add(Matches(AddVirtualJoin(result, join, Combine(state.Top, OperatorType.And)).Alias.Value));

                foreach (var (alias, predicate) in state.Virtuals)
                    AddVirtualJoin(result, join, predicate, alias);

                foreach (var (alias, compare) in state.Compares)
                    AddVirtualJoin(result, join, null, alias).Compare = compare;
            }

            return result;
        }

        private static bool SameJoins(List<JoinClause> left, List<JoinClause> right)
        {
            if (left.Count != right.Count)
                return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (ReferenceEquals(left[i], right[i]) == false)
                    return false;
            }

            return true;
        }

        private JoinClause AddVirtualJoin(List<JoinClause> into, JoinClause node, QueryExpression predicate, string name = null)
        {
            name ??= $"{node.Alias.Value}#{++_counter}";
            var virtualJoin = WithWhere(node, predicate);
            virtualJoin.Alias = new StringSegment(name);
            virtualJoin.RootAlias = new StringSegment(_rootOf[node]);
            virtualJoin.ReferencedInWhere = true;
            virtualJoin.IsLeft = false;
            into.Add(virtualJoin);
            return virtualJoin;
        }

        private static JoinClause WithWhere(JoinClause node, QueryExpression predicate)
        {
            var copy = node.ShallowCopy();
            copy.Query = node.Query.ShallowCopy();
            if (predicate != null)
                copy.Query.Where = node.Query.Where == null ? predicate : Combine([node.Query.Where, predicate], OperatorType.And);
            return copy;
        }

        private static void Flatten(QueryExpression expression, OperatorType op, List<QueryExpression> into)
        {
            if (expression is BinaryExpression binary && binary.Operator == op)
            {
                Flatten(binary.Left, op, into);
                Flatten(binary.Right, op, into);
                return;
            }

            into.Add(expression);
        }

        private static QueryExpression Paren(QueryExpression expression)
        {
            if (expression is BinaryExpression { Operator: OperatorType.And or OperatorType.Or, Parenthesis: false } binary)
                return new BinaryExpression(binary.Left, binary.Right, binary.Operator) { Parenthesis = true };

            return expression;
        }

        private static QueryExpression Combine(List<QueryExpression> items, OperatorType op)
        {
            if (items.Count == 0)
                return null;

            if (op == OperatorType.And)
            {
                var positive = new List<QueryExpression>();
                var negative = new List<QueryExpression>();
                foreach (var item in items)
                    (item is NegatedExpression ? negative : positive).Add(item);

                if (positive.Count == 0 && negative.Count > 1)
                {
                    var inner = new List<QueryExpression>();
                    foreach (var not in negative)
                        inner.Add(((NegatedExpression)not).Expression);

                    return new NegatedExpression(Paren(Combine(inner, OperatorType.Or)));
                }

                items = [.. positive, .. negative];
            }

            var result = Paren(items[0]);
            for (var i = 1; i < items.Count; i++)
                result = new BinaryExpression(result, Paren(items[i]), op) { Parenthesis = i < items.Count - 1 };

            return result;
        }

        // The expression as the where clause of the sub-query of the alias
        private static QueryExpression Strip(QueryExpression expression, JoinClause node)
        {
            switch (expression)
            {
                case FieldExpression { Compound.Count: > 1 } field when field.Compound[0] == node.Alias:
                    return new FieldExpression(field.Compound.GetRange(1, field.Compound.Count - 1)) { IsQuoted = field.IsQuoted };
                case MethodExpression method:
                    {
                        if (method.Arguments is [FieldExpression { Compound.Count: 1 } idOf] && idOf.Compound[0] == node.Alias &&
                            QueryMethod.GetMethodType(method.Name.Value, throwIfNoMatch: false) == MethodType.Id)
                            return new MethodExpression(method.Name, new List<QueryExpression>()) { Alias = method.Alias };

                        var arguments = new List<QueryExpression>(method.Arguments.Count);
                        foreach (var argument in method.Arguments)
                            arguments.Add(Strip(argument, node));

                        return new MethodExpression(method.Name, arguments) { Alias = method.Alias };
                    }
                case BinaryExpression binary:
                    return new BinaryExpression(Strip(binary.Left, node), Strip(binary.Right, node), binary.Operator) { Parenthesis = binary.Parenthesis };
                case NegatedExpression not:
                    return new NegatedExpression(Strip(not.Expression, node));
                case InExpression @in:
                    {
                        var values = new List<QueryExpression>(@in.Values.Count);
                        foreach (var value in @in.Values)
                            values.Add(Strip(value, node));

                        return new InExpression(Strip(@in.Source, node), values, @in.All);
                    }
                case BetweenExpression between:
                    return new BetweenExpression(Strip(between.Source, node), between.Min, between.Max) { MinInclusive = between.MinInclusive, MaxInclusive = between.MaxInclusive };
                default:
                    return expression;
            }
        }
    }
}
