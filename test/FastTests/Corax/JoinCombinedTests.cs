using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// The pieces of the JOIN work that only meet in the combined branch
public class JoinCombinedTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";
    private const int Users = 3_000;
    private const int Depts = 300;
    private const int Games = 6_000;
    private static readonly string[] Countries = ["nl", "de", "fr", "be", "uk"];

    private const string GamesFrom = "from index 'Games' as g";
    private const string UsersJoin = "join (from index 'Users' where Tag in ('t1', 't2', 't3')) as u on g.OwnerId = id(u)";
    private const string DeptsJoin = "join (from index 'Depts' where Tag in ('d9', 'd18', 'd27')) as d on g.DeptId = id(d)";

    private static readonly (string Name, string Rql)[] Variants =
    [
        ("id join, scores", $"{GamesFrom} join (from index 'Users' where search(Name, 'word3 word5 common')) as u on g.OwnerId = id(u) where search(g.Name, 'g1 g4 common') order by score() {Everything}"),
        ("id join, no scores", $"{GamesFrom} {UsersJoin} where g.Country = 'de' {Everything}"),
        ("id join, inner sort", $"{GamesFrom} {UsersJoin} order by u.Rank as long, g.Rank as long {Everything}"),
        ("id join, inner sort desc and country", $"{GamesFrom} {UsersJoin} order by g.Country, u.Tag desc, g.Rank as long {Everything}"),
        ("two joins, OR", $"{GamesFrom} {UsersJoin} {DeptsJoin} where matches(u) or matches(d) order by score() {Everything}"),
        ("two joins, OR and a clause", $"{GamesFrom} {UsersJoin} {DeptsJoin} where search(g.Name, 'g2 g7') and (matches(u) or matches(d)) order by score() {Everything}"),
        ("two joins, AND", $"{GamesFrom} {UsersJoin} {DeptsJoin} order by score() {Everything}"),
        ("field join, scores", $"{GamesFrom} join (from index 'Users' where Tag = 't5') as u on g.Postcode = u.Postcode order by score() {Everything}"),
        ("field join and id join", $"{GamesFrom} join (from index 'Users' where Tag = 't5') as u on g.Postcode = u.Postcode join (from index 'Depts' where Tag = 'd15') as d on g.DeptId = id(d) order by score() {Everything}"),
    ];

    private sealed record Row(string Id, double Score);

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinMatchesDoNotOutliveTheirTransaction()
    {
        using var store = Seed();

        var baseline = new Dictionary<string, List<Row>>();
        foreach (var (name, rql) in Variants)
        {
            var rows = Fetch(store, rql);
            Assert.True(rows.Count > 0, name);
            baseline[name] = rows;
        }

        CheckTheRowsAgainstTheData(baseline);

        // the memory of a finished transaction is given to the next ones
        for (var round = 0; round < 6; round++)
        {
            var round1 = round;
            var failures = new List<string>();
            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, worker =>
            {
                foreach (var (name, rql) in Variants.Skip((worker + round1) % Variants.Length).Concat(Variants.Take((worker + round1) % Variants.Length)))
                {
                    var rows = Fetch(store, rql);
                    if (SameRows(baseline[name], rows) == false)
                    {
                        lock (failures)
                            failures.Add($"round {round1}, worker {worker}, '{name}': {Describe(baseline[name], rows)}");
                    }
                }
            });

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AStreamKeepsItsJoinMatchesAliveWhileOtherQueriesRun()
    {
        using var store = Seed();
        var rql = $"{GamesFrom} {UsersJoin} {DeptsJoin} where matches(u) or matches(d) {Everything}";

        List<string> Stream()
        {
            using var session = store.OpenSession();
            var ids = new List<string>();
            using var stream = session.Advanced.Stream<Game>(session.Advanced.RawQuery<Game>(rql));
            while (stream.MoveNext())
                ids.Add(stream.Current.Id);
            return ids;
        }

        var expected = Stream();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Distinct().Count(), expected.Count);

        var baseline = Variants.ToDictionary(x => x.Name, x => Fetch(store, x.Rql));
        using (var session = store.OpenSession())
        {
            var ids = new List<string>();
            using var stream = session.Advanced.Stream<Game>(session.Advanced.RawQuery<Game>(rql));
            while (stream.MoveNext())
            {
                ids.Add(stream.Current.Id);
                if (ids.Count % 400 != 1)
                    continue;

                // other transactions come and go while this one is open
                foreach (var (name, other) in Variants)
                    Assert.True(SameRows(baseline[name], Fetch(store, other)), name);

                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            }

            Assert.Equal(expected, ids);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByAJoinedFieldNextToMatchesOfTheSameJoinIsRejected()
    {
        using var store = Seed();

        void Rejected(string rql, string alias)
        {
            using var session = store.OpenSession();
            var exception = Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Game>(rql).ToList());
            Assert.Contains($"cannot be combined with matches({alias})", exception.Message);
        }

        Rejected($"{GamesFrom} {UsersJoin} {DeptsJoin} where matches(u) or matches(d) order by u.Rank as long {Everything}", "u");
        Rejected($"{GamesFrom} {UsersJoin} {DeptsJoin} where matches(u) or matches(d) order by d.Rank as long {Everything}", "d");
        Rejected($"{GamesFrom} {UsersJoin} {DeptsJoin} where matches(d) or matches(u) order by g.Country, u.Rank as long {Everything}", "u");
        Rejected($"{GamesFrom} {UsersJoin} where matches(u) order by u.Rank as long {Everything}", "u");
        Rejected($"{GamesFrom} {UsersJoin} where g.Country = 'de' or matches(u) order by u.Rank as long {Everything}", "u");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByAJoinedFieldNextToMatchesOfAnotherJoinWorks()
    {
        using var store = Seed();
        var users = UsersWithTag("t1", "t2", "t3").ToDictionary(x => $"users/{x}", x => x);
        var depts = DeptsWithTag("d9", "d18", "d27").Select(x => $"depts/{x}").ToHashSet();

        // u is not mentioned in the where: an inner join, ranked; d is optional next to the country
        var rows = Fetch(store, $"{GamesFrom} {UsersJoin} {DeptsJoin} where g.Country = 'de' or matches(d) order by u.Rank as long, g.Rank as long {Everything}");
        var expected = GameData().Where(g => users.ContainsKey(g.OwnerId) && (g.Country == "de" || depts.Contains(g.DeptId))).ToList();
        Assert.Equal(expected.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal), rows.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));

        var byId = expected.ToDictionary(x => x.Id);
        var keys = rows.Select(x => (users[byId[x.Id].OwnerId], byId[x.Id].Rank)).ToList();
        Assert.Equal(keys.OrderBy(x => x.Item1).ThenBy(x => x.Rank).ToList(), keys);
    }

    private void CheckTheRowsAgainstTheData(Dictionary<string, List<Row>> baseline)
    {
        var users = UsersWithTag("t1", "t2", "t3").Select(x => $"users/{x}").ToHashSet();
        var depts = DeptsWithTag("d9", "d18", "d27").Select(x => $"depts/{x}").ToHashSet();
        var games = GameData();

        Ids("id join, no scores", games.Where(g => users.Contains(g.OwnerId) && g.Country == "de"));
        Ids("id join, inner sort", games.Where(g => users.Contains(g.OwnerId)));
        Ids("two joins, OR", games.Where(g => users.Contains(g.OwnerId) || depts.Contains(g.DeptId)));
        Ids("two joins, AND", games.Where(g => users.Contains(g.OwnerId) && depts.Contains(g.DeptId)));

        var postcodes = UsersWithTag("t5").Select(x => $"p{x % 100}").ToHashSet();
        Ids("field join, scores", games.Where(g => postcodes.Contains(g.Postcode)));

        void Ids(string name, IEnumerable<GameRow> expected) =>
            Assert.Equal(expected.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal), baseline[name].Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    private static bool SameRows(List<Row> expected, List<Row> actual)
    {
        if (expected.Count != actual.Count)
            return false;

        for (var i = 0; i < expected.Count; i++)
        {
            if (expected[i].Id != actual[i].Id || Math.Abs(expected[i].Score - actual[i].Score) > 1e-6)
                return false;
        }

        return true;
    }

    private static string Describe(List<Row> expected, List<Row> actual)
    {
        var index = 0;
        while (index < expected.Count && index < actual.Count && expected[index].Id == actual[index].Id && Math.Abs(expected[index].Score - actual[index].Score) <= 1e-6)
            index++;

        return $"{expected.Count} rows expected, {actual.Count} returned, first difference at {index}: " +
               $"{(index < expected.Count ? expected[index] : null)} vs {(index < actual.Count ? actual[index] : null)}";
    }

    // one session per query: a session hands the same tracked entity to every query that returns the same id
    private static List<Row> Fetch(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        var rows = new List<Row>();
        foreach (var entity in session.Advanced.RawQuery<Game>(rql).ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            rows.Add(new Row((string)metadata[Constants.Documents.Metadata.Id],
                metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : 0));
        }

        return rows;
    }

    private static IEnumerable<int> UsersWithTag(params string[] tags) => Enumerable.Range(0, Users).Where(i => tags.Contains($"t{i % 50}"));

    private static IEnumerable<int> DeptsWithTag(params string[] tags) => Enumerable.Range(0, Depts).Where(i => tags.Contains($"d{i % 30}"));

    private sealed record GameRow(string Id, string OwnerId, string DeptId, string Country, string Postcode, long Rank);

    private static List<GameRow> GameData() => Enumerable.Range(0, Games)
        .Select(i => new GameRow($"games/{i}", $"users/{i * 7 % Users}", $"depts/{i * 3 % Depts}", Countries[i % Countries.Length], $"p{i * 11 % 100}", i))
        .ToList();

    // all the tests only read: they share one database (see SharedJoinDatabase)
    private IDocumentStore Seed() => shared.Get("seed", Options.ForSearchEngine(RavenSearchEngineMode.Corax), o => GetDocumentStore(o), Populate);

    private void Populate(IDocumentStore store)
    {
        new UsersIndex().Execute(store);
        new DeptsIndex().Execute(store);
        new GamesIndex().Execute(store);

        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < Users; i++)
                bulk.Store(new User { Id = $"users/{i}", Name = $"word{i % 40} word{i % 7} common", Tag = $"t{i % 50}", Rank = i, Postcode = $"p{i % 100}" });
            for (var i = 0; i < Depts; i++)
                bulk.Store(new Dept { Id = $"depts/{i}", Name = $"dept{i % 30} common", Tag = $"d{i % 30}", Rank = i });
            foreach (var g in GameData())
                bulk.Store(new Game { Id = g.Id, Name = $"game g{g.Rank % 13} common", OwnerId = g.OwnerId, DeptId = g.DeptId, Country = g.Country, Postcode = g.Postcode, Rank = g.Rank });
        }

        Indexes.WaitForIndexing(store);
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
        public long Rank { get; set; }
        public string Postcode { get; set; }
    }

    private sealed class Dept
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
        public long Rank { get; set; }
    }

    private sealed class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string DeptId { get; set; }
        public string Country { get; set; }
        public string Postcode { get; set; }
        public long Rank { get; set; }
    }

    private sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Tag, u.Rank, u.Postcode };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private sealed class DeptsIndex : AbstractIndexCreationTask<Dept>
    {
        public DeptsIndex()
        {
            Map = depts => from d in depts select new { d.Name, d.Tag, d.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Depts";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.DeptId, g.Country, g.Postcode, g.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
