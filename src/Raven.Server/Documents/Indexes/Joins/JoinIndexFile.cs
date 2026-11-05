using System;
using System.IO;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron.Data.BTrees;
using Voron.Impl;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>
/// The lists of a join index: structures in the environment of the index itself, written in its write transaction. They are created in the first write transaction that needs them,
/// and the generation row is committed with the lists, so a reader of the environment sees both from one commit.
/// </summary>
internal sealed class JoinIndexFile : IJoinFile
{
    private readonly Transaction _tx;
    private readonly long _seed;

    public JoinSchema Schema { get; }

    public JoinIndexGeneration Published { get; }

    public JoinIndexFile(Transaction tx, JoinSchema schema)
    {
        _tx = tx;
        Schema = schema;

        if (JoinFileRows.Exists(tx) == false)
            JoinFileRows.Initialize(tx, schema, Random.Shared.NextInt64());

        Tree meta = tx.ReadTree(JoinNames.Meta);
        if (JoinFileRows.ReadInt64(meta, JoinNames.SchemaRow) != schema.Hash)
            throw new InvalidDataException("The lists of the join index were written for another definition. The index has to be reset.");

        _seed = JoinFileRows.ReadInt64(meta, JoinNames.SeedRow);
        Published = JoinFileRows.ReadGeneration(meta, null);
    }

    public long HashDocumentId(ReadOnlySpan<byte> lowerId) => JoinFileRows.HashDocumentId(lowerId, _seed);

    public long InnerKeyOf(ReadOnlySpan<byte> lowerId) => JoinFileRows.InnerKeyOf(lowerId, _seed);

    public JoinWriter BeginWrite() => new(_tx, Schema, Published.Serial);
}
