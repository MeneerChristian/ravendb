using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Indexes;

/// <summary>
/// What a join index keeps up to date so that a JOIN of the outer index with the inner index on id() can be answered without scanning the inner index.
/// It is the <see cref="IndexDefinition.Join"/> section of the definition of a join index; the name, the priority, the state and the other settings are the ones of the index.
/// </summary>
public class JoinDefinition
{
    /// <summary>The index of the query that holds the JOIN. For an auto index (<see cref="OuterCollection"/>) it is optional: the index the join index prefers.</summary>
    public string OuterIndex { get; set; }

    /// <summary>
    /// Set when the outer index is an auto-map index of this collection. The server then binds the join index to the auto-map index of the collection that has the field the join index needs
    /// (<see cref="OuterField"/>). Null for a named index.
    /// </summary>
    public string OuterCollection { get; set; }

    /// <summary>The field of the outer index that holds the key of the joined documents: their id, or the value of <see cref="InnerField"/>.</summary>
    public string OuterField { get; set; }

    /// <summary>The value of <see cref="OuterField"/> that stands for the id of the outer document (<c>JOIN ... ON id(m) = c.B</c>) instead of a field of the outer index.</summary>
    public const string OuterIdKey = Constants.Documents.Indexing.Fields.DocumentIdFieldName;

    /// <summary>True when the outer key is the lowercased id of the outer document itself: <see cref="OuterField"/> is <see cref="OuterIdKey"/>.</summary>
    [JsonIgnore]
    public bool JoinsOnOuterId => string.Equals(OuterField, OuterIdKey, StringComparison.Ordinal);

    /// <summary>
    /// The field of the inner index whose value the outer key is joined with (<c>JOIN ... ON o.A = i.B</c>). Null for a join on the id of the inner documents (<c>ON o.A = id(i)</c>).
    /// Many outer entries and many inner entries may share a key.
    /// </summary>
    public string InnerField { get; set; }

    /// <summary>The index of the joined documents. For an auto index (<see cref="InnerCollection"/>) it is optional: the index the join index prefers.</summary>
    public string InnerIndex { get; set; }

    /// <summary>Like <see cref="OuterCollection"/> for the joined index: it needs the declared fields and the fields of the groupings.</summary>
    public string InnerCollection { get; set; }

    /// <summary>What identifies the outer side: the collection of an auto index, else the name of the index, in lower case.</summary>
    [JsonIgnore]
    public string OuterIdentity => SideIdentity(OuterCollection, OuterIndex);

    /// <summary>What identifies the inner side: the collection of an auto index, else the name of the index, in lower case.</summary>
    [JsonIgnore]
    public string InnerIdentity => SideIdentity(InnerCollection, InnerIndex);

    private static string SideIdentity(string collection, string index)
    {
        return string.IsNullOrWhiteSpace(collection) == false ? "auto:" + collection.ToLowerInvariant() : (index ?? string.Empty).ToLowerInvariant();
    }

    /// <summary>How the lists of the join index hold what a sub-query matches. <see cref="JoinStorageMode.Auto"/>, the default, leaves the choice to the server.</summary>
    public JoinStorageMode StorageMode { get; set; }

    /// <summary>The fields of the inner index that sub-queries filter by, at most 8. The order is part of the definition.</summary>
    public List<JoinField> Fields { get; set; } = new();

    /// <summary>The equalities that are kept as lists of their own, at most 4.</summary>
    public List<JoinGrouping> Groupings { get; set; } = new();

    /// <summary>Declares a field of the inner index.</summary>
    public JoinDefinition AddField(string field, JoinFieldKind kind = JoinFieldKind.Exact)
    {
        Fields.Add(new JoinField { Field = field, Kind = kind });
        return this;
    }

    /// <summary>Declares a grouping: <paramref name="field"/> equals <paramref name="value"/>, the term as the index writes it.</summary>
    public JoinDefinition AddGrouping(string field, string value)
    {
        Groupings.Add(new JoinGrouping { Field = field, Value = value });
        return this;
    }

