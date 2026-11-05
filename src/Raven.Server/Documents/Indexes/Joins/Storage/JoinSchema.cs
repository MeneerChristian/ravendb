using Raven.Client.Documents.Indexes;
using System;
using System.Text;
using Sparrow;

namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>The shape of a join index file: what a definition needs the file to hold. A file written for another shape is not reused.</summary>
public sealed class JoinSchema
{
    public int FieldCount { get; }

    public int GroupingCount { get; }

    /// <summary>A field with several terms per inner entry (Text kind).</summary>
    public bool IsMultiValued(int field) => _multiValued[field];

    public long Hash { get; }

    /// <summary>The join is on a field of the inner index: the file holds the groups of the inner entries by key and the key of every outer entry.</summary>
    public bool IsFieldJoin { get; }

    private readonly bool[] _multiValued;

    public JoinSchema(bool[] multiValued, int groupingCount, long hash, bool isFieldJoin = false)
    {
        if (multiValued.Length > JoinLimits.MaxFields)
            throw new ArgumentOutOfRangeException(nameof(multiValued), $"At most {JoinLimits.MaxFields} fields.");

        if (groupingCount > JoinLimits.MaxGroupings)
            throw new ArgumentOutOfRangeException(nameof(groupingCount), $"At most {JoinLimits.MaxGroupings} groupings.");

        FieldCount = multiValued.Length;
        GroupingCount = groupingCount;
        _multiValued = multiValued;
        Hash = hash;
        IsFieldJoin = isFieldJoin;
    }

    public static JoinSchema From(JoinDefinition definition)
    {
        var multiValued = new bool[definition.Fields.Count];
        var text = new StringBuilder();
        // the second line says that an outer entry holds the keys of every document it names, not one
        text.Append(definition.OuterIdentity).Append('\n').Append(definition.OuterField).Append('\n').Append(definition.InnerIdentity).Append('\n').Append("keys:many\n");
        if (definition.InnerField != null)
            text.Append("field:").Append(definition.InnerField).Append("\nkeys:several\n");

        for (int i = 0; i < definition.Fields.Count; i++)
        {
            JoinField field = definition.Fields[i];
            multiValued[i] = field.Kind == JoinFieldKind.Text;
            text.Append(field.Field).Append(':').Append((int)field.Kind).Append('\n');
        }

        foreach (JoinGrouping grouping in definition.Groupings)
            text.Append("g:").Append(grouping.Field).Append('=').Append(grouping.Value).Append('\n');

        long hash = unchecked((long)Hashing.XXHash64.CalculateInline(Encoding.UTF8.GetBytes(text.ToString())));
        return new JoinSchema(multiValued, definition.Groupings.Count, hash, definition.InnerField != null);
    }

    /// <summary>A schema for tests and tools that do not come from a definition.</summary>
    public static JoinSchema Create(int fieldCount, int groupingCount, long hash = 1, bool[] multiValued = null, bool isFieldJoin = false)
    {
        return new JoinSchema(multiValued ?? new bool[fieldCount], groupingCount, hash, isFieldJoin);
    }
}
