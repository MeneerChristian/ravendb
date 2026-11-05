using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Server.Config;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Raven.Server.ServerWide.Context;
using Sparrow.LowMemory;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>A test class that runs its tests with the join indexes on a field in the storage mode Indirect: the harness it is created from turns the mode on in the settings and in the definition.</summary>
public interface IIndirectModeTest
{
}

/// <summary>
/// The cache of the key lists of an Indirect join index in a test: <see cref="Default"/> is what the server makes (the budget of the configuration, fill-hold), <see cref="Off"/> has none, <see cref="On"/> keeps every list
/// that is read (a budget no test reaches, no admission rule), <see cref="Tiny"/> a few hundred bytes (eviction and rejection in the middle of a workload). The restart and the read every third step of the oracle are
/// drivers in the tests that use them.
/// </summary>
public enum ResidentMode
{
    Default,
    Off,
    On,
    Tiny
}

/// <summary>A test class that runs its join indexes with the cache in this mode (the join indexes are Indirect as well when it is an <see cref="IIndirectModeTest"/>).</summary>
public interface IResidentModeTest
{
    ResidentMode ResidentMode { get; }
}

/// <summary>A database with the two indexes and a join index, and operations that write documents the way an application does.</summary>
public sealed class JoinIndexHarness : IDisposable
{
    public const string OuterName = "Conversations/ByContext";
    public const string InnerName = "Contexts/Search";
    public const string JoinName = "SearchPairs";

    public sealed class Conversation
    {
        public string ContextId { get; set; }

        /// <summary>The keys of a conversation of <see cref="MultiOuterIndex"/>: an array of ids.</summary>
        public string[] ContextIds { get; set; }

        public string Title { get; set; }
    }

    public sealed class Context
    {
        /// <summary>The key of a join on a field (<see cref="FieldInnerIndex"/>).</summary>
        public string Code { get; set; }

        /// <summary>The keys of a context of <see cref="FieldMultiInnerIndex"/>: an array of codes.</summary>
        public string[] Codes { get; set; }

        public string Country { get; set; }

        public string Name { get; set; }

        public int Rank { get; set; }

        public bool IsActive { get; set; }

        public string Title { get; set; }
    }

    private readonly RavenTestBase _test;

    /// <summary>The test that made the harness runs the join indexes on a field in the storage mode Indirect.</summary>
    public bool IsIndirect => _test is IIndirectModeTest;

    public DocumentStore Store { get; }

    public DocumentDatabase Database { get; private set; }

    public JoinIndexInvariantChecker.SeenTerms Seen { get; private set; }

    /// <summary>
    /// The registry of the caches of the join indexes of this harness: the one of the process is charged by the caches of the tests that run at the same time and drops every list on a low memory notification, of the machine or simulated, so a test that
    /// expects lists in its cache must not depend on it.
    /// </summary>
    public ResidentCacheRegistry ResidentRegistry { get; } = new(ResidentCacheRegistry.DefaultBudgetBytes, registerWithLowMemoryNotification: false);

    private JoinIndexHarness(RavenTestBase test, DocumentStore store)
    {
        _test = test;
        Store = store;
    }

    public static IndexDefinition JoinIndexDefinition(string name = JoinName)
    {
        return new IndexDefinition
        {
            Name = name,
            Join = new JoinDefinition { OuterIndex = OuterName, OuterField = "ContextId", InnerIndex = InnerName }
                .AddField("Country")
                .AddField("Name")
                .AddField("Rank", JoinFieldKind.Long)
                .AddField("Title", JoinFieldKind.Text)
                .AddGrouping("IsActive", "true")
        };
    }

    /// <summary>A join index of a join on a field: the key of the conversations (ContextId) is joined with the key of the contexts (Code).</summary>
    public static IndexDefinition FieldJoinIndexDefinition(string name = JoinName, bool outerId = false)
    {
        return new IndexDefinition
        {
            Name = name,
            Join = new JoinDefinition { OuterIndex = OuterName, OuterField = outerId ? "id()" : "ContextId", InnerField = "Code", InnerIndex = InnerName }
                .AddField("Country")
                .AddField("Name")
                .AddField("Rank", JoinFieldKind.Long)
                .AddField("Title", JoinFieldKind.Text)
                .AddGrouping("IsActive", "true")
        };
    }

