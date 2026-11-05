using System;
using System.IO;
using Sparrow;
using Voron;
using Voron.Data.BTrees;
using Voron.Data.Lookups;
using Voron.Impl;

namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>The rows and structures that every home of a join index has, whether it is a file of its own or the environment of a join index.</summary>
public static unsafe class JoinFileRows
{
    public static bool Exists(Transaction tx) => tx.ReadTree(JoinNames.Meta) != null;

    /// <summary>Creates the structures and the rows of an empty join index in the transaction.</summary>
    public static void Initialize(Transaction tx, JoinSchema schema, long seed)
    {
        Tree meta = tx.CreateTree(JoinNames.Meta);
        meta.Add(JoinNames.FormatRow, (long)JoinStorageFormat.Version);
        meta.Add(JoinNames.SchemaRow, schema.Hash);
        meta.Add(JoinNames.SeedRow, seed);
        meta.Add(JoinNames.GenerationRow, JoinIndexGeneration.Initial.Serialize());
        meta.Add(JoinNames.SeveralRow, 0L);

        tx.OpenContainer(JoinStorageFormat.Lists);
        tx.LookupFor<Int64LookupKey>(JoinStorageFormat.OuterDocuments);
        tx.LookupFor<Int64LookupKey>(JoinStorageFormat.InnerDocuments);
        tx.LookupFor<Int64LookupKey>(JoinStorageFormat.OuterToInner);
        if (schema.IsFieldJoin)
        {
            meta.Add(JoinNames.SeveralKeysRow, 0L);
            tx.CompactTreeFor(JoinStorageFormat.KeyTerms);
            tx.LookupFor<Int64LookupKey>(JoinStorageFormat.InnerKeys);
            tx.LookupFor<Int64LookupKey>(JoinStorageFormat.KeyAtoms);
            tx.LookupFor<Int64LookupKey>(JoinStorageFormat.KeyCounts);
        }

        for (int field = 0; field < schema.FieldCount; field++)
        {
            tx.CompactTreeFor(JoinStorageFormat.Terms(field));
            tx.LookupFor<Int64LookupKey>(JoinStorageFormat.ListHandles(field));
            tx.LookupFor<Int64LookupKey>(JoinStorageFormat.InnerTerms(field));
        }
    }

    public static long ReadInt64(Tree meta, Slice row)
    {
        if (meta.TryRead(row, out ValueReader reader) == false)
            throw new InvalidDataException($"The join index file has no row '{row}'.");

        return *(long*)reader.Base;
    }

    /// <summary>Reads the generation of the transaction. The published one is reused when it is the very commit that the transaction shows.</summary>
    public static JoinIndexGeneration ReadGeneration(Tree meta, JoinIndexGeneration published)
    {
        if (meta.TryRead(JoinNames.GenerationRow, out ValueReader reader) == false)
            throw new InvalidDataException("The join index file has no generation.");

        var span = new ReadOnlySpan<byte>(reader.Base, reader.Length);
        return published != null && JoinIndexGeneration.PeekSerial(span) == published.Serial ? published : JoinIndexGeneration.Deserialize(span);
    }

    /// <summary>The hash of a document id as it is kept in the rows that find the entry of a document. Ids are given lowercased.</summary>
    public static long HashDocumentId(ReadOnlySpan<byte> lowerId, long seed) => unchecked((long)Hashing.XXHash64.CalculateInline(lowerId, (ulong)seed));

    /// <summary>
    /// The key of an inner document in <c>doc.i</c>, in <c>o2i</c>, <c>fm</c> and the member lists of the groupings. It is made of the id of the document, so it does not change when the index
    /// gives the document another entry (the document was deleted and created again): the rows of everything that is joined to it stay as they are. It has 53 bits, so that it stays positive in the encoding of the lists, and is never 0.
    /// </summary>
    public static long InnerKeyOf(ReadOnlySpan<byte> lowerId, long seed)
    {
        long key = (long)((ulong)HashDocumentId(lowerId, seed) >> 11);
        return key == 0 ? 1 : key;
    }
}

/// <summary>Turns the id of an inner document (lowercased) into its key in the structures of a join index file.</summary>
public interface IInnerKeys
{
    long InnerKeyOf(ReadOnlySpan<byte> lowerId);
}

/// <summary>What the refresh engine needs from the home of a join index: the shape of the lists, the seed of the hashes, the generation and a way to write.</summary>
public interface IJoinFile : IInnerKeys
{
    JoinSchema Schema { get; }

    /// <summary>The generation of the lists as they are now.</summary>
    JoinIndexGeneration Published { get; }

    long HashDocumentId(ReadOnlySpan<byte> lowerId);

    JoinWriter BeginWrite();
}
