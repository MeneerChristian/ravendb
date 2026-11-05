using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Raven.Client.Documents.Conventions;

namespace Raven.Client.Documents.Indexes;

/// <summary>
/// Builds a <see cref="JoinDefinition"/> from declarations made with expressions over the entries of the two indexes, the way
/// <see cref="IndexDefinitionBuilder{TDocument,TReduceResult}"/> builds an index definition from a map.
/// </summary>
/// <typeparam name="TOuter">The entry of the outer index: the type whose properties are the fields of the outer index.</typeparam>
/// <typeparam name="TInner">The entry of the inner index (the joined index): the type whose properties are the fields of the inner index.</typeparam>
public sealed class JoinDefinitionBuilder<TOuter, TInner>
{
    private readonly List<Declaration> _fields = new();
    private readonly List<Declaration> _groupings = new();

    private sealed class Declaration
    {
        public Declaration(string name, Expression<Func<TInner, object>> expression, JoinFieldKind kind, object value)
        {
            Name = name;
            Expression = expression;
            Kind = kind;
            Value = value;
        }

        public string Name { get; }

        public Expression<Func<TInner, object>> Expression { get; }

        public JoinFieldKind Kind { get; }

        public object Value { get; }
    }

    /// <summary>The name of the outer index, null until it is declared.</summary>
    public string OuterIndexName { get; set; }

    /// <summary>The name of the inner index, null until it is declared.</summary>
    public string InnerIndexName { get; set; }

    /// <summary>Set when the outer index is an auto-map index of this collection (see <see cref="JoinDefinition.OuterCollection"/>).</summary>
    public string OuterCollection { get; set; }

    /// <summary>Set when the inner index is an auto-map index of this collection (see <see cref="JoinDefinition.InnerCollection"/>).</summary>
    public string InnerCollection { get; set; }

    /// <summary>The field of the outer index that holds the id of the joined document: <c>x => x.ContextId</c>.</summary>
    public Expression<Func<TOuter, object>> OuterKey { get; set; }

    /// <summary>The key as the name of the field, when it is not declared with <see cref="OuterKey"/>.</summary>
    public string OuterKeyField { get; set; }

    /// <summary>The field of the inner index that the outer key is joined with (<c>ON o.A = i.B</c>): <c>x => x.Code</c>. Not set for a join on the id of the inner documents.</summary>
    public Expression<Func<TInner, object>> InnerKey { get; set; }

    /// <summary>The inner key as the name of the field, when it is not declared with <see cref="InnerKey"/>.</summary>
    public string InnerKeyField { get; set; }

    /// <summary>Keeps the lists of the entries of the joined index that have a value in the field, so that a query can filter by it.</summary>
    public void AddField(Expression<Func<TInner, object>> field, JoinFieldKind kind = JoinFieldKind.Exact)
    {
        _fields.Add(new Declaration(null, field ?? throw new ArgumentNullException(nameof(field)), kind, null));
    }

    /// <summary>Declares a field by name.</summary>
    public void AddField(string field, JoinFieldKind kind = JoinFieldKind.Exact)
    {
        _fields.Add(new Declaration(field, null, kind, null));
    }

    /// <summary>Keeps the entries of the joined index in which the field equals the value as a list of their own: a query that asks for that equality reads one list.</summary>
    public void AddGrouping(Expression<Func<TInner, object>> field, object value)
    {
        _groupings.Add(new Declaration(null, field ?? throw new ArgumentNullException(nameof(field)), JoinFieldKind.Exact, value));
    }

    /// <summary>Declares a grouping by the name of the field.</summary>
    public void AddGrouping(string field, object value)
    {
        _groupings.Add(new Declaration(field, null, JoinFieldKind.Exact, value));
    }

    /// <summary>The definition the declarations stand for. Throws <see cref="JoinValidationException"/> for an expression that cannot be translated and for the rules that need no server.</summary>
    public JoinDefinition ToJoinDefinition(DocumentConventions conventions = null)
    {
        conventions ??= new DocumentConventions();

        var definition = new JoinDefinition
        {
            OuterIndex = OuterIndexName,
            OuterCollection = OuterCollection,
            InnerIndex = InnerIndexName,
            InnerCollection = InnerCollection,
            OuterField = ResolveKey(conventions),
            InnerField = ResolveInnerKey(conventions)
        };

        foreach (Declaration declaration in _fields)
        {
            string name = declaration.Name;
            if (declaration.Expression != null)
            {
                JoinExpressionTranslator.Member member = JoinExpressionTranslator.ToMember(declaration.Expression, conventions, "a field");
                JoinExpressionTranslator.CheckField(member, declaration.Kind);
                name = member.Name;
            }

            definition.AddField(name, declaration.Kind);
        }

        foreach (Declaration declaration in _groupings)
        {
            string name = declaration.Name;
            if (declaration.Expression != null)
            {
                JoinExpressionTranslator.Member member = JoinExpressionTranslator.ToMember(declaration.Expression, conventions, "a grouping");
                JoinExpressionTranslator.CheckGrouping(member);
                name = member.Name;
            }

            definition.AddGrouping(name, JoinExpressionTranslator.ToTerm(declaration.Value, name));
        }

        List<JoinValidationError> errors = definition.ValidateShape();
        if (errors.Count > 0)
            throw new JoinValidationException(errors);

        return definition;
    }

    private string ResolveInnerKey(DocumentConventions conventions)
    {
        if (InnerKey != null && string.IsNullOrEmpty(InnerKeyField) == false)
            throw new JoinValidationException(JoinValidationCode.InvalidDefinition, "The inner join key is declared twice, with an expression and with a name. Declare it once.");

        if (InnerKey == null)
            return string.IsNullOrEmpty(InnerKeyField) ? null : InnerKeyField;

        JoinExpressionTranslator.Member key = JoinExpressionTranslator.ToMember(InnerKey, conventions, "the inner join key");
        JoinExpressionTranslator.CheckInnerKey(key);
        return key.Name;
    }

    private string ResolveKey(DocumentConventions conventions)
    {
        if (OuterKey != null && string.IsNullOrEmpty(OuterKeyField) == false)
            throw new JoinValidationException(JoinValidationCode.InvalidDefinition, "The join key is declared twice, with an expression and with a name. Declare it once.");

        if (OuterKey == null)
            return OuterKeyField;

        JoinExpressionTranslator.Member key = JoinExpressionTranslator.ToMember(OuterKey, conventions, "the join key");
        JoinExpressionTranslator.CheckKey(key, InnerKey != null || string.IsNullOrEmpty(InnerKeyField) == false);
        return key.Name;
    }
}