    /// <summary>The inner index of <see cref="InnerIndex"/> with the key field Code.</summary>
    public static IndexDefinition FieldInnerIndex()
    {
        IndexDefinition definition = InnerIndex();
        definition.Maps.Clear();
        definition.Maps.Add("from c in docs.Contexts select new { c.Code, c.Country, c.Name, c.Rank, c.IsActive, c.Title }");
        return definition;
    }

    /// <summary>The inner index whose key field Code holds an array of codes (the documents have them in Codes).</summary>
    public static IndexDefinition FieldMultiInnerIndex()
    {
        IndexDefinition definition = InnerIndex();
        definition.Maps.Clear();
        definition.Maps.Add("from c in docs.Contexts select new { Code = c.Codes, c.Country, c.Name, c.Rank, c.IsActive, c.Title }");
        return definition;
    }

    public static IndexDefinition OuterIndex() => new() { Name = OuterName, Maps = { "from c in docs.Conversations select new { c.ContextId, c.Title }" } };

    /// <summary>The outer index whose key field holds an array of ids (the documents have them in ContextIds); the name and the field are those of <see cref="OuterIndex"/>, so the queries and the join index definition are the same.</summary>
    public static IndexDefinition MultiOuterIndex() => new() { Name = OuterName, Maps = { "from c in docs.Conversations select new { ContextId = c.ContextIds, c.Title }" } };

    public static IndexDefinition InnerIndex()
    {
        return new IndexDefinition
        {
            Name = InnerName,
            Maps = { "from c in docs.Contexts select new { c.Country, c.Name, c.Rank, c.IsActive, c.Title }" },
            Fields =
            {
                ["Title"] = new IndexFieldOptions { Indexing = FieldIndexing.Search },
                ["Name"] = new IndexFieldOptions { Indexing = FieldIndexing.Exact }
            }
        };
    }

