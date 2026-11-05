using Raven.Client.Documents.Indexes;
using System;
using System.Buffers.Text;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Sparrow.Threading;
using Voron;
using Constants = Raven.Client.Constants;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>What the refresher needs to know about a definition to read the indexes: the names of the fields as slices that live as long as the plan.</summary>
public sealed class JoinPlan : IDisposable
{
    private readonly ByteStringContext _names = new(SharedMultipleUseFlag.None);

    public JoinDefinition Definition { get; }

    public JoinSchema Schema { get; }

    public Slice IdField { get; }

    public Slice OuterKeyField { get; }

    /// <summary>The key field of the inner index of a join on a field; default for a join on the id.</summary>
    public Slice InnerKeyField { get; }

    public bool IsFieldJoin => Definition.InnerField != null;

    public Slice[] Fields { get; }

    public Slice[] GroupingFields { get; }

    public JoinPlan(JoinDefinition definition)
    {
        Definition = definition;
        Schema = JoinSchema.From(definition);
        IdField = Create(Constants.Documents.Indexing.Fields.DocumentIdFieldName);
        OuterKeyField = Create(definition.OuterField);
        if (definition.InnerField != null)
            InnerKeyField = Create(definition.InnerField);

        Fields = new Slice[definition.Fields.Count];
        for (int i = 0; i < Fields.Length; i++)
            Fields[i] = Create(definition.Fields[i].Field);

        GroupingFields = new Slice[definition.Groupings.Count];
        for (int i = 0; i < GroupingFields.Length; i++)
            GroupingFields[i] = Create(definition.Groupings[i].Field);
    }

    private Slice Create(string name)
    {
        Slice.From(_names, name, ByteStringType.Immutable, out Slice slice);
        return slice;
    }

    public bool IsLong(int field) => Definition.Fields[field].Kind == JoinFieldKind.Long;

    public static bool TryParseLong(ReadOnlySpan<byte> term, out long value)
    {
        return Utf8Parser.TryParse(term, out value, out int consumed) && consumed == term.Length;
    }

    public void Dispose() => _names.Dispose();
}