    /// <summary>
    /// True when the two sections declare the same join: the names of the indexes are compared without regard to case, the order of the fields and of the groupings counts
    /// (the server keeps them in that order). Two null sections are the same.
    /// </summary>
    public static bool IsSameJoin(JoinDefinition left, JoinDefinition right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left == null || right == null)
            return false;

        return left.IsSameJoinAs(right);
    }

    /// <summary>True when the other definition declares the same join, see <see cref="IsSameJoin(JoinDefinition,JoinDefinition)"/>.</summary>
    protected bool IsSameJoinAs(JoinDefinition other)
    {
        if (string.Equals(OuterIndex, other.OuterIndex, StringComparison.OrdinalIgnoreCase) == false ||
            string.Equals(OuterCollection, other.OuterCollection, StringComparison.OrdinalIgnoreCase) == false ||
            string.Equals(OuterField, other.OuterField, StringComparison.Ordinal) == false ||
            string.Equals(InnerField, other.InnerField, StringComparison.Ordinal) == false ||
            string.Equals(InnerIndex, other.InnerIndex, StringComparison.OrdinalIgnoreCase) == false ||
            string.Equals(InnerCollection, other.InnerCollection, StringComparison.OrdinalIgnoreCase) == false ||
            StorageMode != other.StorageMode)
            return false;

        return SameItems(Fields, other.Fields, static (a, b) => a.Kind == b.Kind && string.Equals(a.Field, b.Field, StringComparison.Ordinal)) &&
               SameItems(Groupings, other.Groupings, static (a, b) => string.Equals(a.Field, b.Field, StringComparison.Ordinal) && string.Equals(a.Value, b.Value, StringComparison.Ordinal));
    }

    private static bool SameItems<T>(List<T> left, List<T> right, Func<T, T, bool> same) where T : class
    {
        if (left == null || right == null)
            return ReferenceEquals(left, right);

        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] == null || right[i] == null)
            {
                if (ReferenceEquals(left[i], right[i]) == false)
                    return false;

                continue;
            }

            if (same(left[i], right[i]) == false)
                return false;
        }

        return true;
    }

    /// <summary>A hash code that agrees with <see cref="IsSameJoin(JoinDefinition,JoinDefinition)"/>.</summary>
    public int GetJoinHashCode()
    {
        HashCode hash = new();
        hash.Add(OuterIndex, StringComparer.OrdinalIgnoreCase);
        hash.Add(OuterCollection, StringComparer.OrdinalIgnoreCase);
        hash.Add(OuterField, StringComparer.Ordinal);
        hash.Add(InnerField, StringComparer.Ordinal);
        hash.Add(InnerIndex, StringComparer.OrdinalIgnoreCase);
        hash.Add(InnerCollection, StringComparer.OrdinalIgnoreCase);
        hash.Add(StorageMode);
        foreach (JoinField field in Fields)
            hash.Add(field?.Field, StringComparer.Ordinal);

        foreach (JoinGrouping grouping in Groupings)
            hash.Add(grouping?.Field, StringComparer.Ordinal);

        return hash.ToHashCode();
    }

    /// <summary>A copy that can be changed without changing this definition.</summary>
    public JoinDefinition Clone()
    {
        var copy = new JoinDefinition();
        CopyTo(copy);
        return copy;
    }

    /// <summary>Copies the join into <paramref name="copy"/>: the sides, the key, the fields and the groupings.</summary>
    public void CopyTo(JoinDefinition copy)
    {
        copy.OuterIndex = OuterIndex;
        copy.OuterCollection = OuterCollection;
        copy.OuterField = OuterField;
        copy.InnerField = InnerField;
        copy.InnerIndex = InnerIndex;
        copy.InnerCollection = InnerCollection;
        copy.StorageMode = StorageMode;
        copy.Fields = new List<JoinField>(Fields.Count);
        copy.Groupings = new List<JoinGrouping>(Groupings.Count);

        foreach (JoinField field in Fields)
            copy.Fields.Add(field == null ? null : new JoinField { Field = field.Field, Kind = field.Kind });

        foreach (JoinGrouping grouping in Groupings)
            copy.Groupings.Add(grouping == null ? null : new JoinGrouping { Field = grouping.Field, Value = grouping.Value });
    }

    /// <summary>The JSON of the join, as it is stored in the definition of the index.</summary>
    public DynamicJsonValue ToJson()
    {
        DynamicJsonValue json = new();
        WriteSpecTo(json);
        return json;
    }

    /// <summary>Writes the sides, the key, the fields and the groupings.</summary>
    protected void WriteSpecTo(DynamicJsonValue json)
    {
        var fields = new DynamicJsonArray();
        foreach (JoinField field in Fields)
            fields.Add(field.ToJson());

        var groupings = new DynamicJsonArray();
        foreach (JoinGrouping grouping in Groupings)
            groupings.Add(grouping.ToJson());

        json[nameof(OuterIndex)] = OuterIndex;
        json[nameof(OuterCollection)] = OuterCollection;
        json[nameof(OuterField)] = OuterField;
        if (InnerField != null)
            json[nameof(InnerField)] = InnerField;

        json[nameof(InnerIndex)] = InnerIndex;
        json[nameof(InnerCollection)] = InnerCollection;
        if (StorageMode != JoinStorageMode.Auto)
            json[nameof(StorageMode)] = StorageMode.ToString();

        json[nameof(Fields)] = fields;
        json[nameof(Groupings)] = groupings;
    }

    /// <summary>Reads what <see cref="ToJson"/> wrote. Throws <see cref="JoinValidationException"/> for a form that is not valid.</summary>
    public static JoinDefinition FromJson(BlittableJsonReaderObject json)
    {
        var definition = new JoinDefinition();
        ReadSpecFrom(json, definition);
        return definition;
    }

    /// <summary>Reads the sides, the key, the fields and the groupings into <paramref name="definition"/>.</summary>
    protected static void ReadSpecFrom(BlittableJsonReaderObject json, JoinDefinition definition)
    {
        json.TryGet(nameof(OuterIndex), out string outerIndex);
        definition.OuterIndex = outerIndex;
        json.TryGet(nameof(OuterField), out string outerField);
        definition.OuterField = outerField;
        json.TryGet(nameof(InnerField), out string innerField);
        definition.InnerField = innerField;
        json.TryGet(nameof(InnerIndex), out string innerIndex);
        definition.InnerIndex = innerIndex;
        json.TryGet(nameof(OuterCollection), out string outerCollection);
        definition.OuterCollection = outerCollection;
        json.TryGet(nameof(InnerCollection), out string innerCollection);
        definition.InnerCollection = innerCollection;

        definition.StorageMode = JoinStorageMode.Auto;
        if (json.TryGet(nameof(StorageMode), out string storageMode) && string.IsNullOrEmpty(storageMode) == false)
        {
            if (Enum.TryParse(storageMode, ignoreCase: false, out JoinStorageMode mode) == false || Enum.IsDefined(typeof(JoinStorageMode), mode) == false)
                throw new JoinValidationException(JoinValidationCode.InvalidDefinition, $"The storage mode '{storageMode}' is unknown. Expected one of: {string.Join(", ", Enum.GetNames(typeof(JoinStorageMode)))}.");

            definition.StorageMode = mode;
        }

        definition.Fields = new List<JoinField>();
        if (json.TryGet(nameof(Fields), out BlittableJsonReaderArray fields))
        {
            foreach (object item in fields)
                definition.Fields.Add(JoinField.FromJson(AsObject(item, nameof(Fields))));
        }

        definition.Groupings = new List<JoinGrouping>();
        if (json.TryGet(nameof(Groupings), out BlittableJsonReaderArray groupings))
        {
            foreach (object item in groupings)
                definition.Groupings.Add(JoinGrouping.FromJson(AsObject(item, nameof(Groupings))));
        }
    }

    private static BlittableJsonReaderObject AsObject(object item, string property)
    {
        return item as BlittableJsonReaderObject ?? throw new JoinValidationException(JoinValidationCode.InvalidDefinition, $"The items of '{property}' must be objects.");
    }

    /// <summary>The rules that do not need the server: the sides, the key, counts, duplicates. The name of the join index is the name of the index and is not checked here.</summary>
    public List<JoinValidationError> ValidateShape()
    {
        var errors = new List<JoinValidationError>();
        ValidateSpecShape(errors);
        return errors;
    }

    /// <summary>Adds the errors of the rules that do not need the server to <paramref name="errors"/>.</summary>
    protected void ValidateSpecShape(List<JoinValidationError> errors)
    {
        ValidateSide(errors, OuterIndex, OuterCollection, nameof(OuterIndex), nameof(OuterCollection));
        Require(errors, OuterField, nameof(OuterField));
        if (InnerField != null)
            Require(errors, InnerField, nameof(InnerField));
        else if (JoinsOnOuterId)
            errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"A join on the id of the outer document ({nameof(OuterField)} '{OuterIdKey}') joins it with a field of the inner index: {nameof(InnerField)} is required."));

        ValidateSide(errors, InnerIndex, InnerCollection, nameof(InnerIndex), nameof(InnerCollection));

        if (Enum.IsDefined(typeof(JoinStorageMode), StorageMode) == false)
            errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"The storage mode {(int)StorageMode} is unknown."));
        else if (StorageMode == JoinStorageMode.Indirect && JoinsOnOuterId)
            errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"The storage mode {nameof(JoinStorageMode.Indirect)} is for a join of a field of the outer index with a field of the inner index or with the id of the inner documents, not for a join on the id of the outer document."));

        if (Fields == null || Groupings == null)
        {
            errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, "Fields and Groupings must not be null."));
            return;
        }

        if (Fields.Count > JoinLimits.MaxFields)
            errors.Add(new JoinValidationError(JoinValidationCode.LimitExceeded, $"A join index declares at most {JoinLimits.MaxFields} fields, this one declares {Fields.Count}."));

        if (Groupings.Count > JoinLimits.MaxGroupings)
            errors.Add(new JoinValidationError(JoinValidationCode.LimitExceeded, $"A join index declares at most {JoinLimits.MaxGroupings} groupings, this one declares {Groupings.Count}."));

        var seenFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (JoinField field in Fields)
        {
            if (field == null || string.IsNullOrWhiteSpace(field.Field))
            {
                errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, "A field needs a name."));
                continue;
            }

            if (Enum.IsDefined(typeof(JoinFieldKind), field.Kind) == false)
                errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"The field '{field.Field}' has an unknown kind."));

            if (seenFields.Add(field.Field) == false)
                errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"The field '{field.Field}' is declared more than once."));
        }

        var seenGroupings = new HashSet<(string, string)>();
        foreach (JoinGrouping grouping in Groupings)
        {
            if (grouping == null || string.IsNullOrWhiteSpace(grouping.Field) || grouping.Value == null)
            {
                errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, "A grouping needs a field and a value."));
                continue;
            }

            if (Encoding.UTF8.GetByteCount(grouping.Value) > JoinLimits.MaxTermLength)
                errors.Add(new JoinValidationError(JoinValidationCode.LimitExceeded, $"The value of the grouping on '{grouping.Field}' is longer than {JoinLimits.MaxTermLength} bytes."));

            if (seenGroupings.Add((grouping.Field, grouping.Value)) == false)
                errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"The grouping '{grouping.Field}' = '{grouping.Value}' is declared more than once."));
        }
    }

    private static void ValidateSide(List<JoinValidationError> errors, string index, string collection, string indexProperty, string collectionProperty)
    {
        if (string.IsNullOrWhiteSpace(collection))
        {
            if (collection != null)
                errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"{collectionProperty} must be null or a collection name."));

            Require(errors, index, indexProperty);
            return;
        }

        if (string.IsNullOrWhiteSpace(index) == false && index.StartsWith("Auto/", StringComparison.OrdinalIgnoreCase) == false)
            errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"{collectionProperty} says that the side is an auto index, but {indexProperty} '{index}' is not the name of one (Auto/...)."));
    }

    private static void Require(List<JoinValidationError> errors, string value, string property)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add(new JoinValidationError(JoinValidationCode.InvalidDefinition, $"{property} is required."));
    }
}
