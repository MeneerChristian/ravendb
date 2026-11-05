using Raven.Client.Documents.Indexes;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>A field of the inner index that a join index declares. It has a name and a kind and no storage of its own: the join index does not index entries.</summary>
public sealed class JoinIndexField : IndexFieldBase
{
    /// <summary>The id of the field in the join index: its position in the order of the declaration, starting at 1.</summary>
    public int Id { get; set; }

    /// <summary>How the field is indexed in the inner index.</summary>
    public JoinFieldKind Kind { get; set; }
}
