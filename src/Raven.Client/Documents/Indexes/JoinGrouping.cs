using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Indexes;

/// <summary>An equality atom (field = term) that is kept as a list of its own.</summary>
public sealed class JoinGrouping
{
    /// <summary>The name of the field in the inner index.</summary>
    public string Field { get; set; }

    /// <summary>The term as it is indexed, for example 'true' for a boolean field.</summary>
    public string Value { get; set; }

    /// <summary>The grouping as it is sent to the server.</summary>
    public DynamicJsonValue ToJson()
    {
        return new DynamicJsonValue
        {
            [nameof(Field)] = Field,
            [nameof(Value)] = Value
        };
    }

    /// <summary>Reads a grouping from its JSON form.</summary>
    public static JoinGrouping FromJson(BlittableJsonReaderObject json)
    {
        json.TryGet(nameof(Field), out string field);
        json.TryGet(nameof(Value), out string value);

        return new JoinGrouping { Field = field, Value = value };
    }
}
