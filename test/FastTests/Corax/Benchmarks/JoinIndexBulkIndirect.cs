using Raven.Client.Documents.Indexes;

namespace FastTests.Corax;

public partial class JoinIndexProductionBenchmark
{
    // RAVEN_JOIN_PROD_STORAGE=Indirect (with the setting Join.IndirectEnabled=true in RAVEN_JOIN_PROD_SETTINGS) builds the field join indexes of the harness in the Indirect mode
    static partial void ApplyStorageMode(IndexDefinition definition)
    {
        if (System.Environment.GetEnvironmentVariable("RAVEN_JOIN_PROD_STORAGE") == "Indirect")
            definition.Join.StorageMode = JoinStorageMode.Indirect;
    }

    // RAVEN_JOIN_PROD_IDSTORAGE=Indirect builds the join index on the company ids (a join on the id of the inner documents) in the Indirect mode
    static partial void ApplyIdStorageMode(IndexDefinition definition)
    {
        if (System.Environment.GetEnvironmentVariable("RAVEN_JOIN_PROD_IDSTORAGE") == "Indirect")
            definition.Join.StorageMode = JoinStorageMode.Indirect;
    }
}
