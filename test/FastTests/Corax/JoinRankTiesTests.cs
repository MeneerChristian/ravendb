using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Corax.Querying;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Indexes;
using Tests.Infrastructure;
using Xunit;
using JoinFieldScan = Raven.Server.Documents.Indexes.Persistence.Corax.CoraxIndexReadOperation.JoinFieldScan;

namespace FastTests.Corax;

// The ranks of a JOIN on the id that orders by a field that holds several terms per entry
public class JoinRankTiesTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const string Where = "where Tag in ('t1', 't2', 't3', 't4', 't5', 't6')";
    private const int Vocabulary = 60;

    private static readonly string[] Words = Enumerable.Range(0, Vocabulary).Select(i => "w" + (i * 7 % Vocabulary).ToString("D2")).ToArray();

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task TheRanksFollowThePlainOrderForEveryBatchingScanAndLookupStrategyOnFourThreads()
    {
        using var store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);

        var users = UserData();
        var random = new Random(5);
        // the batches of the indexing differ in their size and in what the index holds before them
        for (var start = 0; start < users.Count;)
        {
            var size = 1 + random.Next(120);
            Store(store, users.Skip(start).Take(size));
            start += size;
            if (random.Next(3) == 0)
                Indexes.WaitForIndexing(store);
        }

        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < 4_000; i++)
                bulk.Store(new Game { Id = $"rankgames/{i}", Owner = users[random.Next(users.Count)].Id });
        }

        Indexes.WaitForIndexing(store);

        var database = await Databases.GetDocumentDatabaseInstanceFor(store);
        var termsOfId = users.ToDictionary(x => x.Id.ToLowerInvariant(), x => x.Name.Split(' ').ToArray());
        var strategies = new[] { JoinKeyLookupStrategy.PerKey, JoinKeyLookupStrategy.Cursor, JoinKeyLookupStrategy.Sweep, JoinKeyLookupStrategy.Auto };

        foreach (var orderBy in new[] { "order by Name", "order by Name desc", "order by Tag, Name", "order by Name, Tag desc" })
        {
            var rql = $"from index 'RankUsers' {Where} {orderBy} {Everything}";
            var plain = PlainOrder(store, rql);
            Assert.True(plain.Count > 500, rql);

            Dictionary<string, float> first;
            using (var session = new JoinFieldScanTests.ScanSession(database, "RankUsers", rql))
            using (var ranks = session.Reader.ScanIdsAndRanks(session.Query, session.DocumentsContext, CancellationToken.None))
            {
                first = RanksByKey(ranks);
                AssertRanksFollowThePlainOrder(rql, plain, first, orderBy.Contains("Tag") ? null : termsOfId);
                Assert.True(first.Values.Distinct().Count() > 1);
            }

            // concurrent readers, many repetitions
            var scanTasks = Enumerable.Range(0, 4).Select(thread => Task.Run(() =>
            {
                JoinKeyLookupSettings.Forced = strategies[thread];
                try
                {
                    for (var rep = 0; rep < 60; rep++)
                    {
                        using var session = new JoinFieldScanTests.ScanSession(database, "RankUsers", rql);
                        using var ranks = session.Reader.ScanIdsAndRanks(session.Query, session.DocumentsContext, CancellationToken.None);
                        var again = RanksByKey(ranks);
                        Assert.True(first.Count == again.Count && first.All(x => again.TryGetValue(x.Key, out var rank) && rank == x.Value), $"[{rql}] thread {thread}, rep {rep}: the ranks changed");

                        if (rep % 10 != 0)
                            continue;

                        var terms = new List<HashSet<string>>();
                        foreach (var strategy in new[] { JoinFieldScan.Auto, JoinFieldScan.EntryTerms, JoinFieldScan.TermWalk, JoinFieldScan.BulkMap })
                        {
                            using var scores = session.Reader.ScanTermScores(session.Query, session.DocumentsContext, "Name", strategy, false, CancellationToken.None);
                            terms.Add(Enumerable.Range(0, scores.Count).Select(i => Encoding.UTF8.GetString(scores.Term(i))).ToHashSet());
                        }

                        Assert.All(terms, x => Assert.True(x.SetEquals(terms[0]), $"[{rql}] thread {thread}: a scan strategy found other terms"));
                    }
                }
                finally
                {
                    JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.Auto;
                }
            })).ToArray();

            // the join itself, under every lookup strategy: the games come in the order of the ranks of their owners
            var joinRql = $"from index 'RankGames' as g join (from index 'RankUsers' {Where}) as u on g.Owner = id(u) {orderBy.Replace("Name", "u.Name").Replace("Tag", "u.Tag")} {Everything}";
            var joinTasks = Enumerable.Range(0, 4).Select(thread => Task.Run(() =>
            {
                JoinKeyLookupSettings.Forced = strategies[thread];
                try
                {
                    for (var rep = 0; rep < 6; rep++)
                    {
                        using var session = store.OpenSession();
                        var owners = session.Advanced.RawQuery<Game>(joinRql).ToList().Select(x => x.Owner.ToLowerInvariant()).ToList();
                        Assert.NotEmpty(owners);
                        var previous = float.MinValue;
                        foreach (var owner in owners)
                        {
                            Assert.True(first.TryGetValue(owner, out var rank), $"[{joinRql}] owner {owner} has no rank");
                            Assert.True(rank >= previous, $"[{joinRql}] thread {thread}, rep {rep}: the games are not in the order of the ranks of their owners ({previous} then {rank})");
                            previous = rank;
                        }
                    }
                }
                finally
                {
                    JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.Auto;
                }
            })).ToArray();

            await Task.WhenAll(scanTasks.Concat(joinTasks));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task TheRankTiesOfAMultiTermFieldDependOnTheIndexingBatchesNotOnTheSmallestTermAlone()
    {
        var users = UserData();
        var termsOfId = users.ToDictionary(x => x.Id.ToLowerInvariant(), x => x.Name.Split(' ').ToArray());
        var smallestWords = users.Select(x => x.Name.Split(' ').Min(StringComparer.Ordinal)).Distinct().Count();
        var results = new List<string>();

        foreach (var (variant, chunk) in new[] { ("one batch", 0), ("batches of 40", 40), ("batches of 7", 7) })
        foreach (var rql in new[] { $"from index 'RankUsers' order by Name {Everything}", $"from index 'RankUsers' {Where} order by Name {Everything}" })
        {
            using var store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
            new UsersIndex().Execute(store);
            var oneBatch = false;
            if (chunk == 0)
            {
                // indexing is stopped while the documents are stored, so that the load of the machine does not cut them into several batches
                store.Maintenance.Send(new StopIndexingOperation());
                using (var bulk = store.BulkInsert())
                {
                    foreach (var user in users)
                        bulk.Store(user, user.Id);
                }

                JoinTestHelpers.StartIndexingAndWait(this, store);
                oneBatch = JoinTestHelpers.IndexedInOneBatch(store, "RankUsers");
            }
            else
            {
                // the index has indexed every batch before the next one is stored: the batches are these chunks
                for (var start = 0; start < users.Count; start += chunk)
                {
                    Store(store, users.Skip(start).Take(chunk));
                    Indexes.WaitForIndexing(store);
                }
            }

            Indexes.WaitForIndexing(store);
            var database = await Databases.GetDocumentDatabaseInstanceFor(store);
            var plain = PlainOrder(store, rql);
            using var session = new JoinFieldScanTests.ScanSession(database, "RankUsers", rql);
            using var ranks = session.Reader.ScanIdsAndRanks(session.Query, session.DocumentsContext, CancellationToken.None);
            var byKey = RanksByKey(ranks);
            var filtered = rql.Contains("where");
            AssertRanksFollowThePlainOrder($"{variant}, {rql}", plain, byKey, filtered ? termsOfId : null);

            var distinct = byKey.Values.Distinct().Count();
            var runsOfTheSmallestWord = 1 + Enumerable.Range(1, plain.Count - 1).Count(i => SmallestWord(termsOfId[plain[i]]) != SmallestWord(termsOfId[plain[i - 1]]));
            results.Add($"{variant}, {(filtered ? "with where" : "no where")}: {distinct} distinct ranks, {runsOfTheSmallestWord} runs of the smallest word in the plain order");

            if (oneBatch)
            {
                // everything is indexed in one batch, the stored term of an entry is its smallest term
                Assert.Equal(runsOfTheSmallestWord, distinct);
                if (filtered == false)
                    Assert.Equal(smallestWords, distinct);
            }
            else
            {
                // the ties are those of the stored terms, which can be any term of the entry
                Assert.True(distinct >= smallestWords, results[^1]);
            }

            static string SmallestWord(string[] words) => words.Min(StringComparer.Ordinal);
        }

        foreach (var line in results)
            output.WriteLine(line);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task TheRanksOfABulkInsertAreTheSameAsThePlainQueryWhateverTheBatches()
    {
        var reps = int.TryParse(Environment.GetEnvironmentVariable("RAVEN_RANKTIES_REPS"), out var configured) ? configured : 3;
        var users = new JoinScoreOrderTests.Generator(41).Users(2_500).Select((x, i) => new User { Id = $"users/{i}", Name = x.Name, Tag = $"t{i % 17}" }).ToList();
        var termsOfId = users.ToDictionary(x => x.Id, x => x.Name.Split(' ').ToArray());
        var rql = $"from index 'RankUsers' where Tag in ('t1', 't2', 't3', 't4') order by Name {Everything}";
        var differing = 0;

        for (var rep = 0; rep < reps; rep++)
        {
            using var store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
            new UsersIndex().Execute(store);
            using (var bulk = store.BulkInsert())
            {
                foreach (var user in users)
                    bulk.Store(user, user.Id);
            }

            Indexes.WaitForIndexing(store);
            var database = await Databases.GetDocumentDatabaseInstanceFor(store);
            var plain = PlainOrder(store, rql);
            using var session = new JoinFieldScanTests.ScanSession(database, "RankUsers", rql);
            using var ranks = session.Reader.ScanIdsAndRanks(session.Query, session.DocumentsContext, CancellationToken.None);
            var byKey = RanksByKey(ranks);
            AssertRanksFollowThePlainOrder($"rep {rep}", plain, byKey, termsOfId);

            var expected = 1 + Enumerable.Range(1, plain.Count - 1).Count(i => termsOfId[plain[i]].Min(StringComparer.Ordinal) != termsOfId[plain[i - 1]].Min(StringComparer.Ordinal));
            if (expected != byKey.Values.Distinct().Count())
            {
                differing++;
                output.WriteLine($"rep {rep}: {expected} ranks by the smallest word, {byKey.Values.Distinct().Count()} ranks of the scan");
            }
        }

        output.WriteLine($"{differing} of {reps} databases differ from the order by the smallest word");
    }

    private static Dictionary<string, float> RanksByKey(JoinTermScores ranks)
    {
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        for (var i = 0; i < ranks.Count; i++)
            result.Add(Encoding.UTF8.GetString(ranks.Term(i)), ranks.Score(i));
        return result;
    }

    private static List<string> PlainOrder(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<User>(rql).ToList().Select(x => x.Id.ToLowerInvariant()).ToList();
    }

    // The ranks are dense, never go down along the plain order, give a rank to exactly the documents of the plain query
    private static void AssertRanksFollowThePlainOrder(string label, List<string> plain, Dictionary<string, float> ranks, Dictionary<string, string[]> termsOfId)
    {
        Assert.True(plain.Count == ranks.Count && plain.All(ranks.ContainsKey), $"[{label}] {plain.Count} documents in the plain query, {ranks.Count} ranked");

        var previous = 0f;
        var runStart = 0;
        for (var i = 0; i <= plain.Count; i++)
        {
            var rank = i < plain.Count ? ranks[plain[i]] : float.MaxValue;
            if (i < plain.Count)
                Assert.True(rank == previous || rank == previous + 1, $"[{label}] position {i}: rank {rank} after {previous} (the ranks must be dense and never go down)");

            if (rank != previous && i > 0 && termsOfId != null)
            {
                var common = termsOfId[plain[runStart]].AsEnumerable();
                for (var j = runStart + 1; j < i; j++)
                    common = common.Intersect(termsOfId[plain[j]]);
                Assert.True(common.Any(), $"[{label}] the entries of the rank {previous} share no term (around {plain[i - 1]})");
            }

            if (rank != previous)
                runStart = i;
            previous = rank;
        }

        Assert.Equal((float)ranks.Values.Distinct().Count(), ranks[plain[^1]]);
    }

    private static void Store(IDocumentStore store, IEnumerable<User> users)
    {
        using var session = store.OpenSession();
        foreach (var user in users)
            session.Store(user, user.Id);
        session.SaveChanges();
    }

    private static List<User> UserData()
    {
        var random = new Random(77);
        return Enumerable.Range(0, 3_000).Select(i => new User
        {
            Id = $"rankusers/{i}",
            // one to three words of a small vocabulary: a lot of entries share a term, and some hold several
            Name = string.Join(' ', Enumerable.Range(0, 1 + random.Next(3)).Select(_ => Words[random.Next(Words.Length)])),
            Tag = $"t{random.Next(8)}"
        }).ToList();
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
    }

    private sealed class Game
    {
        public string Id { get; set; }
        public string Owner { get; set; }
    }

    private sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Tag };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "RankUsers";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Owner };
        }

        public override string IndexName => "RankGames";
    }
}