    public static async Task<JoinIndexHarness> CreateAsync(RavenTestBase test, Dictionary<string, string> databaseSettings = null, bool putJoin = true, bool runInMemory = true,
        IndexDefinition[] indexes = null)
    {
        var options = RavenTestBase.Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.RunInMemory = runInMemory;
        if (test is IIndirectModeTest)
        {
            databaseSettings = databaseSettings == null ? new Dictionary<string, string>() : new Dictionary<string, string>(databaseSettings);
            databaseSettings[RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = "true";
        }

        if (databaseSettings != null)
        {
            Action<DatabaseRecord> previous = options.ModifyDatabaseRecord;
            options.ModifyDatabaseRecord = record =>
            {
                previous?.Invoke(record);
                foreach ((string key, string value) in databaseSettings)
                    record.Settings[key] = value;
            };
        }

        DocumentStore store = test.GetDocumentStore(options);
        indexes ??= [OuterIndex(), InnerIndex()];
        if (indexes.Length > 0)
            await store.Maintenance.SendAsync(new PutIndexesOperation(indexes));

        var harness = new JoinIndexHarness(test, store);
        await harness.AttachAsync();
        if (putJoin)
            await harness.PutJoinAsync(JoinIndexDefinition());

        return harness;
    }

    /// <summary>The harness of a database that exists on a server of its own (one that was started again on a data directory), not made by the test.</summary>
    public static JoinIndexHarness ForExistingDatabase(RavenTestBase test, DocumentStore store, DocumentDatabase database)
    {
        return new JoinIndexHarness(test, store) { Database = database };
    }

    public async Task AttachAsync()
    {
        Database = await _test.GetDatabase(Store.Database);
    }

    public async Task PutJoinAsync(IndexDefinition definition)
    {
        if (_test is IIndirectModeTest && definition.Join is { StorageMode: JoinStorageMode.Auto } && definition.Join.JoinsOnOuterId == false)
            definition.Join.StorageMode = JoinStorageMode.Indirect;

        await Store.Maintenance.SendAsync(new PutIndexesOperation(definition));
        Seen = new JoinIndexInvariantChecker.SeenTerms(definition.Join.Fields.Count);
    }

    public static ResidentCacheOptions OptionsOf(ResidentMode mode) => mode switch
    {
        ResidentMode.Default => new ResidentCacheOptions { BudgetBytes = ResidentCacheRegistry.DefaultBudgetBytes, Admission = ResidentAdmission.FillHold },
        ResidentMode.Off => null,
        ResidentMode.On => new ResidentCacheOptions { BudgetBytes = long.MaxValue / 4, Admission = ResidentAdmission.Always },
        ResidentMode.Tiny => new ResidentCacheOptions { BudgetBytes = 600, Admission = ResidentAdmission.Always },
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    /// <summary>Replaces the cache of the join index with an empty one of the mode (no epoch): the next refresh run establishes it and warms it up.</summary>
    public static void UseResident(JoinIndex joinIndex, ResidentMode mode, ResidentCacheRegistry registry = null)
    {
        s_configured.AddOrUpdate(joinIndex, null);
        s_modes.GetOrCreateValue(joinIndex.DocumentDatabase).Value = mode;
        joinIndex.UseResidentCache(OptionsOf(mode), registry);
    }

    /// <summary>Takes a join index that a query created: the invariant checker needs the number of its declared fields.</summary>
    public void TrackJoin(string name) => Seen = new JoinIndexInvariantChecker.SeenTerms(Join(name).Join.Fields.Count);

    /// <summary>
    /// The join index of the node. The index store replaces the instance of a join index while the first build runs, and the cache of the new instance is the default one: the first time the harness hands out an instance its cache is the one that the test last
    /// asked for (<see cref="UseResident"/>, else the mode of the class, else the default one), on the registry of the harness.
    /// </summary>
    public JoinIndex Join(string name = JoinName)
    {
        var joinIndex = (JoinIndex)Database.IndexStore.GetIndex(name);
        if (joinIndex != null && (_test is IIndirectModeTest || _test is IResidentModeTest) && s_configured.TryGetValue(joinIndex, out _) == false)
        {
            ResidentMode mode = s_modes.TryGetValue(Database, out StrongBox<ResidentMode> asked) ? asked.Value : (_test as IResidentModeTest)?.ResidentMode ?? ResidentMode.Default;
            UseResident(joinIndex, mode, ResidentRegistry);
        }

        return joinIndex;
    }

    private static readonly ConditionalWeakTable<JoinIndex, object> s_configured = new();

    private static readonly ConditionalWeakTable<DocumentDatabase, StrongBox<ResidentMode>> s_modes = new();

    public IndexStats Stats(string name = JoinName) => Store.Maintenance.Send(new GetIndexStatisticsOperation(name));

    public void Save(Action<IDocumentSession> work)
    {
        using IDocumentSession session = Store.OpenSession();
        work(session);
        session.SaveChanges();
    }

    public void PutContext(int id, string country = "nl", string name = "Alpha", int rank = 1, bool active = true, string title = "red fox")
    {
        Save(s => s.Store(new Context { Country = country, Name = name, Rank = rank, IsActive = active, Title = title }, $"contexts/{id}"));
    }

    public void PutCodedContext(int id, string code, string country = "nl", string name = "Alpha", int rank = 1, bool active = true, string title = "red fox")
    {
        Save(s => s.Store(new Context { Code = code, Country = country, Name = name, Rank = rank, IsActive = active, Title = title }, $"contexts/{id}"));
    }

    /// <summary>The harness of a join on a field: the conversations carry the key in ContextId, the contexts in Code.</summary>
    /// <summary>A context whose key field holds an array of codes (<see cref="FieldMultiInnerIndex"/>).</summary>
    public void PutCodedContextKeys(int id, string[] codes, string country = "nl", string name = "Alpha", int rank = 1, bool active = true, string title = "red fox")
    {
        Save(s => s.Store(new Context { Codes = codes, Country = country, Name = name, Rank = rank, IsActive = active, Title = title }, $"contexts/{id}"));
    }

    /// <summary>The harness of a join on a field. With <paramref name="arrays"/> the key of the conversations is an array (ContextIds, see <see cref="PutConversationKeys"/>) and so is the key of the contexts (Codes, see <see cref="PutCodedContextKeys"/>).</summary>
    public static async Task<JoinIndexHarness> CreateFieldJoinAsync(RavenTestBase test, Dictionary<string, string> databaseSettings = null, bool putJoin = true, bool runInMemory = true, bool arrays = false,
        bool outerId = false)
    {
        // the key of the outer side is the id of the document: the outer index needs no field for it, and the keys of the contexts are the ids of the conversations
        IndexDefinition[] indexes = outerId ? [OuterIndex(), arrays ? FieldMultiInnerIndex() : FieldInnerIndex()] : arrays ? [MultiOuterIndex(), FieldMultiInnerIndex()] : [OuterIndex(), FieldInnerIndex()];
        JoinIndexHarness harness = await CreateAsync(test, databaseSettings, putJoin: false, runInMemory, indexes: indexes);
        if (putJoin)
            await harness.PutJoinAsync(FieldJoinIndexDefinition(outerId: outerId));

        return harness;
    }

    public void PutConversation(int id, string contextId, string title = "hello")
    {
        Save(s => s.Store(new Conversation { ContextId = contextId, Title = title }, $"conversations/{id}"));
    }

    public void PutConversationKeys(int id, string[] contextIds, string title = "hello")
    {
        Save(s => s.Store(new Conversation { ContextIds = contextIds, Title = title }, $"conversations/{id}"));
    }

    public void Delete(string id) => Save(s => s.Delete(id));

    /// <summary>Waits until the indexes (the join index among them) are not stale, then compares the join index with a scan of the snapshots.</summary>
    public async Task<JoinIndexInvariantChecker.Result> CheckAsync(TimeSpan? timeout = null, string name = JoinName)
    {
        DateTime until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            await Task.Run(() => WaitUntilNotStale(until));

            JoinIndex joinIndex = Join(name);
            JoinIndexInvariantChecker.Result last = joinIndex == null
                ? new JoinIndexInvariantChecker.Result { NotComparedReason = "the join index does not exist now" }
                : JoinIndexInvariantChecker.Check(Database, joinIndex, Seen);
            if (last.Compared || DateTime.UtcNow > until)
                return last;

            await Task.Delay(20);
        }
    }

    private void WaitUntilNotStale(DateTime until)
    {
        while (DateTime.UtcNow < until)
        {
            if (Store.Maintenance.Send(new GetIndexesStatisticsOperation()).Any(x => x.IsStale) == false)
                return;

            Thread.Sleep(10);
        }
    }

    /// <summary>Waits until the resident lists were compared with the outer snapshot (the epoch is that snapshot) and returns the number of lists that were; fails when they differ. At least <paramref name="atLeast"/> lists were compared.</summary>
    public async Task<int> AssertResidentAsync(int atLeast = 0, string name = JoinName, TimeSpan? timeout = null)
    {
        DateTime until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        JoinIndexInvariantChecker.Result result;
        while (true)
        {
            result = await CheckAsync(TimeSpan.FromSeconds(5), name);
            Assert.True(result.Ok, result.ToString());
            if (result.ResidentChecked >= atLeast && result.ResidentChecked >= 0 || DateTime.UtcNow > until)
                break;

            await Task.Delay(50);
        }

        Assert.True(result.ResidentChecked >= atLeast, $"{result.ResidentChecked} resident lists were compared ({result.ResidentNote}), expected at least {atLeast}; {ResidentState(name)}");
        return result.ResidentChecked;
    }

    /// <summary>The state that decides whether a list is kept: the registry that charges the cache, the memory state of the process, and the counters of the cache.</summary>
    public string ResidentState(string name = JoinName)
    {
        JoinResident resident = Join(name).Resident;
        if (resident == null)
            return "no cache";

        ResidentCacheStats stats = resident.Cache.GetStats();
        ResidentCacheRegistry registry = ResidentCacheRegistry.Instance;
        return $"harness registry: low memory {ResidentRegistry.IsLowMemory}, charged {ResidentRegistry.ChargedBytes}, caches {ResidentRegistry.CacheCount}; process registry: low memory {registry.IsLowMemory}, charged {registry.ChargedBytes}, budget {registry.BudgetBytes}, caches {registry.CacheCount}; process low memory {LowMemoryNotification.Instance.LowMemoryState}; "
               + $"cache: keys {stats.ResidentKeys}, bytes {stats.ResidentBytes}, warm-up keys {stats.WarmUpKeys}, inserts {stats.Inserts}, rejected {stats.Rejected}, not admitted {stats.NotAdmitted}, low memory drops {stats.LowMemoryDrops}, invalidations {stats.Invalidations}";
    }

    public async Task AssertCurrentAsync(string name = JoinName)
    {
        JoinIndexInvariantChecker.Result result = await CheckAsync(name: name);
        Assert.True(result.Ok, result.ToString());
    }

    /// <summary>Runs the tombstone cleaner of the database once.</summary>
    public async Task CleanTombstonesAsync()
    {
        var method = typeof(TombstoneCleaner).GetMethod("ExecuteCleanup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        await (Task)method.Invoke(Database.TombstoneCleaner, new object[] { null });
    }

    /// <summary>One round of a seeded workload: inserts, updates, moves between contexts, deletes, recreates, flips of contexts (a hub among them) and keys that point nowhere.</summary>
    public void RandomRound(Random random, int operations, int contexts = 14, int conversations = 80)
    {
        for (int i = 0; i < operations; i++)
        {
            int kind = random.Next(100);
            if (kind < 40)
            {
                // the hub gets half of the conversations
                int context = random.Next(2) == 0 ? 0 : random.Next(contexts + 4);
                string key = random.Next(12) == 0 ? null : $"contexts/{context}";
                PutConversation(random.Next(conversations), key, random.Next(3) == 0 ? "a" : "b");
            }
            else if (kind < 55)
            {
                Delete($"conversations/{random.Next(conversations)}");
            }
            else if (kind < 80)
            {
                int id = random.Next(random.Next(4) == 0 ? 1 : contexts);
                PutContext(id, random.Next(3) == 0 ? "nl" : "c" + random.Next(4), "n" + random.Next(5), random.Next(50), random.Next(3) != 0, string.Join(' ', Enumerable.Range(0, random.Next(0, 4)).Select(_ => "w" + random.Next(6))));
            }
            else if (kind < 90)
            {
                Delete($"contexts/{random.Next(contexts)}");
            }
            else
            {
                // delete and recreate in two batches
                int id = random.Next(contexts);
                Delete($"contexts/{id}");
                PutContext(id, "re", "again", random.Next(50), true, "back");
            }
        }
    }

    /// <summary>A key of a join on a field: none, empty, in two cases, a hub that most documents share, or one of a few others.</summary>
    public static string RandomCode(Random random, int codes = 7)
    {
        int kind = random.Next(100);
        if (kind < 6)
            return null;

        if (kind < 9)
            return "";

        string code = kind < 40 ? "k0" : "k" + random.Next(codes);
        return random.Next(9) == 0 ? code.ToUpperInvariant() : code;
    }

    /// <summary>One round of a seeded workload of a join on a field: conversations and contexts that change their keys, join in groups of every size, a hub on both sides, deletes and recreations.</summary>
    public void RandomFieldRound(Random random, int operations, int contexts = 16, int conversations = 80)
    {
        for (int i = 0; i < operations; i++)
        {
            int kind = random.Next(100);
            if (kind < 34)
            {
                PutConversation(random.Next(conversations), RandomCode(random), random.Next(3) == 0 ? "a" : "b");
            }
            else if (kind < 46)
            {
                Delete($"conversations/{random.Next(conversations)}");
            }
            else if (kind < 82)
            {
                int id = random.Next(random.Next(4) == 0 ? 2 : contexts);
                PutCodedContext(id, RandomCode(random), random.Next(3) == 0 ? "nl" : "c" + random.Next(4), "n" + random.Next(5), random.Next(50), random.Next(3) != 0, string.Join(' ', Enumerable.Range(0, random.Next(0, 4)).Select(_ => "w" + random.Next(6))));
            }
            else if (kind < 92)
            {
                Delete($"contexts/{random.Next(contexts)}");
            }
            else
            {
                int id = random.Next(contexts);
                Delete($"contexts/{id}");
                PutCodedContext(id, "k" + random.Next(3), "re", "again", random.Next(50), true, "back");
            }
        }
    }

    /// <summary>
    /// The array of keys of a random document of a join on a field: none, empty, up to <paramref name="most"/> keys of a few that many documents share (often the hub), one twice, in another case,
    /// with a null inside.
    /// </summary>
    public static string[] RandomCodes(Random random, int most, int codes = 7)
    {
        int kind = random.Next(100);
        if (kind < 5)
            return null;

        if (kind < 9)
            return [];

        int count = most == 1 ? 1 : kind < 40 ? 1 : kind < 75 ? 2 : 1 + random.Next(most);
        var keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            if (random.Next(14) == 0)
            {
                keys[i] = random.Next(2) == 0 ? null : "";
                continue;
            }

            string code = random.Next(4) == 0 ? "k0" : "k" + random.Next(codes);
            keys[i] = random.Next(9) == 0 ? code.ToUpperInvariant() : code;
        }

        if (count > 1 && random.Next(8) == 0)
            keys[1] = keys[0];

        return keys;
    }

    /// <summary>
    /// One round of a seeded workload of a join on a field on the indexes of <c>CreateFieldJoinAsync(arrays: true)</c>: the conversations hold up to <paramref name="outerKeys"/> keys, the contexts up to <paramref name="innerKeys"/>.
    /// Keys are added to and dropped from the arrays from round to round, documents are deleted and created again.
    /// </summary>
    public void RandomFieldArrayRound(Random random, int operations, int outerKeys, int innerKeys, int contexts = 16, int conversations = 80)
    {
        for (int i = 0; i < operations; i++)
        {
            int kind = random.Next(100);
            if (kind < 34)
            {
                PutConversationKeys(random.Next(conversations), RandomCodes(random, outerKeys), random.Next(3) == 0 ? "a" : "b");
            }
            else if (kind < 46)
            {
                Delete($"conversations/{random.Next(conversations)}");
            }
            else if (kind < 82)
            {
                int id = random.Next(random.Next(4) == 0 ? 2 : contexts);
                PutCodedContextKeys(id, RandomCodes(random, innerKeys), random.Next(3) == 0 ? "nl" : "c" + random.Next(4), "n" + random.Next(5), random.Next(50), random.Next(3) != 0, string.Join(' ', Enumerable.Range(0, random.Next(0, 4)).Select(_ => "w" + random.Next(6))));
            }
            else if (kind < 92)
            {
                Delete($"contexts/{random.Next(contexts)}");
            }
            else
            {
                int id = random.Next(contexts);
                Delete($"contexts/{id}");
                PutCodedContextKeys(id, ["k" + random.Next(3), "k" + random.Next(3)], "re", "again", random.Next(50), true, "back");
            }
        }
    }

    /// <summary>
    /// The keys of a random context of a join of the id of the conversation with a field of the context: ids of conversations (the hub among them, one that does not exist), in another case,
    /// none, empty, null inside, repeated. <paramref name="most"/> bounds the length of the array.
    /// </summary>
    public static string[] RandomConversationIds(Random random, int most, int conversations)
    {
        int kind = random.Next(100);
        if (kind < 5)
            return null;

        if (kind < 9)
            return [];

        int count = most == 1 ? 1 : kind < 40 ? 1 : kind < 75 ? 2 : 1 + random.Next(most);
        var keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            if (random.Next(14) == 0)
            {
                keys[i] = random.Next(2) == 0 ? null : "";
                continue;
            }

            // the hub, a document that exists, or one that does not exist (yet)
            int id = random.Next(4) == 0 ? 0 : random.Next(conversations + 6);
            keys[i] = random.Next(9) == 0 ? $"Conversations/{id}" : $"conversations/{id}";
        }

        if (count > 1 && random.Next(8) == 0)
            keys[1] = keys[0];

        return keys;
    }

