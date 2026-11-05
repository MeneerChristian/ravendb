using System;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>
/// The keys of the rows of a build. The walk of the key terms reaches the pairs of a row and a key in the order of the keys, so every pair would read the row, put the key in and write the row again; the pairs are
/// noted instead, sorted by row once the queue is full (or the walk ends), and every row is written once with all its keys.
/// </summary>
internal sealed partial class JoinBuilder
{
    private struct RowKey : IComparable<RowKey>
    {
        public long Row;
        public long KeyRef;
        public long TermHash;

        public int CompareTo(RowKey other)
        {
            int result = Row.CompareTo(other.Row);
            return result != 0 ? result : KeyRef.CompareTo(other.KeyRef);
        }
    }

    private ContextBoundNativeList<RowKey> _rowKeys;
    private ContextBoundNativeList<long> _rowKeyList;
    private ContextBoundNativeList<long> _rowTermHashes;

    private void InitializeRowKeys()
    {
        _rowKeys = new ContextBoundNativeList<RowKey>(_allocator, 1024);
        _rowKeyList = new ContextBoundNativeList<long>(_allocator, 32);
        _rowTermHashes = new ContextBoundNativeList<long>(_allocator, 32);
    }

    private void DisposeRowKeys()
    {
        _rowKeys.Dispose();
        _rowKeyList.Dispose();
        _rowTermHashes.Dispose();
    }

    /// <summary>The row (an outer entry, or an inner document) has the key. A queue that is full is written first: a row that has pairs on both sides of it gets its keys merged into what the first write left.</summary>
    private void NoteRowKey(long row, long keyRef, bool ofInnerDocument, long termHash = 0)
    {
        if (_rowKeys.Count >= _flushThreshold)
            WriteRowKeys(ofInnerDocument);

        _rowKeys.Add(new RowKey { Row = row, KeyRef = keyRef, TermHash = termHash });
    }

    private void WriteRowKeys(bool ofInnerDocument)
    {
        Span<RowKey> pairs = _rowKeys.ToSpan();
        pairs.Sort();
        bool withFingerprints = ofInnerDocument == false && _writer.Schema.HasOuterFingerprints;

        int index = 0;
        while (index < pairs.Length)
        {
            long row = pairs[index].Row;
            _rowKeyList.Count = 0;
            _rowTermHashes.Count = 0;
            while (index < pairs.Length && pairs[index].Row == row)
            {
                RowKey pair = pairs[index++];
                if (_rowKeyList.Count == 0 || _rowKeyList[_rowKeyList.Count - 1] != pair.KeyRef)
                {
                    _rowKeyList.Add(pair.KeyRef);
                    if (withFingerprints)
                        _rowTermHashes.Add(pair.TermHash);
                }
            }

            if (ofInnerDocument)
                _writer.AddInnerKeys(row, _rowKeyList.ToSpan());
            else
            {
                long fingerprint = 0;
                if (withFingerprints)
                    OuterKeyFingerprint.Of(_rowTermHashes.ToSpan(), out fingerprint);

                _writer.AddOuterKeys(row, _rowKeyList.ToSpan(), fingerprint);
            }

            ThrowIfCancelled();
        }

        _rowKeys.Count = 0;
    }
}
