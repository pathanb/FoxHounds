using System.Diagnostics;

namespace FoxHounds.Core;

/// <summary>
/// The only way code may read time (spec 4.3: no hidden time). Search time limits, game timestamps and progress
/// reports take an IClock; tests pass a <see cref="ManualClock"/>.
/// </summary>
public interface IClock
{
    /// <summary>Wall-clock time, for timestamps only.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic time since an arbitrary origin; only differences are meaningful.</summary>
    TimeSpan Elapsed { get; }
}

/// <summary>The real clock. The one place allowed to use the banned time APIs (decision D-29).</summary>
public sealed class SystemClock : IClock
{
#pragma warning disable RS0030 // Banned API: this class is the sanctioned wrapper around real time (D-29).
    private static readonly long Origin = Stopwatch.GetTimestamp();

    private SystemClock()
    {
    }

    /// <summary>The shared instance.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(Origin);
#pragma warning restore RS0030
}

/// <summary>A clock that moves only when told to. Thread-safe.</summary>
public sealed class ManualClock : IClock
{
    private readonly Lock _lock = new();
    private DateTimeOffset _utcNow;
    private TimeSpan _elapsed;

    /// <summary>Creates a clock showing <paramref name="start"/>.</summary>
    public ManualClock(DateTimeOffset start)
    {
        _utcNow = start;
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
            {
                return _elapsed;
            }
        }
    }

    /// <summary>Moves both readings forward.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="by"/> is negative.</exception>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        lock (_lock)
        {
            _elapsed += by;
            _utcNow += by;
        }
    }
}
