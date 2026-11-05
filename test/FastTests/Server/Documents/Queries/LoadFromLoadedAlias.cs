using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Queries;

public class LoadFromLoadedAlias(ITestOutputHelper output) : RavenTestBase(output)
{
    [RavenFact(RavenTestCategory.Querying)]
    public void SelectingAFieldOfAnAliasLoadedByAFieldOfAnotherLoadedAliasReadsItFromThatDocument()
    {
        using var store = GetDocumentStore();
        using (var session = store.OpenSession())
        {
            session.Store(new Company { Name = "acme" }, "companies/1");
            session.Store(new Person { Name = "bob", CompanyId = "companies/1" }, "people/1");
            session.Store(new Thing { Name = "bike", OwnerId = "people/1" }, "things/1");
            session.SaveChanges();
        }

        using (var session = store.OpenSession())
        {
            // 'c' is loaded by a field of 'p', not by a field of the document of the query: only 'c' is selected, 'p' is not
            var rows = session.Advanced.RawQuery<Row>(
                    "from Things as t load t.OwnerId as p, p.CompanyId as c select t.Name as Thing, c.Name as Company")
                .ToList();

            var row = Assert.Single(rows);
            Assert.Equal("bike", row.Thing);
            Assert.Equal("acme", row.Company);
        }
    }

    private class Company
    {
        public string Name { get; set; }
    }

    private class Person
    {
        public string Name { get; set; }
        public string CompanyId { get; set; }
    }

    private class Thing
    {
        public string Name { get; set; }
        public string OwnerId { get; set; }
    }

    private class Row
    {
        public string Thing { get; set; }
        public string Company { get; set; }
    }
}
