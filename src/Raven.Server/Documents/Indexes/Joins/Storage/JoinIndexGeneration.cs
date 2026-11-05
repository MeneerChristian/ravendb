using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>
/// What the lists of a join index reflect: the environments of the two indexes and what each of them had consumed.
/// It is written in the same commit as the lists, so a reader of a snapshot of the join index reads the generation of the lists it sees.
/// </summary>
public sealed class JoinIndexGeneration : IEquatable<JoinIndexGeneration>
{
    public static readonly JoinIndexGeneration Initial = new(0, Guid.Empty, JoinCursors.Empty, Guid.Empty, JoinCursors.Empty, 0, 0);

    /// <summary>The number of commits that published a generation.</summary>
    public long Serial { get; }

    public Guid OuterEnvironmentId { get; }

    public JoinCursors Outer { get; }

    public Guid InnerEnvironmentId { get; }

    public JoinCursors Inner { get; }

    /// <summary>The last entry id of the inner index that was consumed, entries above it were not seen yet.</summary>
    public long InnerWatermark { get; }

    public long PublishedAtTicks { get; }

    /// <summary>
    /// The lists are an exact intermediate state of a refresh that was cut at the budget of a commit: the cursors say how far it got. It is never served, whatever the cursors say, because it
    /// holds the entries of a newer snapshot of an index than the cursors name.
    /// </summary>
    public bool IsPartial { get; }

    public JoinIndexGeneration(long serial, Guid outerEnvironmentId, JoinCursors outer, Guid innerEnvironmentId, JoinCursors inner, long innerWatermark, long publishedAtTicks, bool isPartial = false)
    {
        IsPartial = isPartial;
        Serial = serial;
        OuterEnvironmentId = outerEnvironmentId;
        Outer = outer;
        InnerEnvironmentId = innerEnvironmentId;
        Inner = inner;
        InnerWatermark = innerWatermark;
        PublishedAtTicks = publishedAtTicks;
    }

    public JoinIndexGeneration WithSerial(long serial, long publishedAtTicks)
    {
        return new JoinIndexGeneration(serial, OuterEnvironmentId, Outer, InnerEnvironmentId, Inner, InnerWatermark, publishedAtTicks, IsPartial);
    }

    /// <summary>Whether both indexes are the very ones the lists were built from, so the cursors can be compared at all.</summary>
    public bool HasSameSources(Guid outerEnvironmentId, Guid innerEnvironmentId) => OuterEnvironmentId == outerEnvironmentId && InnerEnvironmentId == innerEnvironmentId;

    public bool Equals(JoinIndexGeneration other)
    {
        return other is not null && Serial == other.Serial && OuterEnvironmentId == other.OuterEnvironmentId && InnerEnvironmentId == other.InnerEnvironmentId &&
               InnerWatermark == other.InnerWatermark && IsPartial == other.IsPartial && Outer.Equals(other.Outer) && Inner.Equals(other.Inner);
    }

    public override bool Equals(object obj) => Equals(obj as JoinIndexGeneration);

    public override int GetHashCode() => Serial.GetHashCode();

    public override string ToString() => $"#{Serial}{(IsPartial ? " partial" : "")} outer[{OuterEnvironmentId:N}: {Outer}] inner[{InnerEnvironmentId:N}: {Inner}] watermark {InnerWatermark}";

    private const int Format = 2;

    public byte[] Serialize()
    {
        int size = sizeof(int) + 2 * 16 + 3 * sizeof(long) + 1 + CursorsSize(Outer) + CursorsSize(Inner);
        byte[] buffer = new byte[size];
        Span<byte> span = buffer;
        int pos = 0;

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos), Format);
        pos += sizeof(int);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(pos), Serial);
        pos += sizeof(long);
        OuterEnvironmentId.TryWriteBytes(span.Slice(pos));
        pos += 16;
        InnerEnvironmentId.TryWriteBytes(span.Slice(pos));
        pos += 16;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(pos), InnerWatermark);
        pos += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(pos), PublishedAtTicks);
        pos += sizeof(long);
        WriteCursors(span, ref pos, Outer);
        WriteCursors(span, ref pos, Inner);
        span[pos++] = IsPartial ? (byte)1 : (byte)0;

        return buffer;
    }

    /// <summary>Reads the serial of a serialized generation without reading the rest of it.</summary>
    public static long PeekSerial(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt64LittleEndian(span.Slice(sizeof(int)));

    public static JoinIndexGeneration Deserialize(ReadOnlySpan<byte> span)
    {
        int pos = 0;
        int format = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos));
        if (format != Format)
            throw new InvalidOperationException($"The generation has the format {format}, expected {Format}.");

        pos += sizeof(int);
        long serial = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(pos));
        pos += sizeof(long);
        var outerId = new Guid(span.Slice(pos, 16));
        pos += 16;
        var innerId = new Guid(span.Slice(pos, 16));
        pos += 16;
        long watermark = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(pos));
        pos += sizeof(long);
        long publishedAt = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(pos));
        pos += sizeof(long);
        JoinCursors outer = ReadCursors(span, ref pos);
        JoinCursors inner = ReadCursors(span, ref pos);
        bool isPartial = span[pos] != 0;

        return new JoinIndexGeneration(serial, outerId, outer, innerId, inner, watermark, publishedAt, isPartial);
    }

    private static int CursorsSize(JoinCursors cursors)
    {
        int size = sizeof(int);
        foreach ((string collection, _, _) in cursors.Collections)
            size += sizeof(int) + Encoding.UTF8.GetByteCount(collection) + 2 * sizeof(long);

        return size;
    }

    private static void WriteCursors(Span<byte> span, ref int pos, JoinCursors cursors)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos), cursors.Collections.Count);
        pos += sizeof(int);

        foreach ((string collection, long document, long tombstone) in cursors.Collections)
        {
            int length = Encoding.UTF8.GetBytes(collection, span.Slice(pos + sizeof(int)));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos), length);
            pos += sizeof(int) + length;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(pos), document);
            pos += sizeof(long);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(pos), tombstone);
            pos += sizeof(long);
        }
    }

    private static JoinCursors ReadCursors(ReadOnlySpan<byte> span, ref int pos)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos));
        pos += sizeof(int);

        var list = new List<(string, long, long)>(count);
        for (int i = 0; i < count; i++)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos));
            pos += sizeof(int);
            string collection = Encoding.UTF8.GetString(span.Slice(pos, length));
            pos += length;
            long document = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(pos));
            pos += sizeof(long);
            long tombstone = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(pos));
            pos += sizeof(long);
            list.Add((collection, document, tombstone));
        }

        return new JoinCursors(list);
    }
}
