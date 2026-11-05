using System;
using Raven.Client.Exceptions;
using Raven.Server.Documents.Queries;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Queries
{
    public class JoinMetadataTests : NoDisposalNeeded
    {
        public JoinMetadataTests(ITestOutputHelper output) : base(output)
        {
        }

        private static QueryMetadata Metadata(string rql) => new QueryMetadata(rql, null, 0);

        [RavenFact(RavenTestCategory.Querying)]
        public void AJavaScriptProjectionThatDoesNotUseTheAliasDoesNotLoadTheJoinedDocument()
        {
            // 'm' is a letter of the words of the function text, but not an identifier of it
            var metadata = Metadata("from index 'Games' as g join (from index 'Users') as m on g.OwnerId = id(m) select { Name: g.Name }");

            Assert.Null(metadata.Query.Load);
            Assert.False(metadata.Query.Joins[0].ProjectsDocument);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void AJavaScriptProjectionThatUsesTheAliasLoadsTheJoinedDocument()
        {
            var metadata = Metadata("from index 'Games' as g join (from index 'Users') as m on g.OwnerId = id(m) select { Owner: m.Name }");

            Assert.NotNull(metadata.Query.Load);
            Assert.True(metadata.Query.Joins[0].ProjectsDocument);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void APropertyThatIsNamedLikeTheAliasIsNotTheAlias()
        {
            var metadata = Metadata("from index 'Games' as g join (from index 'Users') as m on g.OwnerId = id(m) select { Owner: g.m }");

            Assert.Null(metadata.Query.Load);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void WhereOnTheJoinAliasIsMovedToTheSubQuery()
        {
            var metadata = Metadata("from index 'Games' as g join (from index 'Users' where Age > 3) as u on g.OwnerId = id(u) where g.Name = 'a' and u.Name = 'b'");

            Assert.Contains("g.Name", metadata.Query.Where.ToString());
            Assert.DoesNotContain("u.Name", metadata.Query.Where.ToString());
            var join = Assert.Single(metadata.Query.Joins);
            Assert.False(join.ReferencedInWhere);
            Assert.Contains("<Field>: Name Equal", join.Query.Where.ToString());
            Assert.Contains("<Field>: Age GreaterThan", join.Query.Where.ToString());
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void WhereOnTheJoinAliasUnderOrBecomesAVirtualJoin()
        {
            var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) where g.Name = 'a' or u.Name = 'b'");

            var join = Assert.Single(metadata.Query.Joins);
            Assert.True(join.ReferencedInWhere);
            Assert.Equal("u", join.RootAlias.ToString());
            Assert.StartsWith("u#", join.Alias.ToString());
            Assert.Contains("matches(<Field>: " + join.Alias, metadata.Query.Where.ToString());
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void ConditionsThatCompareTheMainDocumentWithAJoinOnAFieldAreRejected()
        {
            var e = Assert.Throws<InvalidQueryException>(() => Metadata("from index 'Games' as g join (from index 'Users') as u on g.Name = u.Name where g.Name = u.Name"));
            Assert.Contains("ambiguous", e.Message);

            // a join on the id: the comparison is a virtual join
            var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) where g.Name = u.Name");
            Assert.Contains(metadata.Query.Joins, x => x.Compare != null);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void WhereOfAQueryWithoutJoinsIsNotChecked()
        {
            var e = Assert.Throws<InvalidQueryException>(() => Metadata("from index 'Games' as g where g.Name = 'a' and u.Name = 'b'"));
            Assert.Contains("Unknown alias", e.Message);
            Assert.DoesNotContain("JOIN", e.Message);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void WhereOnTheMainAliasAndMatchesIsStillAccepted()
        {
            Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) where g.Name = 'a' and matches(u)");
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void JoinInACollectionQueryIsRejectedWithAnAccurateMessage()
        {
            var e = Assert.Throws<InvalidQueryException>(() => Metadata("from Games as g join (from index 'Users') as u on g.OwnerId = id(u)"));

            Assert.Contains("FROM INDEX", e.Message);
            Assert.DoesNotContain("patch", e.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
