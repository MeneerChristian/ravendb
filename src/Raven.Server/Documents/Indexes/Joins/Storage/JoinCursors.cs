using System;
using System.Collections.Generic;
using System.Linq;

namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>What an index has consumed, per collection: the etag of the last indexed document and of the last processed tombstone.</summary>
public sealed class JoinCursors : IEquatable<JoinCursors>
{
    public static readonly JoinCursors Empty = new(Array.Empty<(string, long, long)>());

    public IReadOnlyList<(string Collection, long Document, long Tombstone)> Collections { get; }

    public JoinCursors(IEnumerable<(string Collection, long Document, long Tombstone)> collections)
    {
        Collections = collections.OrderBy(x => x.Collection, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static JoinCursors From(IReadOnlyDictionary<string, (long Doc, long Tombstone)> consumed)
    {
        var list = new (string, long, long)[consumed.Count];
        int i = 0;
        foreach ((string collection, (long doc, long tombstone)) in consumed)
            list[i++] = (collection, doc, tombstone);

        return new JoinCursors(list);
    }

    /// <summary>Whether both name the same collections.</summary>
    public bool HasSameCollections(IReadOnlyDictionary<string, (long Doc, long Tombstone)> consumed)
    {
        if (consumed.Count != Collections.Count)
            return false;

        foreach ((string collection, _, _) in Collections)
        {
            if (consumed.ContainsKey(collection) == false)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Compares with what an index has consumed, as <see cref="CompareTo(JoinCursors)"/> does, without building cursors. Both must name the same collections.
    /// Equal: the same, Behind: this is behind the index, Ahead: this is ahead of the index in at least one etag.
    /// </summary>
    public GenerationComparison CompareTo(IReadOnlyDictionary<string, (long Doc, long Tombstone)> consumed)
    {
        bool greater = false;
        bool smaller = false;

        foreach ((string collection, long document, long tombstone) in Collections)
        {
            (long otherDocument, long otherTombstone) = consumed.TryGetValue(collection, out (long Doc, long Tombstone) value) ? value : (0, 0);
            Note(document, otherDocument, ref greater, ref smaller);
            Note(tombstone, otherTombstone, ref greater, ref smaller);
        }

        if (greater)
            return GenerationComparison.Ahead;

        return smaller ? GenerationComparison.Behind : GenerationComparison.Equal;
    }

    public bool Equals(JoinCursors other)
    {
        if (other is null || other.Collections.Count != Collections.Count)
            return false;

        for (int i = 0; i < Collections.Count; i++)
        {
            if (string.Equals(Collections[i].Collection, other.Collections[i].Collection, StringComparison.OrdinalIgnoreCase) == false ||
                Collections[i].Document != other.Collections[i].Document ||
                Collections[i].Tombstone != other.Collections[i].Tombstone)
                return false;
        }

        return true;
    }

    public override bool Equals(object obj) => Equals(obj as JoinCursors);

    public override int GetHashCode() => Collections.Count;

    /// <summary>
    /// Compares as vectors: every etag of this is at least the one of the other (and one is greater) = Ahead, every etag is at most (and one is smaller) = Behind.
    /// A collection that one side does not have counts as 0. Etags that go both ways are Ahead, because an answer is only exact when nothing is ahead.
    /// </summary>
    public GenerationComparison CompareTo(JoinCursors other)
    {
        bool greater = false;
        bool smaller = false;

        foreach ((string collection, long document, long tombstone) in Collections)
        {
            (long otherDocument, long otherTombstone) = other.Find(collection);
            Note(document, otherDocument, ref greater, ref smaller);
            Note(tombstone, otherTombstone, ref greater, ref smaller);
        }

        foreach ((string collection, long document, long tombstone) in other.Collections)
        {
            if (Find(collection) != (0, 0))
                continue;

            Note(0, document, ref greater, ref smaller);
            Note(0, tombstone, ref greater, ref smaller);
        }

        if (greater)
            return GenerationComparison.Ahead;

        return smaller ? GenerationComparison.Behind : GenerationComparison.Equal;
    }

    private static void Note(long mine, long theirs, ref bool greater, ref bool smaller)
    {
        if (mine > theirs)
            greater = true;
        else if (mine < theirs)
            smaller = true;
    }

    /// <summary>The etags of a collection, (0, 0) when the collection is not in the cursors.</summary>
    public (long Document, long Tombstone) Of(string collection) => Find(collection);

    private (long Document, long Tombstone) Find(string collection)
    {
        foreach ((string name, long document, long tombstone) in Collections)
        {
            if (string.Equals(name, collection, StringComparison.OrdinalIgnoreCase))
                return (document, tombstone);
        }

        return (0, 0);
    }

    public override string ToString() => string.Join(", ", Collections.Select(x => $"{x.Collection}:{x.Document}/{x.Tombstone}"));
}

public enum GenerationComparison
{
    Equal,
    Behind,
    Ahead
}
