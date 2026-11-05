using System;
using Raven.Client.Documents.Indexes;
using Raven.Client.ServerWide;
using Raven.Server.Extensions;
using Raven.Server.Rachis;
using Sparrow.Json.Parsing;

namespace Raven.Server.ServerWide.Commands.Indexes
{
    /// <summary>
    /// Puts the definition of a join index into the database record. It is a command of its own, and not a <see cref="PutIndexCommand"/> that carries a join index section,
    /// because a node that does not know the section cannot read the record: the version of the command keeps it away from such a node.
    /// </summary>
    public sealed class PutJoinIndexCommand : UpdateDatabaseRecordFeaturesCommand
    {
        public IndexDefinition Definition;

        public IndexDeploymentMode? DefaultDeploymentMode;

        public PutJoinIndexCommand()
        {
            // for deserialization
        }

        public PutJoinIndexCommand(IndexDefinition definition, string databaseName, string source, DateTime createdAt, string uniqueRequestId, int revisionsToKeep, IndexDeploymentMode deploymentMode)
            : base(databaseName, uniqueRequestId)
        {
            Definition = definition;
            Definition.ClusterState ??= new IndexDefinitionClusterState();
            Source = source;
            CreatedAt = createdAt;
            RevisionsToKeep = revisionsToKeep;
            DefaultDeploymentMode = deploymentMode;
        }

        public DateTime CreatedAt { get; set; }

        public string Source { get; set; }

        public int RevisionsToKeep { get; set; }

        public override void UpdateDatabaseRecord(DatabaseRecord record, long etag)
        {
            if (Definition.Join == null)
                throw new RachisApplyException($"The index '{Definition.Name}' has no join index section");

            new PutIndexCommand.StaticIndexNameValidator(record).Validate(Definition);

            try
            {
                record.AddIndex(Definition, Source, CreatedAt, etag, RevisionsToKeep, DefaultDeploymentMode ?? IndexDeploymentMode.Parallel);
            }
            catch (Exception e)
            {
                throw new RachisApplyException($"Failed to update index '{Definition.Name}'", e);
            }
        }

        public override void FillJson(DynamicJsonValue json)
        {
            json[nameof(Definition)] = Definition.ToJson();
            json[nameof(Source)] = Source;
            json[nameof(CreatedAt)] = CreatedAt;
            json[nameof(RevisionsToKeep)] = RevisionsToKeep;
            json[nameof(DefaultDeploymentMode)] = DefaultDeploymentMode;
        }

        public override string AdditionalDebugInformation(Exception exception)
        {
            string message = $"Join index name: '{Definition.Name}' for database '{DatabaseName}'";
            if (exception != null)
                message += $" Exception: {exception}.";

            return message;
        }

        public override bool Disabled => Definition.State == IndexState.Disabled;
    }
}
