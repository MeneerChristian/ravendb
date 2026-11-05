namespace Corax.Utils;

public class CoraxTestingConfiguration
{
    public bool DisableVectorSearchScanning { get; set; }
    public bool IsAccelerated { get; set; } = true;
    public bool ForceSortingUsingIndex;

    // Bytes of the buffer that scores a term in memory; a term with more entries is scored from its posting list. Null keeps the default.
    public int? Bm25MaxSizeOfStorage;
}
