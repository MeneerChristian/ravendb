using System;
using System.Collections.Generic;
using System.Linq;

namespace Raven.Client.Documents.Indexes;

/// <summary>Thrown when the definition of a join index is not valid. It carries every problem that was found.</summary>
public sealed class JoinValidationException : Exception
{
    /// <summary>The problems that were found.</summary>
    public IReadOnlyList<JoinValidationError> Errors { get; }

    /// <summary>Creates the exception for the problems that were found.</summary>
    public JoinValidationException(IReadOnlyList<JoinValidationError> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>Creates the exception for a single problem.</summary>
    public JoinValidationException(JoinValidationCode code, string message)
        : this(new[] { new JoinValidationError(code, message) })
    {
    }

    /// <summary>The message of the exception: one line for each problem.</summary>
    public static string BuildMessage(IReadOnlyList<JoinValidationError> errors)
    {
        return string.Join(Environment.NewLine, errors.Select(x => x.ToString()));
    }
}
