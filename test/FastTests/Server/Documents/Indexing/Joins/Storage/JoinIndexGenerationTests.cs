using System;
using System.Collections.Generic;
using System.Linq;
using FastTests;
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
