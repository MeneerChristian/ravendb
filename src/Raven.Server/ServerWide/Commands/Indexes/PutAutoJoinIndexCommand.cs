using System;
using Raven.Client.Documents.Indexes;
using Raven.Client.ServerWide;
using Raven.Client.Util;
using Raven.Server.Rachis;
using Raven.Server.Utils;
using Sparrow.Json.Parsing;

namespace Raven.Server.ServerWide.Commands.Indexes
{
    /// <summary>
    /// Puts an auto join index (<see cref="IndexType.AutoJoin"/>) into the auto indexes of the database record. It is a command of its own, and not a <see cref="PutAutoIndexCommand"/>,
    /// for the reason that <see cref="PutJoinIndexCommand"/> is one: a node that does not know the join index section cannot read the record, and the version of the command keeps it away from such a node.
    /// </summary>
    public sealed class PutAutoJoinIndexCommand : UpdateDatabaseRecordFeaturesCommand
    {
        public DateTime CreatedAt;
        public AutoIndexDefinition Definition;
        public IndexDeploymentMode? DefaultAutoDeploymentMode;

        public PutAutoJoinIndexCommand()
        {
            // for deserialization
        }

        public PutAutoJoinIndexCommand(AutoIndexDefinition definition, string databaseName, string uniqueRequestId, IndexDeploymentMode mode, DateTime? createdAt = null)
            : base(databaseName, uniqueRequestId)
        {
            Definition = definition;
            CreatedAt = createdAt ?? SystemTime.UtcNow;
            DefaultAutoDeploymentMode = mode;
        }

        public override void UpdateDatabaseRecord(DatabaseRecord record, long etag)
        {
            if (Definition.Type != IndexType.AutoJoin || Definition.Join == null)
                throw new RachisApplyException($"The auto index '{Definition.Name}' is not an auto join index");

            try
            {
                record.AddIndex(Definition, CreatedAt, etag, DefaultAutoDeploymentMode ?? IndexDeploymentMode.Parallel);
            }
            catch (Exception e)
            {
                throw new RachisApplyException($"Failed to update auto join index '{Definition.Name}'", e);
            }
        }

        public override void FillJson(DynamicJsonValue json)
        {
            json[nameof(Definition)] = TypeConverter.ToBlittableSupportedType(Definition);
            json[nameof(CreatedAt)] = TypeConverter.ToBlittableSupportedType(CreatedAt);
            json[nameof(DefaultAutoDeploymentMode)] = TypeConverter.ToBlittableSupportedType(DefaultAutoDeploymentMode);
        }

        public override string AdditionalDebugInformation(Exception exception)
        {
            string message = $"Auto join index name: '{Definition.Name}' for database '{DatabaseName}'";
            if (exception != null)
                message += $" Exception: {exception}.";

            return message;
        }

        public override bool Disabled => Definition.State == IndexState.Disabled;
    }
}
