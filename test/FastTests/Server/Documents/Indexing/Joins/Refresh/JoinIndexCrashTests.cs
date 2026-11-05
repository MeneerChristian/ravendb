using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Server;
using Raven.Server.Config;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// A server process that is killed (SIGKILL, no shutdown, no flush) between two commits of a chunked refresh of a join index is started again on the same data directory: the join index holds the state of the last commit,
/// which is a partial generation that no query is served from, goes on from it without building the lists again and ends equal to the indexes.
/// </summary>
public class JoinIndexCrashTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Database = "CrashJoin";
    private const int Budget = 50;
    private const int Conversations = 6000;
    private const int Contexts = 60;

    private sealed class ChildServer : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();

        public ChildServer(string dataDirectory, string settingsPath, int port)
        {
            Url = $"http://127.0.0.1:{port}";
            string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Raven.Server.exe" : "Raven.Server");
            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string argument in new[]
                     {
                         "-n", "-c", settingsPath, $"--ServerUrl={Url}", $"--DataDir={dataDirectory}", "--Setup.Mode=None", "--License.Eula.Accepted=true", "--Features.Availability=Experimental",
                         $"--Testing.ParentProcessId={Environment.ProcessId}"
                     })
                info.ArgumentList.Add(argument);

            _process = Process.Start(info) ?? throw new InvalidOperationException("The server did not start.");
            _process.OutputDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
            _process.ErrorDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public string Url { get; }

        public async Task WaitUntilReadyAsync()
        {
            using var http = new HttpClient();
            for (int i = 0; i < 600; i++)
            {
                if (_process.HasExited)
                    throw new InvalidOperationException("The server exited: " + Log());

                try
                {
                    if ((await http.GetAsync(Url + "/build/version")).IsSuccessStatusCode)
                        return;
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("The server did not answer: " + Log());
        }

        /// <summary>SIGKILL on Unix (TerminateProcess on Windows): the process gets no chance to flush or commit anything.</summary>
        public void Kill()
        {
            _process.Kill();
            Assert.True(_process.WaitForExit(30_000), "the server did not die");
        }

        public string Log()
        {
            lock (_output)
                return _output.ToString();
        }

        public void Dispose()
        {
            try
            {
                if (_process.HasExited == false)
                    _process.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void StoreContexts(IDocumentStore store)
    {
        using var bulk = store.BulkInsert();
        for (int i = 0; i < Contexts; i++)
            bulk.Store(new JoinIndexHarness.Context { Country = i % 3 == 0 ? "nl" : "de", Name = "n" + i % 5, Rank = i, IsActive = i % 2 == 0, Title = "red fox " + i % 4 }, $"contexts/{i}");
    }

    private static void StoreConversations(IDocumentStore store, int shift)
    {
        using var bulk = store.BulkInsert();
        for (int i = 0; i < Conversations; i++)
            bulk.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{(i + shift) % Contexts}", Title = "t" + shift }, $"conversations/{i}");
    }

    private static async Task WaitUntilNotStaleAsync(IDocumentStore store, int seconds = 120)
    {
        for (int i = 0; i < seconds * 20; i++)
        {
            if (store.Maintenance.Send(new GetIndexesStatisticsOperation()).Any(x => x.IsStale) == false)
                return;

            await Task.Delay(50);
        }

        Assert.Fail("the indexes stay stale");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AServerThatIsKilledBetweenTwoChunksOfARefreshResumesFromTheLastCommitWithoutBuildingAndEndsExact()
    {
        string dataDirectory = NewDataPath(forceCreateDir: true);
        int port = FreePort();
        long refreshesBeforeKill;

        // a settings file of its own: a server that finds none writes one next to the binaries of the tests
        string settingsPath = Path.Combine(dataDirectory, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{}");
        string serverDirectory = Path.Combine(dataDirectory, "Server");
        using (var child = new ChildServer(serverDirectory, settingsPath, port))
        {
            await child.WaitUntilReadyAsync();
            using var store = new DocumentStore { Urls = [child.Url], Database = Database }.Initialize();

            var record = new DatabaseRecord(Database)
            {
                Settings =
                {
                    [RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = "Corax",
                    [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = "true",
                    [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = Budget.ToString()
                }
            };
            await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(record));
            await store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.OuterIndex(), JoinIndexHarness.InnerIndex()));
            StoreContexts(store);
            StoreConversations(store, shift: 0);
            await store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.JoinIndexDefinition()));
            await WaitUntilNotStaleAsync(store);

            // every conversation moves to another context: Conversations changes, Budget of them in each commit of the join index
            long refreshes = store.Maintenance.Send(new GetIndexStatisticsOperation(JoinIndexHarness.JoinName)).Join.Refreshes;
            StoreConversations(store, shift: 1);

            refreshesBeforeKill = -1;
            for (int i = 0; i < 2000 && refreshesBeforeKill < 0; i++)
            {
                var stats = store.Maintenance.Send(new GetIndexStatisticsOperation(JoinIndexHarness.JoinName));
                if (stats.IsStale && stats.Join.Refreshes >= refreshes + 3)
                {
                    // three chunks are committed and the join index is behind the indexes: the next one is being made
                    child.Kill();
                    refreshesBeforeKill = stats.Join.Refreshes;
                }
                else if (stats.IsStale == false && stats.Join.Refreshes > refreshes)
                    Assert.Fail($"the refresh ended before the server could be killed ({stats.Join.Refreshes - refreshes} chunks)");
                else
                    await Task.Delay(1);
            }

            Assert.True(refreshesBeforeKill > 0, "the join index never began a chunked refresh: " + child.Log());
        }

        // the first start: nothing runs, the state that the killed process left is read as it is
        string[] common = [RavenConfiguration.GetKey(x => x.Core.ServerUrls), RavenConfiguration.GetKey(x => x.Core.DataDirectory), RavenConfiguration.GetKey(x => x.Core.RunInMemory)];
        Dictionary<string, string> Settings(bool indexingDisabled) => new()
        {
            [common[0]] = $"http://127.0.0.1:{port}",
            [common[1]] = serverDirectory,
            [common[2]] = "false",
            [RavenConfiguration.GetKey(x => x.Indexing.Disabled)] = indexingDisabled.ToString()
        };

        RavenServer frozen = GetNewServer(new ServerCreationOptions { DeletePrevious = false, CustomSettings = Settings(true) });
        try
        {
            using var store = (DocumentStore)new DocumentStore { Urls = [frozen.WebUrl], Database = Database }.Initialize();
            DocumentDatabase database = await frozen.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(Database);
            using JoinIndexHarness h = JoinIndexHarness.ForExistingDatabase(this, store, database);
            JoinIndex joinIndex = h.Join();
            Assert.NotNull(joinIndex);

            Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
            using (snapshot)
            {
                // the commit that the process made last before it died: the lists of a part of the changes, a state that is not served
                Assert.True(snapshot.Generation.IsPartial, "the kill did not leave a partial generation: " + snapshot.Generation);
                Assert.True(snapshot.Generation.Serial >= 1);
            }

            var oracle = new JoinIndexQueryOracle(h);
            long served = oracle.Info.Served;
            foreach (string sub in new[] { "", " where Country = 'nl'", " where IsActive = true" })
            {
                (QueryAnswer answer, QueryAnswer scan) = oracle.Both(JoinIndexQueryOracle.Join(sub, " order by m.Title limit 0, 50"));
                JoinIndexQueryOracle.AssertSame(sub, answer, scan);
            }

            Assert.Equal(served, oracle.Info.Served);
            Assert.True(oracle.Info.FallbackTotal > 0, "the queries did not even ask the join index");
        }
        finally
        {
            frozen.Dispose();
        }

        // the second start: the indexes run, the join index goes on from the partial generation
        RavenServer server = GetNewServer(new ServerCreationOptions { DeletePrevious = false, CustomSettings = Settings(false) });
        try
        {
            using var store = (DocumentStore)new DocumentStore { Urls = [server.WebUrl], Database = Database }.Initialize();
            DocumentDatabase database = await server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(Database);
            using JoinIndexHarness h = JoinIndexHarness.ForExistingDatabase(this, store, database);
            h.TrackJoin(JoinIndexHarness.JoinName);
            await WaitUntilNotStaleAsync(store);
            await h.AssertCurrentAsync();

            JoinIndex joinIndex = h.Join();
            Assert.Equal(0, joinIndex.Counters.Builds);
            Assert.Empty(joinIndex.Counters.RebuildsByReason);
            Assert.True(joinIndex.Counters.Refreshes > 0, "the join index did not refresh after the restart");
            Assert.True(JoinFileConsistencyChecker.Check(joinIndex, Conversations, Contexts).Ok);

            var oracle = new JoinIndexQueryOracle(h);
            foreach (string sub in new[] { "", " where Country = 'nl'", " where IsActive = true", " where Rank > 3" })
                oracle.Check(JoinIndexQueryOracle.Join(sub, " order by m.Title limit 0, 50"));
        }
        finally
        {
            server.Dispose();
        }

        Output.WriteLine($"the server was killed after {refreshesBeforeKill} refreshes of the join index");
    }
}
