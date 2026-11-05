using Sparrow.Server;
using Voron;

namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>The names of the structures as slices that live as long as the process, so that opening a snapshot allocates no names.</summary>
public static class JoinNames
{
    public static readonly Slice Meta;
    public static readonly Slice OuterDocuments;
    public static readonly Slice InnerDocuments;
    public static readonly Slice OuterToInner;
    public static readonly Slice OuterFingerprints;
    public static readonly Slice KeyTerms;
    public static readonly Slice InnerKeys;
    public static readonly Slice KeyAtoms;
    public static readonly Slice KeyCounts;
    public static readonly Slice Lists;
    public static readonly Slice FormatRow;
    public static readonly Slice SchemaRow;
    public static readonly Slice SeedRow;
    public static readonly Slice GenerationRow;
    public static readonly Slice SeveralRow;
    public static readonly Slice SeveralKeysRow;
    public static readonly Slice[] Terms;
    public static readonly Slice[] ListHandles;
    public static readonly Slice[] InnerTerms;
    public static readonly Slice[] Slots;

    static JoinNames()
    {
        using (StorageEnvironment.GetStaticContext(out ByteStringContext context))
        {
            Meta = Create(context, JoinStorageFormat.Meta);
            OuterDocuments = Create(context, JoinStorageFormat.OuterDocuments);
            InnerDocuments = Create(context, JoinStorageFormat.InnerDocuments);
            OuterToInner = Create(context, JoinStorageFormat.OuterToInner);
            OuterFingerprints = Create(context, JoinStorageFormat.OuterFingerprints);
            KeyTerms = Create(context, JoinStorageFormat.KeyTerms);
            InnerKeys = Create(context, JoinStorageFormat.InnerKeys);
            KeyAtoms = Create(context, JoinStorageFormat.KeyAtoms);
            KeyCounts = Create(context, JoinStorageFormat.KeyCounts);
            Lists = Create(context, JoinStorageFormat.Lists);
            FormatRow = Create(context, JoinStorageFormat.FormatRow);
            SchemaRow = Create(context, JoinStorageFormat.SchemaRow);
            SeedRow = Create(context, JoinStorageFormat.SeedRow);
            GenerationRow = Create(context, JoinStorageFormat.GenerationRow);
            SeveralRow = Create(context, JoinStorageFormat.SeveralRow);
            SeveralKeysRow = Create(context, JoinStorageFormat.SeveralKeysRow);

            Terms = new Slice[JoinStorageFormat.MaxFields];
            ListHandles = new Slice[JoinStorageFormat.MaxFields];
            InnerTerms = new Slice[JoinStorageFormat.MaxFields];
            for (int field = 0; field < JoinStorageFormat.MaxFields; field++)
            {
                Terms[field] = Create(context, JoinStorageFormat.Terms(field));
                ListHandles[field] = Create(context, JoinStorageFormat.ListHandles(field));
                InnerTerms[field] = Create(context, JoinStorageFormat.InnerTerms(field));
            }

            Slots = new Slice[3 + 2 * JoinStorageFormat.MaxGroupings];
            for (int slot = 0; slot < Slots.Length; slot++)
                Slots[slot] = Create(context, JoinStorageFormat.SlotRowPrefix + slot);
        }
    }

    private static Slice Create(ByteStringContext context, string name)
    {
        Slice.From(context, name, ByteStringType.Immutable, out Slice slice);
        return slice;
    }
}
