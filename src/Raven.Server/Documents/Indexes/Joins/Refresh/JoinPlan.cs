using Raven.Client.Documents.Indexes;
using System;
using System.Buffers.Text;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Sparrow.Threading;
using Voron;
using Voron.Impl;
using Constants = Raven.Client.Constants;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>What the refresher needs to know about a definition to read the indexes: the names of the fields as slices that live as long as the plan.</summary>
public sealed class JoinPlan : IDisposable
{
    private readonly ByteStringContext _names = new(SharedMultipleUseFlag.None);

    public JoinDefinition Definition { get; }

    /// <summary>The mode in which a file of this join index is built. A file that exists keeps the mode it was built in, see <see cref="SchemaOf"/>.</summary>
    public JoinStorageMode BuildMode => _buildMode;

    private JoinStorageMode _buildMode;

    /// <summary>Sets the mode in which the file of this join index is built, for a join index whose mode is chosen when its file is built (see <see cref="JoinOptions.ResolveBuildMode"/>).</summary>
    public void ChooseBuildMode(JoinStorageMode mode)
    {
        if (mode is not (JoinStorageMode.Expanded or JoinStorageMode.Indirect) || (mode == JoinStorageMode.Indirect && _indirect == null))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "This join index cannot be built in that mode.");

        _buildMode = mode;
    }

    /// <summary>The schema of the file that this join index builds.</summary>
    public JoinSchema Schema => SchemaOf(BuildMode);

    private readonly JoinSchema _expanded;
    private readonly JoinSchema _indirect;

    public Slice IdField { get; }

    public Slice OuterKeyField { get; }

    /// <summary>The key field of the inner index: the field of a join on a field, the id field of an Indirect join on the id.</summary>
    public Slice InnerKeyField { get; }

    /// <summary>The file of this join index holds the groups of the inner entries by key (<see cref="JoinSchema.IsFieldJoin"/>): a join on a field, and an Indirect join on the id, whose groups are the single inner documents.</summary>
    public bool IsFieldJoin => Schema.IsFieldJoin;

    public Slice[] Fields { get; }

    public Slice[] GroupingFields { get; }

    public JoinPlan(JoinDefinition definition, JoinStorageMode buildMode = JoinStorageMode.Expanded)
    {
        if (buildMode is not (JoinStorageMode.Expanded or JoinStorageMode.Indirect))
            throw new ArgumentOutOfRangeException(nameof(buildMode), buildMode, "A plan builds in a resolved storage mode.");

        Definition = definition;
        _buildMode = buildMode;
        _expanded = JoinSchema.From(definition);
        _indirect = definition.JoinsOnOuterId == false ? JoinSchema.From(definition, JoinStorageMode.Indirect) : null;
        if (buildMode == JoinStorageMode.Indirect && _indirect == null)
            throw new ArgumentException("Only a join on a field or on the id of the inner index can be built in the Indirect mode.", nameof(buildMode));

        IdField = Create(Constants.Documents.Indexing.Fields.DocumentIdFieldName);
        OuterKeyField = Create(definition.OuterField);
        if (definition.InnerField != null)
            InnerKeyField = Create(definition.InnerField);
        else if (_indirect != null)
            InnerKeyField = IdField;

        Fields = new Slice[definition.Fields.Count];
        for (int i = 0; i < Fields.Length; i++)
            Fields[i] = Create(definition.Fields[i].Field);

        GroupingFields = new Slice[definition.Groupings.Count];
        for (int i = 0; i < GroupingFields.Length; i++)
            GroupingFields[i] = Create(definition.Groupings[i].Field);
    }

    /// <summary>The schema of a file in the mode, null when this join index cannot have such a file (<see cref="JoinStorageMode.Auto"/> is not a mode of a file).</summary>
    public JoinSchema SchemaOf(JoinStorageMode mode)
    {
        return mode switch
        {
            JoinStorageMode.Expanded => _expanded,
            JoinStorageMode.Indirect => _indirect,
            _ => null
        };
    }

    /// <summary>
    /// The schema of the file in the transaction: the one of the mode it was built in when the definition leaves the mode to the server (the default of the node applies to join indexes that are built, not to files that exist),
    /// else the schema of the mode of the definition. Null when the file has a mode that this join index cannot read.
    /// </summary>
    public JoinSchema SchemaOfFile(Transaction tx)
    {
        JoinStorageMode? mode = JoinFileRows.ReadMode(tx);
        if (mode == null || Definition.StorageMode != JoinStorageMode.Auto)
            return Schema;

        return SchemaOf(mode.Value) ?? Schema;
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
