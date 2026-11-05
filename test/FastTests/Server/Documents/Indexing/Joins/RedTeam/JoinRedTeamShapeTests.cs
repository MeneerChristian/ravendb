using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.RedTeam;

/// <summary>Category B of the red team review: data shapes that a join of the server handles and a join index may not (case folding, long keys, odd values, numeric keys, multi-valued fields, huge groups).</summary>
public class JoinRedTeamShapeTests(ITestOutputHelper output) : JoinRedTeamBase(output)
{
    private static readonly string[] UnicodeNames =
    [
        "Ärger", "ÀÉÎÕÜ", "İstanbul", "ΣΊΣΥΦΟΣ", "Straße", "ẞtraße", "ǅungla", "ᾈ", "Kelvin", "Ångström", "ﬁsh", "ǆ", "Ⅷ", "ΐ", "ŉ", "ÅNGSTRÖM", "i̇stanbul", "ǈ", "ǋ"
    ];

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT01_UnicodeCaseFoldingOfIdsAndKeysJoinsLikeTheServer()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        for (int i = 0; i < UnicodeNames.Length; i++)
        {
            string name = UnicodeNames[i];
            string id = "contexts/" + name;
            h.Save(s => s.Store(new JoinIndexHarness.Context { Country = "nl", Name = "n" + name.Length, Rank = i, IsActive = true, Title = "t" }, id));
            h.PutConversation(i * 3, id);
            h.PutConversation(i * 3 + 1, id.ToUpperInvariant());
            h.PutConversation(i * 3 + 2, id.ToLowerInvariant());
        }

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        foreach (string sub in Subs)
        {
            Same(oracle, Join(sub, " order by score() limit 0, 1000"));
            Same(oracle, LeftJoin(sub, " order by score() limit 0, 1000"));
            Same(oracle, Join(sub, " order by m.Title limit 0, 1000"));
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT02_LongIdsOfTheJoinedDocumentsJoinLikeTheServer()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        int n = 0;
        foreach (int length in new[] { 10, 100, 118, 119, 120, 128, 150, 300, 480 })
        {
            string id = "contexts/" + new string('a', length) + (char)('b' + n % 3);
            h.Save(s => s.Store(new JoinIndexHarness.Context { Country = "nl", Name = "n", Rank = 1, IsActive = true, Title = "t" }, id));
            h.PutConversation(n++, id);
            h.PutConversation(n++, id.ToUpperInvariant());
        }

        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);
        var oracle = new JoinIndexQueryOracle(h);
        foreach (string sub in Subs)
        {
            Same(oracle, Join(sub, " order by score() limit 0, 1000"));
            Same(oracle, LeftJoin(sub, " order by m.Title limit 0, 1000"));
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT03_KeysAtAndOverTheLimitOfTheTermsOfAFieldJoinAreExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, ScoreSettings());
        string at = new('x', JoinLimits.MaxTermLength);
        string over = new('y', JoinLimits.MaxTermLength + 1);
        h.PutCodedContext(1, at);
        h.PutCodedContext(2, "k1");
        h.PutConversation(1, at);
        h.PutConversation(2, over);
        h.PutConversation(3, "k1");
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        foreach (string sub in Subs)
        {
            Same(oracle, FieldJoin(sub, " order by m.Title limit 0, 1000"));
            Same(oracle, FieldLeftJoin(sub, " order by m.Title limit 0, 1000"));
        }

        // an inner key over the limit puts the join index in the error state: the answers stay those of the server, and the key that is in both sides is joined
        h.PutCodedContext(3, over);
        await WaitForStateAsync(h, IndexState.Error);
        foreach (string sub in Subs)
            Same(oracle, FieldJoin(sub, " order by m.Title limit 0, 1000"));

