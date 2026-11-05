using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// The where clause of the main query can use the fields of a JOIN alias
public class JoinWhereTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const string Main = "from index 'WPlacements' as p";
    private const string JoinC = "join (from index 'WCompanies') as c on p.CompanyId = id(c)";
    private const string JoinR = "join (from index 'WRegions') as r on c.RegionId = id(r)";

    private sealed class Placement { public string Id; public string Company; public string CompanyId; public string Status; public long Priority; public string Title; public long N; }
    private sealed class Company { public string Id; public string Name; public string Country; public long Size; public double Rating; public string Notes; public string Tag; public string RegionId; public Address Address; }
    private sealed class Address { public string City; }
    private sealed class Region { public string Id; public string Name; public long Level; }

    private sealed class PlacementsIndex : AbstractIndexCreationTask<Placement>
    {
        public override string IndexName => "WPlacements";
        public PlacementsIndex() { Map = docs => from d in docs select new { d.Company, d.CompanyId, d.Status, d.Priority, d.Title, d.N }; Index(x => x.Title, FieldIndexing.Search); }
    }

    private sealed class CompaniesIndex : AbstractIndexCreationTask<Company>
    {
        public override string IndexName => "WCompanies";
        public CompaniesIndex() { Map = docs => from d in docs select new { d.Name, d.Country, d.Size, d.Rating, d.Notes, d.Tag, d.RegionId, City = d.Address.City }; Index(x => x.Notes, FieldIndexing.Search); }
    }

    private sealed class RegionsIndex : AbstractIndexCreationTask<Region>
    {
        public override string IndexName => "WRegions";
        public RegionsIndex() { Map = docs => from d in docs select new { d.Name, d.Level }; }
    }

    private sealed class Data
    {
        public readonly Dictionary<string, Placement> Placements = new();
        public readonly Dictionary<string, Company> Companies = new();
        public readonly Dictionary<string, Region> Regions = new();

        public Company CompanyOf(Placement p) => p.CompanyId != null && Companies.TryGetValue(p.CompanyId, out var c) ? c : null;

        // the join of a chain: a company only joins when its region exists
        public Company ChainCompanyOf(Placement p) => CompanyOf(p) is { } c && c.RegionId != null && Regions.ContainsKey(c.RegionId) ? c : null;
        public Region RegionOf(Placement p) => ChainCompanyOf(p) is { } c ? Regions[c.RegionId] : null;
    }

    private IDocumentStore CreateStore(out Data data)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        var store = GetDocumentStore(options);
        new PlacementsIndex().Execute(store);
        new CompaniesIndex().Execute(store);
        new RegionsIndex().Execute(store);

        var random = new Random(11);
        string[] names = ["acme", "globex", "initech", "umbrella", "hooli"], countries = ["nl", "de", "fr"], words = ["fast", "cheap", "good", "slow"], statuses = ["open", "closed", "draft"];
        data = new Data();
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < 6; i++)
            {
                var region = new Region { Id = $"regions/{i}", Name = i % 2 == 0 ? "north" : "south", Level = i };
                data.Regions[region.Id] = region;
                bulk.Store(region, region.Id);
            }

            for (var i = 0; i < 40; i++)
            {
                var company = new Company
                {
                    Id = $"companies/{i}", Name = names[random.Next(names.Length)], Country = countries[random.Next(countries.Length)], Size = random.Next(10, 500), Rating = Math.Round(random.NextDouble() * 5, 1),
                    Notes = string.Join(' ', Enumerable.Range(0, random.Next(1, 3)).Select(_ => words[random.Next(words.Length)])), Tag = random.Next(3) == 0 ? null : "t" + random.Next(3),
                    RegionId = random.Next(8) switch { 0 => null, 1 => "regions/missing", _ => $"regions/{random.Next(6)}" }, Address = new Address { City = random.Next(2) == 0 ? "utrecht" : "berlin" }
                };
                data.Companies[company.Id] = company;
                bulk.Store(company, company.Id);
            }

            for (var i = 0; i < 160; i++)
            {
                var placement = new Placement
                {
                    Id = $"placements/{i}", N = i, Company = random.Next(2) == 0 ? "pizza" : "taco", Status = statuses[random.Next(3)], Priority = random.Next(0, 10), Title = random.Next(4) == 0 ? "urgent job" : "normal job",
                    CompanyId = random.Next(10) switch { 0 => null, 1 => "companies/missing", _ => $"companies/{random.Next(40)}" }
                };
                data.Placements[placement.Id] = placement;
                bulk.Store(placement, placement.Id);
            }
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private static string[] Ids(IDocumentStore store, string rql, params (string Name, object Value)[] parameters)
    {
        using var session = store.OpenSession();
        var query = session.Advanced.RawQuery<Placement>(rql);
        foreach (var (name, value) in parameters)
            query.AddParameter(name, value);

        return query.ToList().Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static void Check(IDocumentStore store, Data data, string where, Func<Placement, Company, bool> expected, string joins = JoinC, bool chain = false, params (string Name, object Value)[] parameters)
    {
        var rows = data.Placements.Values.Where(p => expected(p, chain ? data.ChainCompanyOf(p) : data.CompanyOf(p))).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var actual = Ids(store, $"{Main} {joins} where {where} {Everything}", parameters);
        Assert.True(rows.Length > 0 && rows.Length < data.Placements.Count, $"[{where}] the data does not tell the rows apart ({rows.Length} rows)");
        Assert.True(rows.SequenceEqual(actual), $"[{where}] expected {rows.Length} rows, got {actual.Length}: missing {string.Join(",", rows.Except(actual).Take(5))}, extra {string.Join(",", actual.Except(rows).Take(5))}");
    }

    private static bool C(Company c, Func<Company, bool> condition) => c != null && condition(c);
    private static bool Words(string text, params string[] any) => text.Split(' ').Intersect(any).Any();

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void PureAndOfOuterAndAliasConditions()
    {
        using var store = CreateStore(out var d);
        void Run(string where, Func<Placement, Company, bool> expected, params (string, object)[] parameters) => Check(store, d, where, expected, parameters: parameters);

        Run("p.Status = 'open' and c.Country = 'nl'", (p, c) => p.Status == "open" && C(c, x => x.Country == "nl"));
        Run("c.Country != 'nl' and p.Status = 'open'", (p, c) => p.Status == "open" && C(c, x => x.Country != "nl"));
        Run("c.Country in ('nl', 'de') and p.Priority > 3", (p, c) => p.Priority > 3 && C(c, x => x.Country is "nl" or "de"));
        Run("c.Size between 100 and 300 and p.Status = 'closed'", (p, c) => p.Status == "closed" && C(c, x => x.Size is >= 100 and <= 300));
        Run("c.Size > 400 or c.Rating < 1.5", (p, c) => C(c, x => x.Size > 400 || x.Rating < 1.5));
        Run("c.Size > 250 and c.Rating < 2.5", (p, c) => C(c, x => x.Size > 250 && x.Rating < 2.5));
        Run("startsWith(c.Name, 'ac') and p.Status != 'draft'", (p, c) => p.Status != "draft" && C(c, x => x.Name.StartsWith("ac")));
        Run("endsWith(c.Name, 'ex') and p.Priority <= 5", (p, c) => p.Priority <= 5 && C(c, x => x.Name.EndsWith("ex")));
        Run("search(c.Notes, 'fast cheap') and p.Priority >= 5", (p, c) => p.Priority >= 5 && C(c, x => Words(x.Notes, "fast", "cheap")));
        // exists() is true for a null value too: the plain query on the index is the oracle
        var withTag = Ids(store, "from index 'WCompanies' where exists(Tag)").Length;
        Assert.Equal(d.Companies.Count, withTag);
        Run("exists(c.Tag) and p.Status = 'open'", (p, c) => p.Status == "open" && c != null);
        Run("c.Name = $name and p.Priority < $prio", (p, c) => p.Priority < 6 && C(c, x => x.Name == "globex"), ("name", "globex"), ("prio", 6L));
        Run("search(p.Title, 'urgent') and c.Country = 'fr'", (p, c) => p.Title.Contains("urgent") && C(c, x => x.Country == "fr"));
        Run("c.Country = 'nl'", (p, c) => C(c, x => x.Country == "nl"));
        Run("id(c) in ('companies/3', 'companies/4', 'companies/5', 'companies/6', 'companies/7', 'companies/8', 'companies/9', 'companies/10') and p.Status = 'open'",
            (p, c) => p.Status == "open" && C(c, x => x.Id is "companies/3" or "companies/4" or "companies/5" or "companies/6" or "companies/7" or "companies/8" or "companies/9" or "companies/10"));
        Run("c.City = 'berlin' and p.Company = 'pizza'", (p, c) => p.Company == "pizza" && C(c, x => x.Address.City == "berlin"));
        Run("c.Country = 'nl' and c.Size > 100 and (p.Status = 'open' or p.Priority > 8)", (p, c) => p.Status == "open" | p.Priority > 8 ? C(c, x => x.Country == "nl" && x.Size > 100) : false);
        Run("p.Company = 'pizza' and c.Name = 'acme'", (p, c) => p.Company == "pizza" && C(c, x => x.Name == "acme"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrAndParentheses()
    {
        using var store = CreateStore(out var d);
        void Run(string where, Func<Placement, Company, bool> expected) => Check(store, d, where, expected);

        Run("p.Status = 'open' or c.Country = 'nl'", (p, c) => p.Status == "open" || C(c, x => x.Country == "nl"));
        Run("(p.Status = 'open' and p.Priority > 5) or (c.Country = 'nl' and c.Size > 100)", (p, c) => (p.Status == "open" && p.Priority > 5) || C(c, x => x.Country == "nl" && x.Size > 100));
        Run("c.Country = 'nl' and (p.Status = 'open' or c.Size > 300)", (p, c) => C(c, x => x.Country == "nl") && (p.Status == "open" || C(c, x => x.Size > 300)));
        Run("(p.Status = 'open' or c.Country = 'nl') and (p.Priority > 5 or c.Rating < 2)", (p, c) => (p.Status == "open" || C(c, x => x.Country == "nl")) && (p.Priority > 5 || C(c, x => x.Rating < 2)));
        Run("p.Status = 'open' or c.Name = 'acme' or c.Name = 'globex'", (p, c) => p.Status == "open" || C(c, x => x.Name is "acme" or "globex"));
        Run("p.Priority > 8 or (c.Country = 'nl' and c.Size > 100)", (p, c) => p.Priority > 8 || C(c, x => x.Country == "nl" && x.Size > 100));
        Run("p.Priority > 8 or search(c.Notes, 'fast') or c.Tag = 't1'", (p, c) => p.Priority > 8 || C(c, x => Words(x.Notes, "fast")) || C(c, x => x.Tag == "t1"));
        // matches(c) is the shorthand for c's own sub-query (here: any company)
        Run("matches(c) and c.Country = 'fr'", (p, c) => C(c, x => x.Country == "fr"));
        Run("p.Status = 'closed' or (matches(c) and c.Size > 450)", (p, c) => p.Status == "closed" || C(c, x => x.Size > 450));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void Negation()
    {
        using var store = CreateStore(out var d);
        void Run(string where, Func<Placement, Company, bool> expected) => Check(store, d, where, expected);

        // a placement without a company has no company that satisfies the condition
        Run("p.Priority >= 0 and not (c.Country = 'nl')", (p, c) => !C(c, x => x.Country == "nl"));
        Run("p.Status = 'open' and not (c.Size > 300)", (p, c) => p.Status == "open" && !C(c, x => x.Size > 300));
        Run("p.Priority >= 0 and not (c.Country = 'nl' or c.Size > 300)", (p, c) => !C(c, x => x.Country == "nl" || x.Size > 300));
        Run("p.Priority >= 0 and not (c.Country = 'nl') and not (c.Size > 300)", (p, c) => !C(c, x => x.Country == "nl") && !C(c, x => x.Size > 300));
        Run("p.Status = 'open' or not (c.Country = 'nl')", (p, c) => p.Status == "open" || !C(c, x => x.Country == "nl"));
        Run("p.Priority >= 0 and not (c.Country = 'nl' and p.Status = 'open')", (p, c) => !(C(c, x => x.Country == "nl") && p.Status == "open"));
        Run("c.Country = 'nl' and not (c.Size > 300)", (p, c) => C(c, x => x.Country == "nl") && !C(c, x => x.Size > 300));
        Run("p.Priority >= 0 and not (p.Status = 'open' or not (c.Country = 'nl'))", (p, c) => !(p.Status == "open" || !C(c, x => x.Country == "nl")));
        Run("p.Priority > 2 and not (matches(c))", (p, c) => p.Priority > 2 && c == null);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ChainedAliases()
    {
        using var store = CreateStore(out var d);
        void Run(string where, Func<Placement, Company, Region, bool> expected)
        {
            Check(store, d, where, (p, c) => expected(p, c, c == null ? null : d.Regions[c.RegionId]), $"{JoinC} {JoinR}", chain: true);
        }

        Run("r.Name = 'north' and p.Status = 'open'", (p, c, r) => p.Status == "open" && r != null && r.Name == "north");
        Run("r.Level > 2 and c.Country = 'nl'", (p, c, r) => c != null && c.Country == "nl" && r.Level > 2);
        Run("p.Status = 'open' or r.Level > 3", (p, c, r) => p.Status == "open" || (r != null && r.Level > 3));
        Run("c.Country = 'nl' and (p.Status = 'open' or r.Level > 3)", (p, c, r) => c != null && c.Country == "nl" && (p.Status == "open" || r.Level > 3));
        Run("c.Country = 'nl' or r.Name = 'south'", (p, c, r) => (c != null && c.Country == "nl") || (r != null && r.Name == "south"));
        Run("p.Priority > 7 or (c.Size > 300 and r.Level < 3)", (p, c, r) => p.Priority > 7 || (c != null && c.Size > 300 && r.Level < 3));
        Run("p.Priority >= 0 and not (r.Name = 'north')", (p, c, r) => !(r != null && r.Name == "north"));
        Run("p.Status = 'open' and not (r.Level > 2)", (p, c, r) => p.Status == "open" && !(r != null && r.Level > 2));
    }

    private sealed class Row { public string Title; public string CompanyName; public long N; }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByLeftJoinProjectionPagingAndFacets()
    {
        using var store = CreateStore(out var d);
        var left = "left join (from index 'WCompanies') as c on p.CompanyId = id(c)";

        // ORDER BY c.Field with conditions on c that are ANDed: the order of c, the rows of the conditions
        using (var session = store.OpenSession())
        {
            var rows = session.Advanced.RawQuery<Placement>($"{Main} {JoinC} where c.Country = 'nl' and p.Status = 'open' order by c.Size as long {Everything}").ToList();
            var expected = d.Placements.Values.Where(p => p.Status == "open" && C(d.CompanyOf(p), x => x.Country == "nl")).Select(p => p.Id).OrderBy(x => x).ToArray();
            Assert.Equal(expected, rows.Select(x => x.Id).OrderBy(x => x).ToArray());
            var sizes = rows.Select(x => d.CompanyOf(x).Size).ToList();
            Assert.Equal(sizes.OrderBy(x => x).ToList(), sizes);
        }

        // under an OR or a NOT the join of c only orders
        using (var session = store.OpenSession())
        {
            foreach (var (where, predicate) in new (string, Func<Placement, Company, bool>)[]
                     {
                         ("p.Status = 'open' or c.Country = 'nl'", (p, c) => p.Status == "open" || C(c, x => x.Country == "nl")),
                         ("p.Priority >= 0 and not (c.Country = 'nl')", (p, c) => !C(c, x => x.Country == "nl")),
                         ("p.Status = 'open' and (p.Priority > 5 or c.Country = 'nl')", (p, c) => p.Status == "open" && (p.Priority > 5 || C(c, x => x.Country == "nl")))
                     })
            {
                var rows = session.Advanced.RawQuery<Placement>($"{Main} {JoinC} where {where} order by c.Size as long {Everything}").ToList();
                var expected = d.Placements.Values.Where(p => predicate(p, d.CompanyOf(p))).Select(p => p.Id).OrderBy(x => x).ToArray();
                Assert.Equal(expected, rows.Select(x => x.Id).OrderBy(x => x).ToArray());
                var sizes = rows.Select(x => d.CompanyOf(x)?.Size ?? long.MaxValue).ToList();
                Assert.Equal(sizes.OrderBy(x => x).ToList(), sizes);
                Assert.Contains(sizes, x => x == long.MaxValue);
            }
        }

        // the order of a company of a chain under an OR is not supported: the message says so
        using (var session = store.OpenSession())
        {
            var exception = Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Placement>($"{Main} {JoinC} {JoinR} where p.Status = 'open' or r.Name = 'north' order by r.Level as long").ToList());
            Assert.Contains("chain of joins", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        // LEFT JOIN: a condition on c that is ANDed filters, under an OR the left join does not filter
        Check(store, d, "c.Country = 'nl' and p.Status = 'open'", (p, c) => p.Status == "open" && C(c, x => x.Country == "nl"), left);
        Check(store, d, "p.Status = 'open' or c.Country = 'nl'", (p, c) => p.Status == "open" || C(c, x => x.Country == "nl"), left);
        Check(store, d, "p.Priority >= 0 and not (c.Country = 'nl')", (p, c) => !C(c, x => x.Country == "nl"), left);
        Check(store, d, "p.Status = 'closed'", (p, c) => p.Status == "closed", left);

        // the projection: the alias is the joined document (null when the company is not one that the condition matched
        using (var session = store.OpenSession())
        {
            var rows = session.Advanced.RawQuery<Row>($"{Main} {JoinC} where c.Country = 'nl' and p.Status = 'open' select p.Title as Title, c.Name as CompanyName {Everything}").ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, x => Assert.NotNull(x.CompanyName));

            var or = session.Advanced.RawQuery<Placement>($"{Main} {JoinC} where p.Status = 'open' or c.Country = 'nl' {Everything}").ToList();
            var projected = session.Advanced.RawQuery<Row>($"{Main} {JoinC} where p.Status = 'open' or c.Country = 'nl' select p.N as N, c.Name as CompanyName {Everything}").ToList();
            Assert.Equal(or.Count, projected.Count);
            foreach (var row in projected)
            {
                var company = d.CompanyOf(d.Placements[$"placements/{row.N}"]);
                Assert.Equal(company is { Country: "nl" } ? company.Name : null, row.CompanyName);
            }
        }

        using (var session = store.OpenSession())
        {
            var expected = d.Placements.Values.Where(p => p.Status == "open" || C(d.CompanyOf(p), x => x.Country == "nl")).Select(p => p.Id).OrderBy(x => x).ToArray();
            var all = new List<string>();
            for (var start = 0; start < expected.Length; start += 20)
            {
                var page = session.Advanced.RawQuery<Placement>($"{Main} {JoinC} where p.Status = 'open' or c.Country = 'nl' order by p.N limit {start}, 20").Statistics(out var stats).ToList();
                Assert.Equal(expected.Length, stats.TotalResults);
                all.AddRange(page.Select(x => x.Id));
            }

            Assert.Equal(expected, all.OrderBy(x => x).ToArray());
        }

        using (var session = store.OpenSession())
        {
            var facets = session.Advanced.RawQuery<Placement>($"{Main} {JoinC} where c.Country = 'nl' and (p.Priority > 3 or c.Size > 300) select facet(Status)").ExecuteAggregation();
            var expected = d.Placements.Values.Where(p => C(d.CompanyOf(p), x => x.Country == "nl") && (p.Priority > 3 || C(d.CompanyOf(p), x => x.Size > 300))).GroupBy(p => p.Status).ToDictionary(x => x.Key, x => x.Count());
            Assert.Equal(expected, facets["Status"].Values.Where(x => x.Count > 0).ToDictionary(x => x.Range, x => (int)x.Count));
        }
    }

    private sealed class IdRow { public string N; public string CompanyId; public string CompanyName; }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void SelectIdOfJoinedDocument()
    {
        using var store = CreateStore(out var d);
        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<IdRow>($"{Main} {JoinC} where p.Status = 'open' select id() as N, id(c) as CompanyId, c.Name as CompanyName {Everything}").ToList();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var company = d.CompanyOf(d.Placements[row.N]);
            Assert.Equal(company.Id, row.CompanyId);
            Assert.Equal(company.Name, row.CompanyName);
        }

        var left = session.Advanced.RawQuery<IdRow>($"{Main} left join (from index 'WCompanies') as c on p.CompanyId = id(c) select id() as N, id(c) as CompanyId {Everything}").ToList();
        Assert.Equal(d.Placements.Count, left.Count);
        foreach (var row in left)
            Assert.Equal(d.CompanyOf(d.Placements[row.N])?.Id, row.CompanyId);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AliasConditionsScoreLikeTheSameConditionInTheSubQuery()
    {
        using var store = CreateStore(out _);

        Dictionary<string, double> Scores(string rql, params (string, object)[] parameters)
        {
            using var session = store.OpenSession();
            var query = session.Advanced.RawQuery<Placement>($"{rql} order by score() {Everything}");
            foreach (var (name, value) in parameters)
                query.AddParameter(name, value);

            return query.ToList().ToDictionary(x => x.Id, x => Convert.ToDouble(session.Advanced.GetMetadataFor(x)["@index-score"]));
        }

        void Same(Dictionary<string, double> expected, Dictionary<string, double> actual, string what)
        {
            Assert.True(expected.Count > 0 && expected.Count < 160, what);
            Assert.Equal(expected.Keys.OrderBy(x => x), actual.Keys.OrderBy(x => x));
            foreach (var (id, score) in expected)
                Assert.True(Math.Abs(score - actual[id]) < 1e-4, $"{what}: {id} scores {actual[id]}, expected {score}");
        }

        const string Plain = "from index 'WCompanies'";
        // the pure AND form is the condition in the sub-query
        Same(Scores($"{Main} join ({Plain} where search(Notes, 'fast cheap')) as c on p.CompanyId = id(c) where p.Status = 'open'"),
            Scores($"{Main} {JoinC} where search(c.Notes, 'fast cheap') and p.Status = 'open'"), "and");
        Same(Scores($"{Main} join ({Plain} where search(Notes, $w)) as c on p.CompanyId = id(c) where p.Status = 'open'", ("w", "good slow")),
            Scores($"{Main} {JoinC} where search(c.Notes, $w) and p.Status = 'open'", ("w", "good slow")), "and with a parameter");

        // under an OR it is matches() of a join with that sub-query
        Same(Scores($"{Main} join ({Plain} where search(Notes, 'fast')) as c on p.CompanyId = id(c) where p.Status = 'open' or matches(c)"),
            Scores($"{Main} {JoinC} where p.Status = 'open' or search(c.Notes, 'fast')"), "or");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void RejectedForms()
    {
        using var store = CreateStore(out _);

        void Rejected(string rql, string message)
        {
            using var session = store.OpenSession();
            var exception = Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Placement>(rql).ToList());
            Assert.Contains(message, exception.Message);
        }

        Rejected($"{Main} join (from index 'WCompanies') as c on p.Status = c.Country where c.Country = p.Title", "ambiguous");
        Rejected($"{Main} {JoinC} join (from index 'WRegions') as g on p.CompanyId = id(g) where c.Country = g.Name", "not joined through each other");
        Rejected($"{Main} join (from index 'WCompanies' where matches(c)) as c on p.CompanyId = id(c)", "cannot be used in the where clause of a JOIN sub-query");
    }
}
