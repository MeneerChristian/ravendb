using System;
using System.Linq.Expressions;
using Raven.Client.Documents.Conventions;

namespace Raven.Client.Documents.Indexes;

/// <summary>
/// Declares a join index in code, the way an <see cref="AbstractIndexCreationTask"/> declares an index: in the constructor. The name of the index is the name of the class,
/// with '_' replaced by '/', and it is created like any other index: <c>new MyJoin().Execute(store)</c>, <see cref="IndexCreation"/> or <c>PutIndexesOperation</c>.
/// <code>
/// public class ConversationsWithContext : AbstractJoinCreationTask&lt;ConversationsByContext.Entry, ContextsSearch.Entry&gt;
/// {
///     public ConversationsWithContext()
///     {
///         UseOuterIndex&lt;ConversationsByContext&gt;();
///         UseInnerIndex&lt;ContextsSearch&gt;();
///         JoinOn(x => x.ContextId);
///         AddField(x => x.Country, JoinFieldKind.Exact);
///         AddField(x => x.Rank, JoinFieldKind.Long);
///         AddGrouping(x => x.IsActive, true);
///     }
/// }
/// </code>
/// </summary>
/// <typeparam name="TOuter">The entry of the outer index: its properties are the fields of the outer index (the type that an index declares as its result type).</typeparam>
/// <typeparam name="TInner">The entry of the inner index, the joined one.</typeparam>
public abstract class AbstractJoinCreationTask<TOuter, TInner> : AbstractIndexCreationTaskBase<IndexDefinition>
{
    private readonly JoinDefinitionBuilder<TOuter, TInner> _builder = new();

    /// <summary>The name of the outer index, null until it is declared.</summary>
    public string OuterIndexName => _builder.OuterIndexName;

    /// <summary>The name of the inner index, null until it is declared.</summary>
    public string InnerIndexName => _builder.InnerIndexName;

    /// <summary>The index of the query that holds the JOIN, by the type of the index: its name is the one the index gets when it is created.</summary>
    protected void UseOuterIndex<TIndex>() where TIndex : AbstractIndexCreationTask, new()
    {
        _builder.OuterIndexName = new TIndex().IndexName;
    }

    /// <summary>The index of the query that holds the JOIN, by name.</summary>
    protected void UseOuterIndex(string indexName)
    {
        _builder.OuterIndexName = indexName;
    }

    /// <summary>The joined index, by the type of the index.</summary>
    protected void UseInnerIndex<TIndex>() where TIndex : AbstractIndexCreationTask, new()
    {
        _builder.InnerIndexName = new TIndex().IndexName;
    }

    /// <summary>The joined index, by name.</summary>
    protected void UseInnerIndex(string indexName)
    {
        _builder.InnerIndexName = indexName;
    }

    /// <summary>The field of the outer index that holds the id of the joined document, the one that the query joins on: <c>JOIN (...) AS c ON m.ContextId = id(c)</c>.</summary>
    protected void JoinOn(Expression<Func<TOuter, object>> outerKey)
    {
        _builder.OuterKey = outerKey;
    }

    /// <summary>The join key by the name of the field.</summary>
    protected void JoinOn(string outerKeyField)
    {
        _builder.OuterKeyField = outerKeyField;
    }

    /// <summary>A join on a field of the inner index, <c>JOIN (...) AS c ON m.ContextCode = c.Code</c>: many outer entries and many inner entries may share a key.</summary>
    protected void JoinOn(Expression<Func<TOuter, object>> outerKey, Expression<Func<TInner, object>> innerKey)
    {
        _builder.OuterKey = outerKey;
        _builder.InnerKey = innerKey;
    }

    /// <summary>A join on a field of the inner index, by the names of the fields.</summary>
    protected void JoinOn(string outerKeyField, string innerKeyField)
    {
        _builder.OuterKeyField = outerKeyField;
        _builder.InnerKeyField = innerKeyField;
    }

    /// <summary>
    /// A join of the id of the outer document with a field of the inner index, <c>JOIN (...) AS c ON id(m) = c.B</c>: one outer entry per document, many inner entries may name it.
    /// The outer index needs no field for the key.
    /// </summary>
    protected void JoinOnId(Expression<Func<TInner, object>> innerKey)
    {
        _builder.OuterKeyField = JoinDefinition.OuterIdKey;
        _builder.InnerKey = innerKey;
    }

    /// <summary>A join of the id of the outer document with a field of the inner index, by the name of the field.</summary>
    protected void JoinOnId(string innerKeyField)
    {
        _builder.OuterKeyField = JoinDefinition.OuterIdKey;
        _builder.InnerKeyField = innerKeyField;
    }

    /// <inheritdoc cref="JoinDefinitionBuilder{TOuter,TInner}.AddField(Expression{Func{TInner,object}},JoinFieldKind)"/>
    protected void AddField(Expression<Func<TInner, object>> field, JoinFieldKind kind = JoinFieldKind.Exact)
    {
        _builder.AddField(field, kind);
    }

    /// <summary>Declares a field by name.</summary>
    protected void AddField(string field, JoinFieldKind kind = JoinFieldKind.Exact)
    {
        _builder.AddField(field, kind);
    }

    /// <inheritdoc cref="JoinDefinitionBuilder{TOuter,TInner}.AddGrouping(Expression{Func{TInner,object}},object)"/>
    protected void AddGrouping(Expression<Func<TInner, object>> field, object value)
    {
        _builder.AddGrouping(field, value);
    }

    /// <summary>Declares a grouping by the name of the field.</summary>
    protected void AddGrouping(string field, object value)
    {
        _builder.AddGrouping(field, value);
    }

    /// <summary>
    /// The definition of the join index. Throws <see cref="JoinValidationException"/> when the declaration is not valid.
    /// A join index has no maps: the section <see cref="IndexDefinition.Join"/> is its definition.
    /// </summary>
    public override IndexDefinition CreateIndexDefinition()
    {
        return new IndexDefinition
        {
            Name = IndexName,
            Join = _builder.ToJoinDefinition(Conventions ?? new DocumentConventions()),
            LockMode = LockMode,
            Priority = Priority,
            State = State,
            DeploymentMode = DeploymentMode
        };
    }
}
