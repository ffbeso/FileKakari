namespace FileKakari;

public static class PreviewPaneSizing
{
    public static double Clamp(double desired, double available, double previewMinimum, double listMinimum, double splitter)
    {
        if (!double.IsFinite(desired)) desired = previewMinimum;
        if (!double.IsFinite(available) || available <= 0) return Math.Max(previewMinimum, desired);
        var maximum = Math.Max(0, available - splitter - listMinimum);
        return Math.Min(maximum, Math.Max(previewMinimum, desired));
    }
}
