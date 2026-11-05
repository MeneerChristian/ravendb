namespace Raven.Client.Documents.Indexes;

/// <summary>Why a join index definition is refused. The client checks the codes that need no server, the server checks all of them.</summary>
public enum JoinValidationCode
{
    /// <summary>A required part is missing or a part is declared twice.</summary>
    InvalidDefinition,

    /// <summary>The outer or the inner index does not exist.</summary>
    IndexMissing,

    /// <summary>The outer and the inner index are the same index.</summary>
    SameIndex,

    /// <summary>An index does not use the Corax search engine.</summary>
    UnsupportedSearchEngine,

    /// <summary>The database is sharded.</summary>
    ShardedDatabase,

    /// <summary>An index is not a map index.</summary>
    UnsupportedIndexKind,

    /// <summary>An index creates several entries for one document.</summary>
    MultipleOutputs,

    /// <summary>An index does not read documents only: counters, time series, LoadDocument, compare exchange values.</summary>
    UnsupportedSource,

    /// <summary>A declared field is not an indexed field of its index.</summary>
    FieldNotIndexed,

    /// <summary>The kind of a field does not fit the field (or the type of the property that declares it).</summary>
    FieldKindMismatch,

    /// <summary>The join key is not a field that a JOIN on id() can use.</summary>
    OuterFieldNotSupported,

    /// <summary>More fields or groupings than allowed, or a value that is too long.</summary>
    LimitExceeded,

    /// <summary>The inner index has boosted fields.</summary>
    BoostedFields,

    /// <summary>An expression of a declaration in code cannot be translated into a field name or a grouping value.</summary>
    UnsupportedExpression,

    /// <summary>The inner key of a join on a field is not a field that a join can compare with the outer key.</summary>
    InnerFieldNotSupported
}
