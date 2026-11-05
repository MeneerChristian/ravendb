namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>The names of the structures of a join index file, format 3: the inner side is addressed by the key of the inner document, not by the entry of the index, and an outer entry holds the keys of every document it names.</summary>
public static class JoinStorageFormat
{
    public const int Version = 3;

    public const int MaxFields = 8;

    public const int MaxGroupings = 4;

    public const string Meta = "meta";

    public const string OuterDocuments = "doc.o";

    public const string InnerDocuments = "doc.i";

    /// <summary>Outer entry to the keys of the inner documents its key field names: one key as the key itself shifted left, several as the container item that holds them with the lowest bit set.</summary>
    public const string OuterToInner = "o2i";

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
