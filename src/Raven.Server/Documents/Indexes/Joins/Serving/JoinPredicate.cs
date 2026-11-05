using System;
using System.Text;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

internal enum JoinPredicateKind
{
    /// <summary>Every outer entry that has a joined entry: the sub-query has no condition.</summary>
    Any,

    /// <summary>The joined entry has this term in a declared field.</summary>
    Term,

    /// <summary>The joined entry is in a declared grouping.</summary>
    Grouping,

    And,

    /// <summary>The joined entry satisfies one of the operands.</summary>
    Or,

    /// <summary>The joined entry has a term of the field between the bounds.</summary>
    Range,

    /// <summary>The joined entry has a term of the field that starts with the bytes.</summary>
    Prefix,

    /// <summary>The outer entries that have a joined entry that does not satisfy the condition. Entries without a joined entry are in neither side.</summary>
    Not
}

/// <summary>A condition of the sub-query of a join, as far as the lists of a join index can answer it.</summary>
internal sealed class JoinPredicate
{
    private JoinPredicate(JoinPredicateKind kind)
    {
        Kind = kind;
    }

    public JoinPredicateKind Kind { get; }

    /// <summary>The index of the declared field (Term).</summary>
    public int Field { get; private init; }

    /// <summary>The term as the joined index stores it (Term).</summary>
    public byte[] Term { get; private init; }

    /// <summary>The index of the grouping (Grouping).</summary>
    public int Grouping { get; private init; }

    /// <summary>The field is a Long field: the bounds are numbers, otherwise they are terms.</summary>
    public bool IsLong { get; private init; }

    public bool HasLower { get; private init; }

    public bool HasUpper { get; private init; }

    public bool LowerInclusive { get; private init; }

    public bool UpperInclusive { get; private init; }

    public long LowerNumber { get; private init; }

    public long UpperNumber { get; private init; }

    public byte[] LowerTerm { get; private init; }

    public byte[] UpperTerm { get; private init; }

    /// <summary>The entries that are matched have different scores in the join of the server: the predicate can only be answered when no score is used.</summary>
    public bool ScoresDiffer { get; private init; }

    /// <summary>The operand of a Not, the left operand of an And.</summary>
    public JoinPredicate Left { get; private init; }

    public JoinPredicate Right { get; private init; }

    /// <summary>
    /// An AND or a NOT somewhere in the condition. The lists answer the other kinds exactly for an entry that names several documents (an OR of lists holds an entry when any document has the atom),
    /// but an AND or a NOT of lists is not what the join of the server returns when an entry names two documents, one that has one atom and one that has the other.
    /// </summary>
    public bool HasAndOrNot => Kind switch
    {
        JoinPredicateKind.And or JoinPredicateKind.Not => true,
        JoinPredicateKind.Or => Left.HasAndOrNot || Right.HasAndOrNot,
        _ => false
    };

    public static JoinPredicate Any() => new(JoinPredicateKind.Any);

    public static JoinPredicate OfTerm(int field, byte[] term, bool scoresDiffer = false) => new(JoinPredicateKind.Term) { Field = field, Term = term, ScoresDiffer = scoresDiffer };

    public static JoinPredicate OfGrouping(int grouping) => new(JoinPredicateKind.Grouping) { Grouping = grouping };

    public static JoinPredicate And(JoinPredicate left, JoinPredicate right) => new(JoinPredicateKind.And) { Left = left, Right = right, ScoresDiffer = left.ScoresDiffer || right.ScoresDiffer };

    public static JoinPredicate Or(JoinPredicate left, JoinPredicate right) => new(JoinPredicateKind.Or) { Left = left, Right = right, ScoresDiffer = true };

    public static JoinPredicate Not(JoinPredicate operand) => new(JoinPredicateKind.Not) { Left = operand, ScoresDiffer = operand.ScoresDiffer };

    public static JoinPredicate OfNumbers(int field, bool hasLower, long lower, bool lowerInclusive, bool hasUpper, long upper, bool upperInclusive) => new(JoinPredicateKind.Range)
    {
        Field = field, IsLong = true, HasLower = hasLower, LowerNumber = lower, LowerInclusive = lowerInclusive, HasUpper = hasUpper, UpperNumber = upper, UpperInclusive = upperInclusive, ScoresDiffer = true
    };

    public static JoinPredicate OfTerms(int field, byte[] lower, bool lowerInclusive, byte[] upper, bool upperInclusive) => new(JoinPredicateKind.Range)
    {
        Field = field, HasLower = lower != null, LowerTerm = lower, LowerInclusive = lowerInclusive, HasUpper = upper != null, UpperTerm = upper, UpperInclusive = upperInclusive, ScoresDiffer = true
    };

    public static JoinPredicate OfPrefix(int field, byte[] prefix) => new(JoinPredicateKind.Prefix) { Field = field, Term = prefix, ScoresDiffer = true };

    public override string ToString()
    {
        return Kind switch
        {
            JoinPredicateKind.Any => "any",
            JoinPredicateKind.Term => $"field {Field} = {Encoding.UTF8.GetString(Term)}",
            JoinPredicateKind.Grouping => $"grouping {Grouping}",
            JoinPredicateKind.And => $"({Left} and {Right})",
            JoinPredicateKind.Not => $"not {Left}",
            JoinPredicateKind.Or => $"({Left} or {Right})",
            JoinPredicateKind.Range => $"field {Field} in {(HasLower ? (LowerInclusive ? "[" : "(") : "(")}{(IsLong ? (HasLower ? LowerNumber.ToString() : "-inf") : (HasLower ? Encoding.UTF8.GetString(LowerTerm) : "-inf"))}, {(IsLong ? (HasUpper ? UpperNumber.ToString() : "inf") : (HasUpper ? Encoding.UTF8.GetString(UpperTerm) : "inf"))}{(HasUpper ? (UpperInclusive ? "]" : ")") : ")")}",
            JoinPredicateKind.Prefix => $"field {Field} starts with {Encoding.UTF8.GetString(Term)}",
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}
