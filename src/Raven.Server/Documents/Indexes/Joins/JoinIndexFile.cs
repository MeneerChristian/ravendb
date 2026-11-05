using System;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Refresh;
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

        // a file that was written in another format or for another definition is built again, not read as what it is not
        JoinStorageMode? mode = JoinFileRows.ReadMode(tx);
        if (mode != schema.Mode)
            throw new JoinRebuildRequiredException(JoinRebuildReason.FormatChanged, $"The lists of the join index have the storage mode {(mode == JoinStorageMode.Auto ? "of a format that is not known" : mode)}, the join index needs {schema.Mode}.");

        Tree meta = tx.ReadTree(JoinNames.Meta);
        if (JoinFileRows.ReadInt64(meta, JoinNames.SchemaRow) != schema.Hash)
            throw new JoinRebuildRequiredException(JoinRebuildReason.FormatChanged, "The lists of the join index were written for another definition. The index has to be reset.");

        _seed = JoinFileRows.ReadInt64(meta, JoinNames.SeedRow);
        Published = JoinFileRows.ReadGeneration(meta, null);
        if (Published.Mode != schema.Mode)
            throw new JoinRebuildRequiredException(JoinRebuildReason.FormatChanged, $"The generation of the join index has the storage mode {Published.Mode}, the join index needs {schema.Mode}.");
    }

    /// <summary>The seed of this file: random for every file that is built, so it names the file the key references belong to.</summary>
    public long Seed => _seed;

    /// <summary>Reads the structures of the file in the transaction of the refresh, as the transaction has them now.</summary>
    public JoinReader OpenReader() => new FileReader(_tx, Schema);

    private sealed class FileReader(Transaction tx, JoinSchema schema) : JoinReader(tx, schema);

    public long HashDocumentId(ReadOnlySpan<byte> lowerId) => JoinFileRows.HashDocumentId(lowerId, _seed);

    public long InnerKeyOf(ReadOnlySpan<byte> lowerId) => JoinFileRows.InnerKeyOf(lowerId, _seed);

    public JoinWriter BeginWrite() => new(_tx, Schema, Published.Serial);
}
