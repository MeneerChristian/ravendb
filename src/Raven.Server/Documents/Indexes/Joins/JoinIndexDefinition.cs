using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raven.Client.Documents.Indexes;
using Raven.Server.Extensions;
using Raven.Server.Json;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Server.Json.Sync;
using Voron;
using Voron.Impl;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>
/// The definition of a join index on the server. The collections are the ones of the two indexes it joins, resolved when the join index is created and persisted with it,
/// so that a join index is opened without the indexes it joins.
/// </summary>
public sealed class JoinIndexDefinition : IndexDefinitionBaseServerSide<JoinIndexField>
{
    /// <summary>The definition as it is stored in the database record.</summary>
    public readonly IndexDefinition IndexDefinition;

    /// <summary><see cref="IndexType.Join"/> for a join index that was put, <see cref="IndexType.AutoJoin"/> for one that a query created.</summary>
    public readonly IndexType Type;

    public JoinIndexDefinition(IndexDefinition definition, IEnumerable<string> collections, long indexVersion, IndexType type = IndexType.Join)
        : base(definition.Name, collections, definition.LockMode ?? IndexLockMode.Unlock, definition.Priority ?? IndexPriority.Normal,
            definition.State ?? IndexState.Normal, GetFields(definition), indexVersion, definition.DeploymentMode, definition.ClusterState,
            definition.ArchivedDataProcessingBehavior)
    {
        IndexDefinition = definition ?? throw new ArgumentNullException(nameof(definition));
        Type = type is IndexType.Join or IndexType.AutoJoin ? type : throw new ArgumentOutOfRangeException(nameof(type), type, null);
    }

    private static JoinIndexField[] GetFields(IndexDefinition definition)
    {
        return definition.Join.Fields.Select((x, i) => new JoinIndexField { Name = x.Field, Id = i + 1, Kind = x.Kind }).ToArray();
    }

    protected override void PersistFields(JsonOperationContext context, AbstractBlittableJsonTextWriter writer)
    {
        DynamicJsonValue builder = IndexDefinition.ToJson();
        using (BlittableJsonReaderObject json = context.ReadObject(builder, nameof(IndexDefinition), BlittableJsonDocumentBuilder.UsageMode.ToDisk))
        {
            writer.WritePropertyName(nameof(IndexDefinition));
            writer.WriteObject(json);
        }
    }

    protected override void PersistMapFields(JsonOperationContext context, AbstractBlittableJsonTextWriter writer)
    {
        writer.WritePropertyName(nameof(MapFields));
        writer.WriteStartArray();
        bool first = true;
        foreach (JoinIndexField field in MapFields.Values.Select(x => x.As<JoinIndexField>()))
        {
            if (first == false)
                writer.WriteComma();

            first = false;
            writer.WriteString(field.Name);
        }

        writer.WriteEndArray();
    }

    protected internal override IndexDefinition GetOrCreateIndexDefinitionInternal()
    {
        var definition = new IndexDefinition();
        IndexDefinition.CopyTo(definition);

        definition.Name = Name;
        definition.Type = Type;
        definition.LockMode = LockMode;
        definition.ArchivedDataProcessingBehavior = ArchivedDataProcessingBehavior;
        definition.Priority = Priority;
        definition.State = State;
        return definition;
    }

    public override IndexDefinitionCompareDifferences Compare(IndexDefinitionBaseServerSide indexDefinition)
    {
        return IndexDefinitionCompareDifferences.All;
    }

    public override IndexDefinitionCompareDifferences Compare(IndexDefinition indexDefinition)
    {
        return IndexDefinition.Compare(indexDefinition);
    }

    protected override int ComputeRestOfHash(int hashCode)
    {
        return hashCode * 397 ^ IndexDefinition.GetHashCode();
    }

    /// <summary>The definition of an auto join index as it is kept in the database record.</summary>
    public AutoIndexDefinition ToAutoIndexDefinition()
    {
        return new AutoIndexDefinition
        {
            Name = Name,
            Type = IndexType.AutoJoin,
            Collection = Collections.First(),
            Join = IndexDefinition.Join.Clone(),
            Priority = Priority,
            State = State
        };
    }

    /// <summary>Reads the definition and the collections that were persisted with the index.</summary>
    public static JoinIndexDefinition Load(StorageEnvironment environment, IndexType type = IndexType.Join)
    {
        using (JsonOperationContext context = JsonOperationContext.ShortTermSingleUse())
        using (Transaction tx = environment.ReadTransaction())
        using (Stream stream = GetIndexDefinitionStream(environment, tx))
        using (BlittableJsonReaderObject reader = context.Sync.ReadForDisk(stream, "index/def"))
        {
            if (reader.TryGet(nameof(IndexDefinition), out BlittableJsonReaderObject json) == false || json == null)
                throw new InvalidOperationException("No persisted definition");

            IndexDefinition definition = JsonDeserializationServer.IndexDefinition(json);
            definition.Name = ReadName(reader);
            definition.LockMode = ReadLockMode(reader);
            definition.Priority = ReadPriority(reader);
            definition.State = ReadState(reader);
            definition.ArchivedDataProcessingBehavior = ReadArchivedDataProcessingBehavior(reader);

            return new JoinIndexDefinition(definition, ReadCollections(reader), ReadVersion(reader), type);
        }
    }
}
