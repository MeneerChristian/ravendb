using System;
using System.Collections.Generic;
using Raven.Client.Documents.Indexes;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>Validates the definition of a join index against the indexes it joins, before the definition is put into the database record.</summary>
public sealed class JoinIndexValidator
{
    private readonly Func<string, JoinIndexFacts> _getIndex;
    private readonly bool _isShardedDatabase;
    private readonly bool _indirectEnabled;

    /// <param name="getIndex">The index with this name, null when there is none.</param>
    /// <param name="isShardedDatabase">Whether the database is sharded.</param>
    /// <param name="indirectEnabled">Whether the server allows the storage mode <see cref="JoinStorageMode.Indirect"/> (Join.IndirectEnabled).</param>
    public JoinIndexValidator(Func<string, JoinIndexFacts> getIndex, bool isShardedDatabase, bool indirectEnabled = false)
    {
        _indirectEnabled = indirectEnabled;
        _getIndex = getIndex ?? throw new ArgumentNullException(nameof(getIndex));
        _isShardedDatabase = isShardedDatabase;
    }

    /// <summary>The errors of the join of a join index; empty when it can be put. The name of the index is checked with the rules of every index.</summary>
    public List<JoinValidationError> Validate(JoinDefinition joinIndex)
    {
        if (joinIndex == null)
            throw new ArgumentNullException(nameof(joinIndex));

        List<JoinValidationError> errors = joinIndex.ValidateShape();
        if (errors.Count > 0)
            return errors;

        if (joinIndex.StorageMode == JoinStorageMode.Indirect && _indirectEnabled == false)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"The storage mode {nameof(JoinStorageMode.Indirect)} is not enabled on this server (Join.IndirectEnabled)."));
            return errors;
        }

        if (_isShardedDatabase)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.ShardedDatabase, "A join index is not supported for a sharded database."));
            return errors;
        }

        if (string.IsNullOrWhiteSpace(joinIndex.OuterCollection) == false || string.IsNullOrWhiteSpace(joinIndex.InnerCollection) == false)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, "A side of a join index that is bound by collection is not supported by a join index: name the index."));
            return errors;
        }

        if (string.Equals(joinIndex.OuterIndex, joinIndex.InnerIndex, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new JoinValidationError(JoinValidationCode.SameIndex, $"The outer and the inner index are both '{joinIndex.OuterIndex}'. A join index of an index with itself is not supported."));
            return errors;
        }

        JoinIndexFacts outer = Resolve(joinIndex.OuterIndex, "outer", errors);
        JoinIndexFacts inner = Resolve(joinIndex.InnerIndex, "inner", errors);

        bool fieldJoin = joinIndex.InnerField != null;
        if (joinIndex.JoinsOnOuterId && fieldJoin == false)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"A join on the id of the outer document joins it with a field of the inner index: {nameof(JoinDefinition.InnerField)} is required."));
            return errors;
        }

        if (outer != null)
        {
            JoinIndexRules.ValidateIndex(outer, "outer", errors);
            if (fieldJoin == false)
                JoinIndexRules.ValidateOuterField(joinIndex.OuterField, outer, errors);
        }

        if (inner != null)
        {
            JoinIndexRules.ValidateIndex(inner, "inner", errors);
            JoinIndexRules.ValidateInnerFields(joinIndex.Fields, joinIndex.Groupings, inner, errors);
        }

        if (fieldJoin && outer != null && inner != null)
            JoinIndexRules.ValidateJoinKeys(joinIndex.OuterField, outer, joinIndex.InnerField, inner, errors);

        return errors;
    }

    private JoinIndexFacts Resolve(string name, string role, List<JoinValidationError> errors)
    {
        JoinIndexFacts facts = _getIndex(name);
        if (facts == null)
            errors.Add(new JoinValidationError(JoinValidationCode.IndexMissing, $"The {role} index '{name}' does not exist."));

        return facts;
    }
}
