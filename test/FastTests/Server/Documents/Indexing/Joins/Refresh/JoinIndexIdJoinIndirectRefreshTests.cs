using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

// The classes of the join indexes on the id of the inner documents (o.A = id(i)), run with the join indexes in the storage mode Indirect.

public class JoinIndexRefreshTestsIndirect(ITestOutputHelper output) : JoinIndexRefreshTests(output), IIndirectModeTest;

public class JoinIndexMultiKeyRefreshTestsIndirect(ITestOutputHelper output) : JoinIndexMultiKeyRefreshTests(output), IIndirectModeTest;

public class JoinIndexBulkLoadTestsIndirect(ITestOutputHelper output) : JoinIndexBulkLoadTests(output), IIndirectModeTest;
