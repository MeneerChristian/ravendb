using System;
using System.Collections.Generic;
using System.Linq;
using FastTests;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Storage;

public class JoinIndexGenerationTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static JoinCursors Cursors(params (string, long, long)[] collections) => new(collections);

    [RavenFact(RavenTestCategory.Voron)]
    public void GenerationRoundTripsThroughItsSerializedForm()
    {
        var generation = new JoinIndexGeneration(7, Guid.NewGuid(), Cursors(("Contexts", 10, 3), ("Archive/ü", 0, 99)), Guid.NewGuid(), Cursors(("Messages", 123456789012, 5)), 4242, 1234);

        JoinIndexGeneration read = JoinIndexGeneration.Deserialize(generation.Serialize());

        Assert.Equal(generation, read);
        Assert.Equal(7, read.Serial);
        Assert.Equal(generation.OuterEnvironmentId, read.OuterEnvironmentId);
        Assert.Equal(generation.InnerEnvironmentId, read.InnerEnvironmentId);
        Assert.Equal(4242, read.InnerWatermark);
        Assert.Equal(1234, read.PublishedAtTicks);
        Assert.Equal(new[] { "Archive/ü", "Contexts" }, read.Outer.Collections.Select(x => x.Collection));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void InitialGenerationRoundTrips()
    {
        Assert.Equal(JoinIndexGeneration.Initial, JoinIndexGeneration.Deserialize(JoinIndexGeneration.Initial.Serialize()));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void AnExpandedGenerationIsWrittenInTheFormat2WithTheBytesOfTheBuildsBeforeTheModes()
    {
        var generation = new JoinIndexGeneration(5, Guid.Empty, Cursors(("A", 1, 2)), Guid.Empty, JoinCursors.Empty, 9, 11, isPartial: true);

        byte[] bytes = generation.Serialize();

        // format, serial, the two environment ids, watermark, published, the cursors of both sides, one flag
        var expected = new List<byte>();
        expected.AddRange(BitConverter.GetBytes(2));
        expected.AddRange(BitConverter.GetBytes(5L));
        expected.AddRange(new byte[32]);
        expected.AddRange(BitConverter.GetBytes(9L));
        expected.AddRange(BitConverter.GetBytes(11L));
        expected.AddRange(BitConverter.GetBytes(1));
        expected.AddRange(BitConverter.GetBytes(1));
        expected.Add((byte)'A');
        expected.AddRange(BitConverter.GetBytes(1L));
        expected.AddRange(BitConverter.GetBytes(2L));
        expected.AddRange(BitConverter.GetBytes(0));
        expected.Add(1);
        Assert.Equal(expected.ToArray(), bytes);
        Assert.Equal(2, generation.Format);

        JoinIndexGeneration read = JoinIndexGeneration.Deserialize(bytes);
        Assert.Equal(JoinStorageMode.Expanded, read.Mode);
        Assert.True(read.IsPartial);
        Assert.True(read.InnerPartial);
        Assert.True(read.OuterPartial);
        Assert.Equal(generation, read);
    }

    [RavenTheory(RavenTestCategory.Voron)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnIndirectGenerationIsTheFormat3AndRoundTripsItsFlags(bool innerPartial, bool outerPartial)
    {
        var generation = new JoinIndexGeneration(7, Guid.NewGuid(), Cursors(("Contexts", 10, 3)), Guid.NewGuid(), Cursors(("Messages", 123456789012, 5)), 4242, 1234, JoinStorageMode.Indirect, innerPartial, outerPartial);

        byte[] bytes = generation.Serialize();
        JoinIndexGeneration read = JoinIndexGeneration.Deserialize(bytes);

        Assert.Equal(3, generation.Format);
        Assert.Equal(3, BitConverter.ToInt32(bytes));
        Assert.Equal(generation, read);
        Assert.Equal(JoinStorageMode.Indirect, read.Mode);
        Assert.Equal(innerPartial, read.InnerPartial);
        Assert.Equal(outerPartial, read.OuterPartial);
        Assert.Equal(innerPartial || outerPartial, read.IsPartial);
        Assert.Equal(7, JoinIndexGeneration.PeekSerial(bytes));
        Assert.NotEqual(generation, generation.WithSerial(8, 1235));
        Assert.Equal(generation.WithSerial(8, 1), JoinIndexGeneration.Deserialize(generation.WithSerial(8, 1).Serialize()));
        Assert.Equal(JoinStorageMode.Indirect, generation.WithSerial(8, 1).Mode);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void TheInitialGenerationOfAnIndirectFileIsTheFormat3()
    {
        JoinIndexGeneration initial = JoinIndexGeneration.InitialOf(JoinStorageMode.Indirect);

        Assert.Equal(3, initial.Format);
        Assert.Equal(JoinIndexGeneration.Initial, JoinIndexGeneration.InitialOf(JoinStorageMode.Expanded));
        Assert.Equal(initial, JoinIndexGeneration.Deserialize(initial.Serialize()));
        Assert.NotEqual(JoinIndexGeneration.Initial, initial);
    }

    // The reader of the builds before the modes: it accepts the format 2 and nothing else (JoinIndexGeneration.Deserialize of a27367394d). Its check, on the bytes of a generation of the new build.
    private static void DeserializeAsTheOldBuildDid(byte[] bytes)
    {
        int format = BitConverter.ToInt32(bytes);
        if (format != 2)
            throw new InvalidOperationException($"The generation has the format {format}, expected 2.");
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void TheFormatThatAnOlderBuildRefusesIsTheOneOfAnIndirectGenerationOnly()
    {
        DeserializeAsTheOldBuildDid(new JoinIndexGeneration(3, Guid.NewGuid(), Cursors(("A", 1, 1)), Guid.NewGuid(), Cursors(("B", 2, 2)), 1, 1).Serialize());

        Assert.Throws<InvalidOperationException>(() => DeserializeAsTheOldBuildDid(JoinIndexGeneration.InitialOf(JoinStorageMode.Indirect).Serialize()));
        Assert.Throws<InvalidOperationException>(() => DeserializeAsTheOldBuildDid(new JoinIndexGeneration(3, Guid.NewGuid(), Cursors(("A", 1, 1)), Guid.NewGuid(), Cursors(("B", 2, 2)), 1, 1, JoinStorageMode.Indirect, false, true).Serialize()));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void AnIndirectGenerationNeedsTheModeByteOfTheFormat3()
    {
        byte[] bytes = JoinIndexGeneration.InitialOf(JoinStorageMode.Indirect).Serialize();
        bytes[^1] = (byte)JoinStorageMode.Expanded;

        Assert.Throws<InvalidOperationException>(() => JoinIndexGeneration.Deserialize(bytes));
        Assert.Throws<ArgumentException>(() => new JoinIndexGeneration(1, Guid.Empty, JoinCursors.Empty, Guid.Empty, JoinCursors.Empty, 0, 0, JoinStorageMode.Expanded, true, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JoinIndexGeneration(1, Guid.Empty, JoinCursors.Empty, Guid.Empty, JoinCursors.Empty, 0, 0, JoinStorageMode.Auto, false, false));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void UnknownFormatIsRefused()
    {
        byte[] bytes = JoinIndexGeneration.Initial.Serialize();
        bytes[0] = 9;

        Assert.Throws<InvalidOperationException>(() => JoinIndexGeneration.Deserialize(bytes));
    }

    [RavenTheory(RavenTestCategory.Voron)]
    [InlineData(5, 5, 5, 5, GenerationComparison.Equal)]
    [InlineData(5, 5, 6, 5, GenerationComparison.Behind)]
    [InlineData(5, 5, 5, 6, GenerationComparison.Behind)]
    [InlineData(6, 5, 5, 5, GenerationComparison.Ahead)]
    [InlineData(5, 6, 5, 5, GenerationComparison.Ahead)]
    [InlineData(6, 5, 5, 6, GenerationComparison.Ahead)]
    public void CursorsAreComparedAsVectors(long document, long tombstone, long otherDocument, long otherTombstone, GenerationComparison expected)
    {
        JoinCursors mine = Cursors(("A", document, tombstone), ("B", 1, 1));
        JoinCursors theirs = Cursors(("a", otherDocument, otherTombstone), ("B", 1, 1));

        Assert.Equal(expected, mine.CompareTo(theirs));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void CollectionThatOneSideDoesNotHaveCountsAsZero()
    {
        JoinCursors empty = Cursors();
        JoinCursors some = Cursors(("A", 3, 0));

        Assert.Equal(GenerationComparison.Equal, empty.CompareTo(Cursors(("A", 0, 0))));
        Assert.Equal(GenerationComparison.Behind, empty.CompareTo(some));
        Assert.Equal(GenerationComparison.Ahead, some.CompareTo(empty));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void SourcesAreTheEnvironmentsOfBothIndexes()
    {
        Guid outer = Guid.NewGuid();
        Guid inner = Guid.NewGuid();
        var generation = new JoinIndexGeneration(1, outer, JoinCursors.Empty, inner, JoinCursors.Empty, 0, 0);

        Assert.True(generation.HasSameSources(outer, inner));
        Assert.False(generation.HasSameSources(inner, outer));
        Assert.False(generation.HasSameSources(outer, Guid.NewGuid()));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void PartialFlagRoundTrips()
    {
        var generation = new JoinIndexGeneration(3, Guid.NewGuid(), Cursors(("A", 4, 1)), Guid.NewGuid(), Cursors(("B", 9, 2)), 5, 6, isPartial: true);

        JoinIndexGeneration read = JoinIndexGeneration.Deserialize(generation.Serialize());

        Assert.True(read.IsPartial);
        Assert.Equal(generation, read);
        Assert.False(JoinIndexGeneration.Deserialize(generation.WithSerial(4, 7).Serialize()).Equals(new JoinIndexGeneration(4, generation.OuterEnvironmentId, generation.Outer, generation.InnerEnvironmentId, generation.Inner, 5, 7)));
        Assert.True(generation.WithSerial(4, 7).IsPartial);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void APartialGenerationIsBehindEvenWhenTheCursorsAreEqual()
    {
        Guid outer = Guid.NewGuid();
        Guid inner = Guid.NewGuid();
        var consumed = new Dictionary<string, (long Doc, long Tombstone)> { ["A"] = (10, 2) };
        var innerConsumed = new Dictionary<string, (long Doc, long Tombstone)> { ["B"] = (7, 1) };

        var exact = new JoinIndexGeneration(1, outer, Cursors(("A", 10, 2)), inner, Cursors(("B", 7, 1)), 0, 0);
        var partial = new JoinIndexGeneration(1, outer, Cursors(("A", 10, 2)), inner, Cursors(("B", 7, 1)), 0, 0, isPartial: true);
        var ahead = new JoinIndexGeneration(1, outer, Cursors(("A", 11, 2)), inner, Cursors(("B", 7, 1)), 0, 0, isPartial: true);

        Assert.Equal(JoinServing.Outcome.Equal, JoinServing.Compare(exact, outer, consumed, inner, innerConsumed));
        Assert.Equal(JoinServing.Outcome.Behind, JoinServing.Compare(partial, outer, consumed, inner, innerConsumed));
        Assert.Equal(JoinServing.Outcome.Ahead, JoinServing.Compare(ahead, outer, consumed, inner, innerConsumed));
        Assert.Equal(JoinServing.Outcome.OtherHistory, JoinServing.Compare(partial, inner, consumed, inner, innerConsumed));
    }
}
