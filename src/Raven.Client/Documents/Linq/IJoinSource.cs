using Raven.Client.Documents.Queries;

namespace Raven.Client.Documents.Linq
{
    /// <summary>A query that can be the source of a Join: it renders its own index query, for the kind of session it was created by.</summary>
    internal interface IJoinSource
    {
        IndexQuery GetIndexQuery();
    }
}
