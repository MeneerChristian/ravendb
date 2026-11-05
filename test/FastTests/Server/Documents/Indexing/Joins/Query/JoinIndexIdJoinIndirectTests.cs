using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

// The classes of the join indexes on the id of the inner documents (o.A = id(i)), run with the join indexes in the storage mode Indirect: the same tests and the same oracle as in the Expanded mode.

public class JoinIndexQueryTestsIndirect(ITestOutputHelper output) : JoinIndexQueryTests(output), IIndirectModeTest;

public class JoinIndexMultiKeyQueryTestsIndirect(ITestOutputHelper output) : JoinIndexMultiKeyQueryTests(output), IIndirectModeTest;

public class JoinIndexQueryPathsTestsIndirect(ITestOutputHelper output) : JoinIndexQueryPathsTests(output), IIndirectModeTest;

public class JoinIndexMultiKeyQueryPathsTestsIndirect(ITestOutputHelper output) : JoinIndexMultiKeyQueryPathsTests(output), IIndirectModeTest;

public class JoinIndexLeftJoinQueryTestsIndirect(ITestOutputHelper output) : JoinIndexLeftJoinQueryTests(output), IIndirectModeTest;

public class JoinIndexUnscoredKindQueryTestsIndirect(ITestOutputHelper output) : JoinIndexUnscoredKindQueryTests(output), IIndirectModeTest;

public class JoinIndexQueryLifecycleTestsIndirect(ITestOutputHelper output) : JoinIndexQueryLifecycleTests(output), IIndirectModeTest;

public class JoinIndexScoreProbeTestsIndirect(ITestOutputHelper output) : JoinIndexScoreProbeTests(output), IIndirectModeTest;

public class JoinIndexTwoJoinQueryTestsIndirect(ITestOutputHelper output) : JoinIndexTwoJoinQueryTests(output), IIndirectModeTest;

public class JoinIndexJoinCombinationTestsIndirect(ITestOutputHelper output) : JoinIndexJoinCombinationTests(output), IIndirectModeTest;

public class JoinIndexQueryTestsIndirectCacheOn(ITestOutputHelper output) : JoinIndexQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.On; }

public class JoinIndexQueryTestsIndirectCacheTiny(ITestOutputHelper output) : JoinIndexQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Tiny; }

public class JoinIndexQueryTestsIndirectCacheOff(ITestOutputHelper output) : JoinIndexQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Off; }

public class JoinIndexMultiKeyQueryTestsIndirectCacheOn(ITestOutputHelper output) : JoinIndexMultiKeyQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.On; }

public class JoinIndexMultiKeyQueryTestsIndirectCacheTiny(ITestOutputHelper output) : JoinIndexMultiKeyQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Tiny; }

public class JoinIndexMultiKeyQueryTestsIndirectCacheOff(ITestOutputHelper output) : JoinIndexMultiKeyQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Off; }
