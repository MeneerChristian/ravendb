using System.Linq;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Parser;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Queries.Parser
{
    public class JoinClauseParsing : NoDisposalNeeded
    {
        public JoinClauseParsing(ITestOutputHelper output) : base(output)
        {
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void JoinIsParsedIntoOuterQueryWithInnerQuery()
        {
            var query = Parse("FROM INDEX 'Games/Search' AS g " +
                              "JOIN (FROM INDEX 'UsersIndex' WHERE search(Username, 'chris') SELECT id()) AS u ON g.OwnerId = id(u) " +
                              "WHERE search(g.Name, 'zelda') ORDER BY score() DESC");

            Assert.NotNull(query.Joins);
            Assert.Equal("u", query.Joins[0].Alias.Value);
            Assert.Equal("UsersIndex", query.Joins[0].Query.From.From.FieldValue);
            Assert.True(query.Joins[0].Query.From.Index);
            Assert.NotNull(query.Joins[0].Query.Where);
            Assert.NotNull(query.Joins[0].Query.Select);

            Assert.Equal(OperatorType.Equal, query.Joins[0].On.Operator);
            Assert.IsType<FieldExpression>(query.Joins[0].On.Left);
            Assert.IsType<MethodExpression>(query.Joins[0].On.Right);

            // the outer query keeps its own clauses
            Assert.Equal("Games/Search", query.From.From.FieldValue);
            Assert.Equal("g", query.From.Alias.Value.Value);
            Assert.NotNull(query.Where);
            Assert.NotNull(query.OrderBy);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void JoinRequiresAnAliasForTheMainDocument()
        {
            var e = Assert.ThrowsAny<System.Exception>(() => Parse("FROM Games JOIN (FROM Users WHERE Name = 'a') AS u ON OwnerId = id(u)"));

            Assert.Contains("requires an alias for the main document", e.Message);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void LeftJoinWithoutMainAliasIsRejectedInsteadOfBecomingAnInnerJoin()
        {
            var e = Assert.ThrowsAny<System.Exception>(() => Parse("FROM INDEX 'Games/Search' LEFT JOIN (FROM INDEX 'UsersIndex' WHERE Name = 'a') AS u ON OwnerId = id(u)"));

            Assert.Contains("requires an alias for the main document", e.Message);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void QueryWithoutJoinHasNoJoinClause()
        {
            var query = Parse("FROM Games AS g WHERE g.Name = 'zelda'");

            Assert.Null(query.Joins);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void ToStringRoundTripsTheJoin()
        {
            var query = Parse("FROM INDEX 'Games/Search' AS g JOIN (FROM INDEX 'UsersIndex' WHERE Name = 'a' SELECT id()) AS u ON g.OwnerId = id(u) WHERE g.Name = 'z'");

            var reparsed = Parse(query.ToString());

            Assert.NotNull(reparsed.Joins);
            Assert.Equal("u", reparsed.Joins[0].Alias.Value);
            Assert.Equal("UsersIndex", reparsed.Joins[0].Query.From.From.FieldValue);
            Assert.NotNull(reparsed.Where);
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [InlineData("FROM Games AS g JOIN FROM Users AS u ON g.OwnerId = id(u)")]                              // no parentheses
        [InlineData("FROM Games AS g JOIN (FROM Users SELECT id() ON g.OwnerId = id(u)")]                      // missing ')'
        [InlineData("FROM Games AS g JOIN (FROM Users SELECT id()) ON g.OwnerId = id(u)")]                     // no alias
        [InlineData("FROM Games AS g JOIN (FROM Users SELECT id()) AS u")]                                    // no ON
        [InlineData("FROM Games AS g JOIN (FROM Users SELECT id()) AS u ON g.OwnerId > id(u)")]                // not an equality
        [InlineData("FROM Games AS g JOIN (FROM Users SELECT id()) AS u ON g.OwnerId = id(u) AND X = 1")]      // more than one condition
        [InlineData("FROM Games AS g JOIN (FROM Users SELECT id()) AS u ON g.OwnerId = 5")]                     // not the id
        [InlineData("FROM Games AS g JOIN (FROM Users SELECT id()) AS u ON g.OwnerId = id(x)")]                 // id of another alias
        [InlineData("FROM Games AS g JOIN (FROM Users SELECT id()) AS u ON g.OwnerId = u.id()")]                // dotted form is not accepted
        public void InvalidJoinIsRejected(string q)
        {
            Assert.ThrowsAny<System.Exception>(() => Parse(q));
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void SeveralJoinsOfTheMainDocumentAreParsedInOrder()
        {
            var query = Parse("FROM INDEX 'Games' AS g " +
                              "JOIN (FROM INDEX 'Users' WHERE search(Name, 'a')) AS u ON g.OwnerId = id(u) " +
                              "JOIN (FROM INDEX 'Publishers' WHERE search(Name, 'b')) AS p ON g.PublisherId = id(p) " +
                              "WHERE search(g.Name, 'zelda')");

            Assert.Equal(new[] { "u", "p" }, query.Joins.Select(x => x.Alias.Value));
            Assert.Equal(new[] { "Users", "Publishers" }, query.Joins.Select(x => x.Query.From.From.FieldValue));
            Assert.All(query.Joins, join => Assert.Null(join.Query.Joins));
            Assert.Equal(query.Joins, query.JoinChain);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void AJoinOnAJoinedDocumentIsMovedIntoTheSubQueryOfThatDocument()
        {
            var query = Parse("FROM INDEX 'Games' AS g " +
                              "JOIN (FROM INDEX 'Users' WHERE search(Name, 'a')) AS u ON g.OwnerId = id(u) " +
                              "JOIN (FROM INDEX 'Companies' WHERE search(Name, 'b')) AS c ON u.CompanyId = id(c)");

            // the main document is only joined with u, u's sub-query is the one joined with c
            var u = Assert.Single(query.Joins);
            Assert.Equal("u", u.Alias.Value);
            Assert.Equal("u", u.Query.From.Alias.Value.Value);
            var c = Assert.Single(u.Query.Joins);
            Assert.Equal("c", c.Alias.Value);
            Assert.Equal("Companies", c.Query.From.From.FieldValue);
            Assert.Equal("u.CompanyId", ((FieldExpression)c.On.Left).FieldValue);

            // the written order is kept for loading the documents
            Assert.Equal(new[] { "u", "c" }, query.JoinChain.Select(x => x.Alias.Value));
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void ChainsOfSeveralLevelsAndSeveralChildrenAreNested()
        {
            var query = Parse("FROM INDEX 'Games' AS g " +
                              "JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u) " +
                              "JOIN (FROM INDEX 'Companies') AS c ON u.CompanyId = id(c) " +
                              "JOIN (FROM INDEX 'Countries') AS k ON c.CountryId = id(k) " +
                              "JOIN (FROM INDEX 'Regions') AS r ON u.RegionId = id(r)");

            var u = Assert.Single(query.Joins);
            Assert.Equal(new[] { "c", "r" }, u.Query.Joins.Select(x => x.Alias.Value));
            var c = u.Query.Joins.Single(x => x.Alias.Value == "c");
            Assert.Equal("k", Assert.Single(c.Query.Joins).Alias.Value);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void NestedJoinSyntaxIsTheSameAsAChain()
        {
            var query = Parse("FROM INDEX 'Games' AS g " +
                              "JOIN (FROM INDEX 'Users' AS u JOIN (FROM INDEX 'Companies') AS c ON u.CompanyId = id(c)) AS u ON g.OwnerId = id(u)");

            var u = Assert.Single(query.Joins);
            Assert.Equal("c", Assert.Single(u.Query.Joins).Alias.Value);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void ToStringRoundTripsSeveralAndChainedJoins()
        {
            var query = Parse("FROM INDEX 'Games' AS g " +
                              "JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u) " +
                              "JOIN (FROM INDEX 'Companies') AS c ON u.CompanyId = id(c) " +
                              "JOIN (FROM INDEX 'Publishers') AS p ON g.PublisherId = id(p)");

            var reparsed = Parse(query.ToString());

            Assert.Equal(new[] { "u", "p" }, reparsed.Joins.Select(x => x.Alias.Value));
            Assert.Equal("c", Assert.Single(reparsed.Joins[0].Query.Joins).Alias.Value);
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u) JOIN (FROM INDEX 'Users') AS u ON g.OtherId = id(u)")] // the same alias twice
        [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS g ON g.OwnerId = id(g)")]                                                   // the alias of the main document
        [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users' AS x) AS u ON g.OwnerId = id(u) JOIN (FROM INDEX 'Companies') AS c ON u.CompanyId = id(c)")] // sub-query alias x is not u
        public void InvalidSeveralJoinsAreRejected(string q)
        {
            Assert.ThrowsAny<System.Exception>(() => Parse(q));
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void AJoinCanMatchOnAFieldOfTheJoinedDocuments()
        {
            var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.Country = u.Country");

            var join = Assert.Single(query.Joins);
            Assert.Equal("Country", join.InnerField);
            Assert.Equal("g.Country", ((FieldExpression)join.On.Left).FieldValue);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void TheFieldOfTheJoinedDocumentsCanBeWrittenFirstAndIsNormalized()
        {
            var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON u.Address.City = g.City");

            var join = Assert.Single(query.Joins);
            Assert.Equal("Address.City", join.InnerField);
            Assert.Equal("g.City", ((FieldExpression)join.On.Left).FieldValue); // the main document's field is always on the left
            Assert.Equal("u.Address.City", ((FieldExpression)join.On.Right).FieldValue);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void AJoinOnTheIdHasNoJoinedField()
        {
            Assert.Null(Assert.Single(Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u)").Joins).InnerField);
            Assert.Null(Assert.Single(Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON id(u) = g.OwnerId").Joins).InnerField);
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [InlineData("id(g) = u.Address.City")]
        [InlineData("u.Address.City = id(g)")]
        public void TheIdOfTheMainDocumentCanBeMatchedAgainstAFieldOfTheJoinedDocumentsInEitherOrder(string on)
        {
            var join = Assert.Single(Parse($"FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON {on}").Joins);

            Assert.Equal("Address.City", join.InnerField);
            Assert.Equal("id", Assert.IsType<MethodExpression>(join.On.Left).Name.Value); // the id of the main document is always on the left
            Assert.Equal("u.Address.City", ((FieldExpression)join.On.Right).FieldValue);
            Assert.False(join.IsComposite);
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [InlineData("id(g) = id(u)")]
        [InlineData("id(u) = id(g)")]
        [InlineData("id(u) = u.Name")]
        [InlineData("id(x) = u.Name")]
        [InlineData("id() = u.Name")]
        [InlineData("id(g) = u.Name AND g.A = u.B")]
        [InlineData("g.A = u.B AND id(g) = u.Name")]
        public void TheIdOfTheMainDocumentOnlyMatchesAFieldOfTheJoinedDocumentsAndNeverInAComposite(string on)
        {
            Assert.ThrowsAny<System.Exception>(() => Parse($"FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON {on}"));
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void AFieldJoinInAChainIsNestedLikeAnyOther()
        {
            var query = Parse("FROM INDEX 'Games' AS g " +
                              "JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u) " +
                              "JOIN (FROM INDEX 'Companies') AS c ON u.CompanyCode = c.Code");

            var u = Assert.Single(query.Joins);
            var c = Assert.Single(u.Query.Joins);
            Assert.Equal("Code", c.InnerField);
            Assert.Equal("u.CompanyCode", ((FieldExpression)c.On.Left).FieldValue);
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.Country = g.Other")]    // nothing of the joined documents
        [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON u.Country = u.Other")]    // nothing of the main document
        [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.Country > u.Country")]  // not an equality
        [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON u.Country = 5")]          // not a field of the main document
        [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON id(u) = id(u)")]          // two ids
        public void InvalidFieldJoinsAreRejected(string q)
        {
            Assert.ThrowsAny<System.Exception>(() => Parse(q));
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void LeftJoinIsParsedAndPlainJoinStaysInner()
        {
            var query = Parse("FROM INDEX 'Games' AS g " +
                              "LEFT JOIN (FROM INDEX 'Users' WHERE search(Name, 'a')) AS u ON g.OwnerId = id(u) " +
                              "JOIN (FROM INDEX 'Publishers') AS p ON g.PublisherId = id(p) " +
                              "left join (FROM INDEX 'Depts') AS d ON g.Country = d.Country " +
                              "WHERE search(g.Name, 'zelda') ORDER BY score()");

            Assert.Equal(new[] { true, false, true }, query.Joins.Select(x => x.IsLeft));
            Assert.Equal(new[] { "u", "p", "d" }, query.Joins.Select(x => x.Alias.Value));
            Assert.NotNull(query.Where);
            Assert.NotNull(query.OrderBy);
            Assert.True(query.Joins[0].Filters == false);
            Assert.True(query.Joins[1].Filters);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void LeftJoinAndBoostRoundTripThroughToString()
        {
            var query = Parse("FROM INDEX 'Games' AS g LEFT JOIN (FROM INDEX 'Users' WHERE Name = 'a') AS u ON g.OwnerId = id(u) BOOST 0.5 " +
                              "JOIN (FROM INDEX 'Depts') AS d ON g.DeptId = id(d) boost $b WHERE g.Name = 'z'");

            var text = query.ToString();
            Assert.Contains("LEFT JOIN (", text);
            var reparsed = Parse(text);

            Assert.Equal(new[] { true, false }, reparsed.Joins.Select(x => x.IsLeft));
            Assert.Equal("0.5", reparsed.Joins[0].Boost.Token.Value);
            Assert.Equal(ValueTokenType.Double, reparsed.Joins[0].Boost.Value);
            Assert.Equal(ValueTokenType.Parameter, reparsed.Joins[1].Boost.Value);
            Assert.Equal("b", reparsed.Joins[1].Boost.Token.Value);
            Assert.NotNull(reparsed.Where);
        }

        [RavenFact(RavenTestCategory.Querying)]
        public void JoinWithoutBoostHasNoBoost()
        {
            Assert.Null(Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u)").Joins[0].Boost);
            Assert.Equal("0", Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.Country = u.Country BOOST 0 WHERE g.Name = 'a'").Joins[0].Boost.Token.Value);
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [InlineData("JOIN", "LEFT JOIN")]
        [InlineData("LEFT JOIN", "JOIN")]
        [InlineData("LEFT JOIN", "LEFT JOIN")]
        public void LeftJoinsCanBePartOfAChain(string outer, string inner)
        {
            var query = Parse($"FROM INDEX 'Games' AS g {outer} (FROM INDEX 'Users') AS u ON g.OwnerId = id(u) {inner} (FROM INDEX 'Depts') AS d ON u.DeptId = id(d)");

            var root = Assert.Single(query.Joins);
            Assert.Equal(outer == "LEFT JOIN", root.IsLeft);
            var nested = Assert.Single(root.Query.Joins);
            Assert.Equal(inner == "LEFT JOIN", nested.IsLeft);
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [InlineData("FROM INDEX 'Games' AS g LEFT (FROM INDEX 'Users') AS u ON g.OwnerId = id(u)")]                          // LEFT without JOIN
        [InlineData("FROM INDEX 'Games' AS g LEFT JOIN FROM INDEX 'Users' AS u ON g.OwnerId = id(u)")]                       // no parentheses
        [InlineData("FROM INDEX 'Games' AS g LEFT JOIN (FROM INDEX 'Users') AS u")]                                          // no ON
        [InlineData("FROM INDEX 'Games' AS g LEFT JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u) BOOST")]               // no boost value
        [InlineData("FROM INDEX 'Games' AS g LEFT JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u) BOOST WHERE a = 1")]   // no boost value
        [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u) LEFT JOIN (FROM INDEX 'Users') AS u ON g.Other = id(u)")] // alias used twice
        public void InvalidLeftJoinsAreRejected(string q)
        {
            Assert.ThrowsAny<System.Exception>(() => Parse(q));
        }

        private static Query Parse(string q)
        {
            var parser = new QueryParser();
            parser.Init(q);
            return parser.Parse();
        }
    }
}
