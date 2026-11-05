using System;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Indexes;

/// <summary>A field of the inner index that sub-queries may filter by.</summary>
public sealed class JoinField
{
    /// <summary>The name of the field in the inner index.</summary>
    public string Field { get; set; }

    /// <summary>How the field is indexed.</summary>
    public JoinFieldKind Kind { get; set; }

    /// <summary>The field as it is sent to the server.</summary>
    public DynamicJsonValue ToJson()
    {
        return new DynamicJsonValue
        {
            [nameof(Field)] = Field,
            [nameof(Kind)] = Kind.ToString()
        };
    }

    /// <summary>Reads a field from its JSON form. Throws <see cref="JoinValidationException"/> when the kind is missing or unknown.</summary>
    public static JoinField FromJson(BlittableJsonReaderObject json)
    {
        json.TryGet(nameof(Field), out string field);

        if (json.TryGet(nameof(Kind), out string kindText) == false || Enum.TryParse(kindText, ignoreCase: false, out JoinFieldKind kind) == false)
            throw new JoinValidationException(JoinValidationCode.InvalidDefinition, $"The field '{field}' has no valid kind. Expected one of: {string.Join(", ", Enum.GetNames(typeof(JoinFieldKind)))}.");

        return new JoinField { Field = field, Kind = kind };
    }
}
