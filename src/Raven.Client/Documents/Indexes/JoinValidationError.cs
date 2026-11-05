namespace Raven.Client.Documents.Indexes;

/// <summary>One problem found in the definition of a join index.</summary>
public sealed class JoinValidationError(JoinValidationCode code, string message)
{
    /// <summary>The kind of the problem.</summary>
    public JoinValidationCode Code { get; } = code;

    /// <summary>What is wrong and how to fix it.</summary>
    public string Message { get; } = message;

    /// <summary>The code and the message.</summary>
    public override string ToString() => $"[{Code}] {Message}";
}
