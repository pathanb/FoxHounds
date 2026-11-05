namespace FoxHounds.Core;

/// <summary>A ply that is not legal in the given position (thrown by <see cref="Rules.ApplyChecked"/>).</summary>
public sealed class IllegalMoveException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public IllegalMoveException()
        : base("Illegal ply.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public IllegalMoveException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public IllegalMoveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A position that breaks the invariants of spec 5.2 (thrown by <see cref="Rules.Validate"/>).</summary>
public sealed class InvalidPositionException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public InvalidPositionException()
        : base("Invalid position.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public InvalidPositionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public InvalidPositionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A property the code relies on did not hold, for example a game longer than <see cref="Rules.MaxPlies"/>
/// (spec 3.7). It can only mean a rules bug, so it is never caught.
/// </summary>
public sealed class InvariantViolationException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public InvariantViolationException()
        : base("Invariant violated.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public InvariantViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public InvariantViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