        var scan = oracle.Run(FieldJoin("", " order by m.Title limit 0, 1000"));
        Assert.Contains("conversations/2", scan.Rows.Select(x => x.Id));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT04_AJoinThatIsInTheErrorStateRecoversWhenTheDocumentIsFixedAndTheIndexIsEnabled()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, ScoreSettings());
        h.PutCodedContext(1, "k1");
        h.PutConversation(1, "k1");
        await h.AssertCurrentAsync();

        h.PutCodedContext(2, new string('z', JoinLimits.MaxTermLength + 1));
        await WaitForStateAsync(h, IndexState.Error);

        // the operator fixes the data and enables the index
        h.PutCodedContext(2, "k2");
        h.PutConversation(2, "k2");

        // the join index reads what the indexes it joins hold: enabled before the inner index took the fixed document, it meets the long key again and goes back to the error state
        await WaitForUpstreamAsync(h);
        Enable(h);
        await WaitForStateAsync(h, IndexState.Normal);
        await h.AssertCurrentAsync();

        var oracle = new JoinIndexQueryOracle(h);
        Assert.True(Same(oracle, FieldJoin("", " order by m.Title limit 0, 1000")), "the join index does not serve after it recovered: " + string.Join(", ", oracle.Info.Fallbacks));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT05_NumericDateAndBooleanKeysOfAFieldJoinJoinLikeTheServer()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, ScoreSettings());
        object[] keys = [5L, "5", 5.5, "5.5", true, "true", "2024-01-01T00:00:00.0000000", 12345678901234L, 1e20, -3L, 0L, "0", 5.0];
        for (int i = 0; i < keys.Length; i++)
        {
            PutRaw(h, $"contexts/{i}", "Contexts", new { Code = keys[i], Country = i % 2 == 0 ? "nl" : "de", Name = "n", Rank = i, IsActive = true, Title = "t" });
            PutRaw(h, $"conversations/{i}", "Conversations", new { ContextId = keys[(i + 1) % keys.Length], Title = "a" });
        }

        PutRaw(h, "conversations/num", "Conversations", new { ContextId = 5L, Title = "b" });
        PutRaw(h, "conversations/dec", "Conversations", new { ContextId = 5.5, Title = "c" });
        PutRaw(h, "contexts/arr", "Contexts", new { Code = new object[] { 5L, "x", null }, Country = "nl", Name = "n", Rank = 1, IsActive = true, Title = "t" });

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        foreach (string sub in Subs)
        {
            Same(oracle, FieldJoin(sub, " order by m.Title limit 0, 1000"));
            Same(oracle, FieldLeftJoin(sub, " order by m.Title limit 0, 1000"));
            Same(oracle, FieldJoin(sub, " limit 0, 1000"));
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT06_NullMissingEmptyAndOddValuesOfTheDeclaredFieldsJoinLikeTheServer()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        string[] odd = ["a b", "a;b", "a|b", "a,b", "*", "?", "\"q\"", "\\", "a\u0001b", "", " ", "NL", "nl "];
        for (int i = 0; i < odd.Length; i++)
        {
            PutRaw(h, $"contexts/o{i}", "Contexts", new { Country = odd[i], Name = odd[i], Rank = i, IsActive = i % 2 == 0, Title = odd[i] });
            h.PutConversation(i, $"contexts/o{i}");
        }

        PutRaw(h, "contexts/m1", "Contexts", new { Title = "only a title" });
        PutRaw(h, "contexts/m2", "Contexts", new { Country = (string)null, Name = (string)null, IsActive = (bool?)null });
        PutRaw(h, "contexts/m3", "Contexts", new { Country = "nl", IsActive = "true" });
        PutRaw(h, "contexts/m4", "Contexts", new { Country = "nl", IsActive = "TRUE", Rank = 7 });
        PutRaw(h, "contexts/m5", "Contexts", new { });
        foreach (string id in new[] { "m1", "m2", "m3", "m4", "m5" })
            h.PutConversation(100 + id[1], "contexts/" + id);

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        var subs = Subs.Concat(["where Country = ''", " where Country = 'a;b'", " where Country = 'a|b'", " where Country = 'a,b'", " where Country = '*'", " where Country = 'nl '", " where Country = 'NL'", " where Rank = 7", " where Rank < 0", " where exists(Rank)", " where Country = null", " where Name = 'a b'", " where Title = 'only'"]).Select(x => x.StartsWith(" ") || x == "" ? x : " " + x);
        foreach (string sub in subs)
        {
            Same(oracle, Join(sub, " order by score() limit 0, 1000"));
            Same(oracle, LeftJoin(sub, " order by m.Title limit 0, 1000"));
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT07_ADeclaredFieldThatTurnsMultiValuedIsRefusedAndTheAnswersStayExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        for (int i = 0; i < 6; i++)
        {
            h.PutContext(i, i % 2 == 0 ? "nl" : "de");
            h.PutConversation(i, $"contexts/{i}");
        }

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        Assert.True(Same(oracle, Join(" where Country = 'nl'", " order by score() limit 0, 1000")));

        PutRaw(h, "contexts/0", "Contexts", new { Country = new[] { "nl", "de" }, Name = "n", Rank = 1, IsActive = true, Title = "t" });
        await WaitForStateAsync(h, IndexState.Error);
        foreach (string sub in Subs.Append(" where Country = 'de'"))
            Same(oracle, Join(sub, " order by score() limit 0, 1000"));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT08_AHugeGroupAndAHubOfOuterEntriesStayExactUnderChangesAndChunks()
    {
        // two documents per commit more than the budget: a chunked refresh over a 20k group
        var settings = ScoreSettings((RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit), "3000"));
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, settings);
        using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < 20_000; i++)
                bulk.Store(new JoinIndexHarness.Context { Code = "hub", Country = i % 2 == 0 ? "nl" : "de", Name = "n" + i % 7, Rank = i % 50, IsActive = i % 3 != 0, Title = "t" }, $"contexts/{i}");

            for (int i = 0; i < 6000; i++)
                bulk.Store(new JoinIndexHarness.Conversation { ContextId = i % 5 == 0 ? "other" : "hub", Title = i % 2 == 0 ? "a" : "b" }, $"conversations/{i}");
        }

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string[] tails = [" order by m.Title limit 0, 50", " order by m.Title limit 5990, 50", " order by m.Title desc limit 0, 7000"];
        bool served = false;
        foreach (string sub in new[] { "", " where Country = 'nl'", " where Country = 'nl' and IsActive = true", " where Rank > 40" })
        {
            foreach (string tail in tails)
                served |= Same(oracle, FieldJoin(sub, tail));
        }

        Assert.True(served, "nothing was served: " + string.Join(", ", oracle.Info.Fallbacks));

        // a bulk change under the hub: half of the group leaves it, one entry comes back
        using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < 20_000; i += 2)
                bulk.Store(new JoinIndexHarness.Context { Code = i % 4 == 0 ? "other" : "gone", Country = "nl", Name = "x", Rank = 1, IsActive = true, Title = "t" }, $"contexts/{i}");
        }

        await h.AssertCurrentAsync();
        foreach (string sub in new[] { "", " where Country = 'nl'", " where Country = 'de'" })
        {
            foreach (string tail in tails)
                Same(oracle, FieldJoin(sub, tail));
        }
    }
}