    /// <summary>
    /// One round of a seeded workload of <c>on id(m) = c.Code</c> (indexes of <c>CreateFieldJoinAsync(outerId: true)</c>): conversations are created, changed and deleted, contexts point at conversations that
    /// exist, do not exist yet or were deleted, change their keys and atoms, are deleted and recreated. With <paramref name="arrays"/> a context holds up to <paramref name="innerKeys"/> keys.
    /// </summary>
    public void RandomIdRound(Random random, int operations, int innerKeys, bool arrays = true, int contexts = 16, int conversations = 40)
    {
        for (int i = 0; i < operations; i++)
        {
            int kind = random.Next(100);
            if (kind < 30)
            {
                PutConversation(random.Next(conversations), null, random.Next(3) == 0 ? "a" : "b");
            }
            else if (kind < 42)
            {
                Delete($"conversations/{random.Next(conversations)}");
            }
            else if (kind < 82)
            {
                int id = random.Next(random.Next(4) == 0 ? 2 : contexts);
                string country = random.Next(3) == 0 ? "nl" : "c" + random.Next(4);
                string title = string.Join(' ', Enumerable.Range(0, random.Next(0, 4)).Select(_ => "w" + random.Next(6)));
                if (arrays)
                    PutCodedContextKeys(id, RandomConversationIds(random, innerKeys, conversations), country, "n" + random.Next(5), random.Next(50), random.Next(3) != 0, title);
                else
                    PutCodedContext(id, RandomConversationIds(random, 1, conversations)?.FirstOrDefault(), country, "n" + random.Next(5), random.Next(50), random.Next(3) != 0, title);
            }
            else if (kind < 92)
            {
                Delete($"contexts/{random.Next(contexts)}");
            }
            else
            {
                int id = random.Next(contexts);
                Delete($"contexts/{id}");
                if (arrays)
                    PutCodedContextKeys(id, [$"conversations/{random.Next(3)}", $"conversations/{random.Next(3)}"], "re", "again", random.Next(50), true, "back");
                else
                    PutCodedContext(id, $"conversations/{random.Next(3)}", "re", "again", random.Next(50), true, "back");
            }
        }
    }

