using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>
/// One query with two joins, each served by a join index of its own, and conditions on both aliases in the top-level where: the answer equals the join of the server (rows, order, total,
/// @index-score) and both join indexes serve their join (an alias that the where clause uses in an OR is joined once more, so a join index may serve two joins of one query).
/// </summary>
public class JoinIndexTwoJoinQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string OuterName = "Conversations/ByIds";
    private const string ContextsName = "Contexts/ByCategory";
    private const string UsersName = "Users/ByColor";
    private const string ContextJoin = "ConversationsWithContexts";
    private const string UserJoin = "ConversationsWithUsers";

    private sealed class Conversation
    {
        public string ContextId { get; set; }

        public string UserId { get; set; }

        public string Title { get; set; }
    }

    private sealed class Context
    {
        public string Category { get; set; }

        public string Name { get; set; }

        public string Tier { get; set; }
    }

    private sealed class User
    {
        public string Color { get; set; }

        public string Name { get; set; }

        public string Admin { get; set; }
    }

    private const string From = "from index 'Conversations/ByIds' as m join (from index 'Contexts/ByCategory'{0}) as c on m.ContextId = id(c) join (from index 'Users/ByColor'{1}) as u on m.UserId = id(u)";

    private const string FromLeftContext = "from index 'Conversations/ByIds' as m left join (from index 'Contexts/ByCategory'{0}) as c on m.ContextId = id(c) join (from index 'Users/ByColor'{1}) as u on m.UserId = id(u)";

    private const string FromLeftUser = "from index 'Conversations/ByIds' as m join (from index 'Contexts/ByCategory'{0}) as c on m.ContextId = id(c) left join (from index 'Users/ByColor'{1}) as u on m.UserId = id(u)";

    private const string FromLeftBoth = "from index 'Conversations/ByIds' as m left join (from index 'Contexts/ByCategory'{0}) as c on m.ContextId = id(c) left join (from index 'Users/ByColor'{1}) as u on m.UserId = id(u)";

    private static string Q(string from, string tail, string sub1 = "", string sub2 = "") => string.Format(from, sub1, sub2) + tail;

    private static IndexDefinition[] Definitions()
    {
        return
        [
            new IndexDefinition { Name = OuterName, Maps = { "from c in docs.Conversations select new { c.ContextId, c.UserId, c.Title }" } },
            new IndexDefinition { Name = ContextsName, Maps = { "from c in docs.Contexts select new { c.Category, c.Name, c.Tier }" }, Fields = { ["Name"] = new IndexFieldOptions { Indexing = FieldIndexing.Exact } } },
            new IndexDefinition { Name = UsersName, Maps = { "from u in docs.Users select new { u.Color, u.Name, u.Admin }" }, Fields = { ["Name"] = new IndexFieldOptions { Indexing = FieldIndexing.Exact } } },
            new IndexDefinition
            {
                Name = ContextJoin,
                Join = new JoinDefinition { OuterIndex = OuterName, OuterField = "ContextId", InnerIndex = ContextsName }.AddField("Category").AddField("Name").AddGrouping("Tier", "gold")
            },
            new IndexDefinition
            {
                Name = UserJoin,
                Join = new JoinDefinition { OuterIndex = OuterName, OuterField = "UserId", InnerIndex = UsersName }.AddField("Color").AddField("Name").AddGrouping("Admin", "true")
            }
        ];
    }

    private static readonly string[] Categories = ["5", "6", "7"];

    private static readonly string[] Colors = ["Green", "Red", "Blue"];

    private void Fill(IDocumentStore store, int seed)
    {
        var random = new Random(seed);
        using IDocumentSession session = store.OpenSession();
        for (int i = 0; i < 14; i++)
            session.Store(new Context { Category = Categories[random.Next(Categories.Length)], Name = "n" + random.Next(4), Tier = random.Next(3) == 0 ? "gold" : "silver" }, $"contexts/{i}");

        for (int i = 0; i < 12; i++)
            session.Store(new User { Color = Colors[random.Next(Colors.Length)], Name = "u" + random.Next(4), Admin = random.Next(3) == 0 ? "true" : "false" }, $"users/{i}");

        for (int i = 0; i < 120; i++)
        {
            // some keys point nowhere or are missing
            string context = random.Next(10) == 0 ? null : $"contexts/{random.Next(17)}";
            string user = random.Next(10) == 0 ? null : $"users/{random.Next(15)}";
            session.Store(new Conversation { ContextId = context, UserId = user, Title = random.Next(3) == 0 ? "a" : "b" }, $"conversations/{i}");
        }

        session.SaveChanges();
    }

    private async Task<DocumentStore> CreateStoreAsync(int seed)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.RunInMemory = true;
        Action<DatabaseRecord> previous = options.ModifyDatabaseRecord;
        options.ModifyDatabaseRecord = record =>
        {
            previous?.Invoke(record);
            record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        };

        DocumentStore store = GetDocumentStore(options);
        Fill(store, seed);
        await store.Maintenance.SendAsync(new PutIndexesOperation(Definitions()));
        Indexes.WaitForIndexing(store);
        return store;
    }

    private static QueryAnswer Run(IDocumentStore store, string rql)
    {
        using IDocumentSession session = store.OpenSession(new SessionOptions { NoCaching = true });
        List<Conversation> documents = session.Advanced.RawQuery<Conversation>(rql + " include timings()").Timings(out QueryTimings timings).Statistics(out QueryStatistics stats).ToList();
        var rows = new List<QueryRow>();
        foreach (Conversation document in documents)
        {
            IMetadataDictionary metadata = session.Advanced.GetMetadataFor(document);
            rows.Add(new QueryRow((string)metadata[Constants.Documents.Metadata.Id], metadata.TryGetValue(Constants.Documents.Metadata.IndexScore, out object score) ? Convert.ToDouble(score) : double.NaN));
        }

        return new QueryAnswer(rows, stats.TotalResults, stats.ResultEtag ?? 0, stats.IsStale, JoinIndexQueryOracle.CountersOf(timings));
    }

    private sealed record Shape(string Name, string Rql);

    private static IEnumerable<Shape> Shapes()
    {
        string[] tails = [" order by score() limit 0, 25", " limit 0, 1000", " order by m.Title limit 0, 10", " order by score() desc limit 3, 7"];
        foreach (string tail in tails)
        {
            yield return new Shape("and", Q(From, $" where c.Category = '5' and u.Color = 'Green'{tail}"));
            yield return new Shape("or", Q(From, $" where c.Category = '5' or u.Color = 'Green'{tail}"));
            yield return new Shape("not-one", Q(From, $" where u.Color = 'Green' and not (c.Category = '5'){tail}"));
            yield return new Shape("not-or", Q(From, $" where m.Title != 'zzz' and not (c.Category = '5' or u.Color = 'Green'){tail}"));
            yield return new Shape("one alias + outer", Q(From, $" where c.Category = '5' and m.Title = 'a'{tail}"));
            yield return new Shape("other alias + outer", Q(From, $" where u.Color = 'Green' and m.Title = 'a'{tail}"));
            yield return new Shape("one alias only", Q(From, $" where c.Category = '6'{tail}"));
            yield return new Shape("groupings", Q(From, $" where c.Tier = 'gold' and u.Admin = 'true'{tail}"));
            yield return new Shape("and + grouping", Q(From, $" where c.Category = '5' and c.Tier = 'gold' and u.Color = 'Green'{tail}"));
            yield return new Shape("sub-query conditions", Q(From, tail, " where Category = '5'", " where Color = 'Green'"));
            yield return new Shape("sub-query + top-level", Q(From, $" where u.Color = 'Green'{tail}", " where Category = '5'"));
            yield return new Shape("no condition", Q(From, tail));
            yield return new Shape("left context, condition on both", Q(FromLeftContext, $" where c.Category = '5' and u.Color = 'Green'{tail}"));
            yield return new Shape("left context, condition on user", Q(FromLeftContext, $" where u.Color = 'Green'{tail}", " where Category = '5'"));
            yield return new Shape("left user, condition on both", Q(FromLeftUser, $" where c.Category = '5' and u.Color = 'Green'{tail}"));
            yield return new Shape("left user, condition on context", Q(FromLeftUser, $" where c.Category = '5'{tail}", "", " where Color = 'Green'"));
            yield return new Shape("left both", Q(FromLeftBoth, tail, " where Category = '5'", " where Color = 'Green'"));
            yield return new Shape("left both, or", Q(FromLeftBoth, $" where c.Category = '5' or u.Color = 'Green'{tail}"));
        }
    }

    private static long Served(IDocumentStore store, string joinIndex) => store.Maintenance.Send(new GetIndexStatisticsOperation(joinIndex)).Join.Served;

    private static Dictionary<string, long> Fallbacks(IDocumentStore store, string joinIndex) => new(store.Maintenance.Send(new GetIndexStatisticsOperation(joinIndex)).Join.Fallbacks);

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TwoJoinsWithConditionsOnBothAliasesEqualTheJoinOfTheServer()
    {
        using DocumentStore store = await CreateStoreAsync(1);
        var report = new List<string>();
        var notBoth = new List<string>();
        var different = new List<string>();

        foreach (Shape shape in Shapes())
        {
            long contextsBefore = Served(store, ContextJoin);
            long usersBefore = Served(store, UserJoin);
            var contextFallbacksBefore = Fallbacks(store, ContextJoin);
            var userFallbacksBefore = Fallbacks(store, UserJoin);

            QueryAnswer served = Run(store, shape.Rql);
            long contexts = Served(store, ContextJoin) - contextsBefore;
            long users = Served(store, UserJoin) - usersBefore;
            string contextReasons = Delta(contextFallbacksBefore, Fallbacks(store, ContextJoin));
            string userReasons = Delta(userFallbacksBefore, Fallbacks(store, UserJoin));

            SetHook(store, true);
            QueryAnswer scan;
            try
            {
                scan = Run(store, shape.Rql);
            }
            finally
            {
                SetHook(store, false);
            }

            try
            {
                JoinIndexQueryOracle.AssertSame(shape.Rql, served, scan);
            }
            catch (Xunit.Sdk.XunitException e)
            {
                different.Add(shape.Name + ": " + e.Message.Split('\n')[0]);
            }

            string line = $"{shape.Name}: c served {contexts} {contextReasons}, u served {users} {userReasons}";
            report.Add(line);
            if (contexts < 1 || users < 1)
                notBoth.Add(line + "  <- " + shape.Rql);
        }

        foreach (string line in report.Distinct())
            Output.WriteLine(line);

        Assert.True(different.Count == 0, "the answer of the join indexes differs from the join of the server:" + Environment.NewLine + string.Join(Environment.NewLine, different.Distinct()));
        Assert.True(notBoth.Count == 0, "not served by both join indexes:" + Environment.NewLine + string.Join(Environment.NewLine, notBoth.Distinct()));
    }

    private static string Delta(Dictionary<string, long> before, Dictionary<string, long> after)
    {
        var reasons = after.Where(x => x.Value > before.GetValueOrDefault(x.Key)).Select(x => x.Key).ToList();
        return reasons.Count == 0 ? "" : "(" + string.Join(", ", reasons) + ")";
    }

    private void SetHook(IDocumentStore store, bool forceFallback)
    {
        var database = GetDatabase(store.Database).Result;
        foreach (string name in new[] { ContextJoin, UserJoin })
            ((JoinIndex)database.IndexStore.GetIndex(name)).QueryHooks = forceFallback ? new JoinIndexQueryHooks { ForceFallback = true } : null;
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheTimingsOfTheQuerySayWhichJoinIndexServedWhichAlias()
    {
        using DocumentStore store = await CreateStoreAsync(2);
        QueryAnswer answer = Run(store, Q(From, " where c.Category = '5' and u.Color = 'Green' order by score() limit 0, 10"));

        Output.WriteLine(string.Join(", ", answer.Counters.Select(x => $"{x.Key}={x.Value}")));
        Assert.Equal(1, answer.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
        Assert.Equal(1, answer.Counters.GetValueOrDefault("Join/u/JoinIndexServed"));
    }
}
