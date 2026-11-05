using System;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Raven.Server.Storage.Layout;
using Voron;
using Voron.Data.BTrees;
using Voron.Impl;

namespace FastTests.Server.Documents.Indexing.Joins.Storage;

/// <summary>A Voron environment that holds the lists of one join index and nothing else: writes go through the same <see cref="JoinIndexFile"/> that a join index writes through.</summary>
internal sealed class JoinStorageHarness : IDisposable
{
    private readonly StorageEnvironment _environment;

    public JoinSchema Schema { get; }

    /// <summary>The seed of the hash of the document ids; every file has its own.</summary>
    public long Seed { get; }

    /// <summary>The generation of the last commit.</summary>
    public JoinIndexGeneration Published { get; private set; }

    private JoinStorageHarness(StorageEnvironment environment, JoinSchema schema)
    {
        _environment = environment;
        Schema = schema;

        using (Transaction tx = environment.WriteTransaction())
        {
            _ = new JoinIndexFile(tx, schema);
            tx.Commit();
        }

        using (Transaction tx = environment.ReadTransaction())
        {
            Tree meta = tx.ReadTree(JoinNames.Meta);
            Seed = JoinFileRows.ReadInt64(meta, JoinNames.SeedRow);
            Published = JoinFileRows.ReadGeneration(meta, null);
        }
    }

    public static JoinSchema CreateSchema(int fields = 2, int groupings = 1, bool[] multiValued = null, long hash = 1, JoinStorageMode mode = JoinStorageMode.Expanded, bool? isFieldJoin = null)
    {
        return JoinSchema.Create(fields, groupings, hash, multiValued, isFieldJoin ?? mode == JoinStorageMode.Indirect, mode);
    }

    public static JoinStorageHarness OpenInMemory(JoinSchema schema = null)
    {
        StorageEnvironment environment = StorageLoader.OpenEnvironment(StorageEnvironmentOptions.CreateMemoryOnlyForTests(), StorageEnvironmentWithType.StorageEnvironmentType.Index);
        return new JoinStorageHarness(environment, schema ?? CreateSchema());
    }

    public long HashDocumentId(ReadOnlySpan<byte> lowerId) => JoinFileRows.HashDocumentId(lowerId, Seed);

    /// <summary>Starts the write transaction of the environment; <see cref="Write.Commit"/> commits it with the generation.</summary>
    public Write BeginWrite(JoinSchema schema = null)
    {
        Transaction tx = _environment.WriteTransaction();
        try
        {
            return new Write(this, tx, new JoinIndexFile(tx, schema ?? Schema).BeginWrite());
        }
        catch
        {
            tx.Dispose();
            throw;
        }
    }

    /// <summary>A write transaction of the environment, for a caller that builds the file itself (a <see cref="JoinIndexFile"/> over it) and commits it.</summary>
    public Transaction BeginWriteTransaction() => _environment.WriteTransaction();

    /// <summary>A read transaction: the lists and the generation in it belong together.</summary>
    public JoinIndexSnapshot OpenSnapshot()
    {
        Transaction tx = _environment.ReadTransaction();
        return new JoinIndexSnapshot(tx, Schema, tx);
    }

    public void Dispose() => _environment.Dispose();

    public sealed class Write : IDisposable
    {
        private readonly JoinStorageHarness _harness;
        private readonly Transaction _tx;

        internal Write(JoinStorageHarness harness, Transaction tx, JoinWriter writer)
        {
            _harness = harness;
            _tx = tx;
            Writer = writer;
        }

        public JoinWriter Writer { get; }

        public JoinIndexGeneration Commit(JoinIndexGeneration generation)
        {
            JoinIndexGeneration published = Writer.Commit(generation);
            _tx.Commit();
            _harness.Published = published;
            return published;
        }

        public void Dispose()
        {
            Writer.Dispose();
            _tx.Dispose();
        }
    }
}
