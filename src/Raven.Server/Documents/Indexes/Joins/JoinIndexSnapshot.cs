using System;
using System.Threading;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron.Data.BTrees;
using Voron.Impl;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>A read transaction of the environment of a join index: the lists and the generation in it belong together, they come from one commit of the index.</summary>
public sealed class JoinIndexSnapshot : JoinReader, IInnerKeys
{
    private IDisposable _owned;

    /// <summary>The generation the lists reflect; the initial one while the lists were not built.</summary>
    public JoinIndexGeneration Generation { get; }

    /// <summary>The seed of the hashes of the document ids in these lists.</summary>
    public long Seed { get; }

    public long InnerKeyOf(ReadOnlySpan<byte> lowerId) => JoinFileRows.InnerKeyOf(lowerId, Seed);

    internal JoinIndexSnapshot(Transaction tx, JoinSchema schema, IDisposable owned) : base(tx, schema)
    {
        _owned = owned;
        Tree meta = tx.ReadTree(JoinNames.Meta);
        Generation = meta == null ? JoinIndexGeneration.Initial : JoinFileRows.ReadGeneration(meta, null);
        Seed = meta == null ? 0 : JoinFileRows.ReadInt64(meta, JoinNames.SeedRow);
    }

    public override void Dispose()
    {
        base.Dispose();
        Interlocked.Exchange(ref _owned, null)?.Dispose();
    }
}
