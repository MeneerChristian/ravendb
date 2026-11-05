using System;
using System.Collections.Generic;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Utils;
using Tests.Infrastructure;

namespace FastTests.Corax;

// Class fixture of the join tests that only read their data
public sealed class SharedJoinDatabase : IDisposable
{
    private readonly object _locker = new();
    private readonly Dictionary<string, (string[] Urls, string Database)> _seeded = new();
    private readonly List<(string[] Urls, string Database)> _created = new();
    private readonly List<string> _dataDirectories = new();

    public IDocumentStore Get(string key, RavenTestBase.Options options, Func<RavenTestBase.Options, DocumentStore> getDocumentStore, Action<IDocumentStore> seed)
    {
        lock (_locker)
        {
            if (_seeded.TryGetValue(key, out var known))
                return new DocumentStore { Urls = known.Urls, Database = known.Database }.Initialize();

            // the data directory belongs to the fixture
            var dataDirectory = RavenTestHelper.NewDataPath("SharedJoin", 0, forceCreateDir: true);
            _dataDirectories.Add(dataDirectory);
            options.Path = dataDirectory;
            options.DeleteDatabaseOnDispose = false;
            var store = getDocumentStore(options);
            _created.Add((store.Urls, store.Database));
            seed(store);
            _seeded[key] = (store.Urls, store.Database);
            return store;
        }
    }

    public void Dispose()
    {
        foreach (var (urls, database) in _created)
        {
            using var store = new DocumentStore { Urls = urls }.Initialize();
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(database, hardDelete: true));
        }

        foreach (var dataDirectory in _dataDirectories)
            IOExtensions.DeleteDirectory(dataDirectory);
    }
}