    /// <summary>The array of keys of a random conversation: none, one, several, repeated, in another case, with a null inside, pointing at nothing, and often at the hub.</summary>
    public static string[] RandomKeys(Random random, int contexts)
    {
        int kind = random.Next(100);
        if (kind < 6)
            return null;

        if (kind < 12)
            return [];

        int count = kind < 40 ? 1 : kind < 70 ? 2 : 3 + random.Next(3);
        var keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            int context = random.Next(3) == 0 ? 0 : random.Next(contexts + 4);
            keys[i] = random.Next(14) == 0 ? null : random.Next(8) == 0 ? $"Contexts/{context}" : $"contexts/{context}";
        }

        if (count > 1 && random.Next(8) == 0)
            keys[1] = keys[0];

        return keys;
    }

    /// <summary>One round of a seeded workload on <see cref="MultiOuterIndex"/>: conversations with arrays of keys that change from round to round, deletes, and the changes of the contexts of <see cref="RandomRound"/>.</summary>
    public void RandomMultiRound(Random random, int operations, int contexts = 14, int conversations = 80)
    {
        for (int i = 0; i < operations; i++)
        {
            int kind = random.Next(100);
            if (kind < 45)
            {
                PutConversationKeys(random.Next(conversations), RandomKeys(random, contexts), random.Next(3) == 0 ? "a" : "b");
            }
            else if (kind < 58)
            {
                Delete($"conversations/{random.Next(conversations)}");
            }
            else if (kind < 80)
            {
                int id = random.Next(random.Next(4) == 0 ? 1 : contexts);
                PutContext(id, random.Next(3) == 0 ? "nl" : "c" + random.Next(4), "n" + random.Next(5), random.Next(50), random.Next(3) != 0, string.Join(' ', Enumerable.Range(0, random.Next(0, 4)).Select(_ => "w" + random.Next(6))));
            }
            else if (kind < 90)
            {
                Delete($"contexts/{random.Next(contexts)}");
            }
            else
            {
                int id = random.Next(contexts);
                Delete($"contexts/{id}");
                PutContext(id, "re", "again", random.Next(50), true, "back");
            }
        }
    }

    public void Dispose() => Store.Dispose();
}
