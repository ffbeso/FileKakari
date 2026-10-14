namespace FileKakari;

/// <summary>Per-load feedback for UI callbacks; rendering and dispatcher wait are not measured here.</summary>
public sealed class AdaptiveBatchSizer
{
    public const int MinimumSize = 64;
    public const int MaximumSize = 2048;
    public const double TargetMilliseconds = 8;

    public int NextSize { get; private set; } = MinimumSize;

    public void Observe(int appliedCount, double uiMilliseconds)
    {
        if (appliedCount <= 0 || !double.IsFinite(uiMilliseconds) || uiMilliseconds < 0) return;

        // Hysteresis avoids oscillation around the budget. Grow gradually so that
        // a cheap initial batch does not immediately become a large UI operation.
        var size = Math.Clamp(appliedCount, MinimumSize, MaximumSize);
        NextSize = uiMilliseconds > TargetMilliseconds
            ? Math.Max(MinimumSize, size / 2)
            : uiMilliseconds < TargetMilliseconds / 2
                ? Math.Min(MaximumSize, size * 2)
                : size;
    }
}
