using System;

namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>A queued change of one list. The key says which list, the sequence keeps the order of the changes of one entry.</summary>
public struct ListOp : IComparable<ListOp>
{
    public long Key;
    public long Value;
    public int Sequence;
    public int IsRemoval;

    public int CompareTo(ListOp other)
    {
        int result = Key.CompareTo(other.Key);
        if (result != 0)
            return result;

        result = Value.CompareTo(other.Value);
        return result != 0 ? result : Sequence.CompareTo(other.Sequence);
    }
}
