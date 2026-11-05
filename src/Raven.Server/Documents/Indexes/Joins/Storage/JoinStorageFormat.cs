using System;
using Raven.Client.Documents.Indexes;

namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>The names of the structures of a join index file, format 3: the inner side is addressed by the key of the inner document, not by the entry of the index, and an outer entry holds the keys of every document it names.</summary>
public static class JoinStorageFormat
{
    /// <summary>The format row of a file that holds the outer entries of the keys in its lists (<see cref="JoinStorageMode.Expanded"/>).</summary>
    public const int Version = 3;

    /// <summary>The format row of a file whose atom lists hold keys (<see cref="JoinStorageMode.Indirect"/>): an older build must not read them as outer entries.</summary>
    public const int IndirectVersion = 4;

    public static int VersionOf(JoinStorageMode mode)
    {
        return mode switch
        {
            JoinStorageMode.Expanded => Version,
            JoinStorageMode.Indirect => IndirectVersion,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "A file has a resolved storage mode.")
        };
    }

    /// <summary>The mode that a format row stands for, <see cref="JoinStorageMode.Auto"/> for a format that this build does not know.</summary>
    public static JoinStorageMode ModeOf(long version)
    {
        return version switch
        {
            Version => JoinStorageMode.Expanded,
            IndirectVersion => JoinStorageMode.Indirect,
            _ => JoinStorageMode.Auto
        };
    }

    public const int MaxFields = 8;

    public const int MaxGroupings = 4;

    public const string Meta = "meta";

    public const string OuterDocuments = "doc.o";

    public const string InnerDocuments = "doc.i";

    /// <summary>Outer entry to the keys of the inner documents its key field names: one key as the key itself shifted left, several as the container item that holds them with the lowest bit set.</summary>
    public const string OuterToInner = "o2i";

    /// <summary>Field joins of an Indirect file: outer entry to the fingerprint of the set of the keys of the entry (see OuterKeyFingerprint), for every entry that has a key and whose fingerprint is known.</summary>
    public const string OuterFingerprints = "o2f";

    /// <summary>Field joins: key term to the reference of the key (the dictionary of the keys of both sides).</summary>
    public const string KeyTerms = "kd";

    /// <summary>Field joins: key of an inner document to the references of its keys (one key as the reference shifted left, several as the container item that holds them with the lowest bit set).</summary>
    public const string InnerKeys = "ik";

    /// <summary>Field joins: reference of a key to the list of the atoms that its group of inner entries has.</summary>
    public const string KeyAtoms = "ka";

    /// <summary>Field joins: pair of a key and an atom to the number of the inner entries of the group that have the atom.</summary>
    public const string KeyCounts = "cn";

    public const string Lists = "lists";

    public const string FormatRow = "format";

    public const string SchemaRow = "schema";

    public const string SeedRow = "seed";

    public const string GenerationRow = "generation";

    /// <summary>The number of outer entries that are joined with two or more inner documents.</summary>
    public const string SeveralRow = "several";

    /// <summary>Field joins: the number of outer entries that hold two or more keys whose group has an inner entry.</summary>
    public const string SeveralKeysRow = "several.keys";

    public const string SlotRowPrefix = "slot.";

    public static string Terms(int field) => "ft." + field;

    public static string ListHandles(int field) => "fh." + field;

    public static string InnerTerms(int field) => "fm." + field;
}
