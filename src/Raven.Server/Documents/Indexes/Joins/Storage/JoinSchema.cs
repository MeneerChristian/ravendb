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

    /// <summary>How the atom lists hold their members; never <see cref="JoinStorageMode.Auto"/>.</summary>
    public JoinStorageMode Mode { get; }

    /// <summary>The atom lists hold keys, not outer entries.</summary>
    public bool IsIndirect => Mode == JoinStorageMode.Indirect;

    /// <summary>
    /// The key of a group is the id of its one inner document (an Indirect join on the id): every count of a pair of a key and an atom is 0 or 1 and is the atoms of the document, so no counts are kept.
    /// </summary>
    public bool KeysAreInnerIds { get; }

    /// <summary>The file keeps the fingerprint of the keys of every outer entry (an Indirect join index), so that an entry whose keys did not change is known without looking its keys up.</summary>
    public bool HasOuterFingerprints => IsIndirect;

    private readonly bool[] _multiValued;

    public JoinSchema(bool[] multiValued, int groupingCount, long hash, bool isFieldJoin = false, JoinStorageMode mode = JoinStorageMode.Expanded, bool keysAreInnerIds = false)
    {
        if (mode is not (JoinStorageMode.Expanded or JoinStorageMode.Indirect))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "A schema has a resolved storage mode.");

        if (mode == JoinStorageMode.Indirect && isFieldJoin == false)
            throw new ArgumentException("Only a join on a field has keys to list.", nameof(mode));

        if (keysAreInnerIds && (mode != JoinStorageMode.Indirect || isFieldJoin == false))
            throw new ArgumentException("Only an Indirect join on the id has the ids of the inner documents as keys.", nameof(keysAreInnerIds));

        if (multiValued.Length > JoinLimits.MaxFields)
            throw new ArgumentOutOfRangeException(nameof(multiValued), $"At most {JoinLimits.MaxFields} fields.");

        if (groupingCount > JoinLimits.MaxGroupings)
            throw new ArgumentOutOfRangeException(nameof(groupingCount), $"At most {JoinLimits.MaxGroupings} groupings.");

        FieldCount = multiValued.Length;
        GroupingCount = groupingCount;
        _multiValued = multiValued;
        Hash = hash;
        IsFieldJoin = isFieldJoin;
        Mode = mode;
        KeysAreInnerIds = keysAreInnerIds;
    }

    /// <summary>The schema of a join index of the definition in the given mode. The hash text of an Expanded schema is the same as before the modes existed, so that the files that were built then are still the schema's own.</summary>
    public static JoinSchema From(JoinDefinition definition, JoinStorageMode mode = JoinStorageMode.Expanded)
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

        bool keysAreInnerIds = mode == JoinStorageMode.Indirect && definition.InnerField == null;
        if (mode == JoinStorageMode.Indirect)
            text.Append("mode:indirect\nouter:fingerprint.rows\n");

        if (keysAreInnerIds)
            text.Append("keys:ids\n");

        long hash = unchecked((long)Hashing.XXHash64.CalculateInline(Encoding.UTF8.GetBytes(text.ToString())));
        // an Indirect join on the id keeps the groups of the inner documents by their id, like a join on a field
        return new JoinSchema(multiValued, definition.Groupings.Count, hash, definition.InnerField != null || mode == JoinStorageMode.Indirect, mode, keysAreInnerIds);
    }

    /// <summary>A schema for tests and tools that do not come from a definition.</summary>
    public static JoinSchema Create(int fieldCount, int groupingCount, long hash = 1, bool[] multiValued = null, bool isFieldJoin = false, JoinStorageMode mode = JoinStorageMode.Expanded, bool keysAreInnerIds = false)
    {
        return new JoinSchema(multiValued ?? new bool[fieldCount], groupingCount, hash, isFieldJoin, mode, keysAreInnerIds);
    }
}
