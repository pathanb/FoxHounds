namespace FoxHounds.Core;

/// <summary>
/// One ply (spec 5.3). The side that moves is implied by the state; for hounds, <see cref="From"/> names the
/// hound that moves. Text form e1-d2 via <see cref="Geometry.FormatMove"/>.
/// </summary>
/// <param name="From">Origin square index.</param>
/// <param name="To">Destination square index.</param>
public readonly record struct Move(byte From, byte To)
{
    /// <summary>"No move": (255, 255).</summary>
    public static readonly Move None = new(255, 255);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => From == 255 && To == 255;
}
