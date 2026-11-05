using System;
using System.Collections.Generic;
using System.Threading;
using Corax.Mappings;
using Corax.Querying;
using Voron.Impl;

namespace Raven.Server.Documents.Indexes
{
    /// <summary>
    /// A read snapshot of a Corax index: the searcher and what the index had consumed, both read from the same storage transaction.
    /// Holds the index's running-queries lock until it is disposed: dispose it within the query or refresh batch that opened it, never keep it across batches. Index reset, replace, optimize and delete wait for it (and fail after 10 seconds).
    /// </summary>
    internal sealed class IndexSnapshot : IDisposable
    {
        private IDisposable _reader;
        private IDisposable _transaction;
        private IDisposable _context;
        private IDisposable _queryLock;

        internal IndexSnapshot(Guid environmentId, IndexSearcher searcher, Transaction transaction, IReadOnlyDictionary<string, (long Doc, long Tombstone)> consumed,
            IndexFieldsMapping fieldsMapping, IDisposable reader, IDisposable ownedTransaction, IDisposable context, IDisposable queryLock)
        {
            EnvironmentId = environmentId;
            Searcher = searcher;
            Transaction = transaction;
            Consumed = consumed;
            FieldsMapping = fieldsMapping;
            _reader = reader;
            _transaction = ownedTransaction;
            _context = context;
            _queryLock = queryLock;
        }

        public Guid EnvironmentId { get; }

        /// <summary>The fields of the index with their analyzers: encode a value that is looked up in a field with <see cref="IndexSearcher.EncodeAndApplyAnalyzer"/> and the metadata of the field.</summary>
        public IndexFieldsMapping FieldsMapping { get; }

        public IndexSearcher Searcher { get; }

        /// <summary>The read transaction of the snapshot, the one the searcher reads with. It belongs to the snapshot: do not dispose or commit it.</summary>
        public Transaction Transaction { get; }

        /// <summary>Per collection: the last document etag indexed and the last tombstone etag processed.</summary>
        public IReadOnlyDictionary<string, (long Doc, long Tombstone)> Consumed { get; }

        public void Dispose()
        {
            DisposeOnce(ref _reader);
            DisposeOnce(ref _transaction);
            DisposeOnce(ref _context);
            DisposeOnce(ref _queryLock);
        }

        private static void DisposeOnce(ref IDisposable disposable)
        {
            Interlocked.Exchange(ref disposable, null)?.Dispose();
        }
    }
}
