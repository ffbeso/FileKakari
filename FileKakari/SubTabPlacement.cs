namespace FileKakari;

public enum SubTabPlacement
{
    Top,
    Left,
    Right,
    Bottom,
    Horizontal = Top,
    Vertical = Left
}

public static class SubTabPlacementHelper
{
    public static SubTabPlacement Resolve(SubTabPlacement? raw, SubTabPlacement fallback)
    {
        if (!raw.HasValue) return fallback;
        return raw.Value switch
        {
            SubTabPlacement.Left or SubTabPlacement.Vertical => SubTabPlacement.Left,
            SubTabPlacement.Right => SubTabPlacement.Right,
            SubTabPlacement.Bottom => SubTabPlacement.Bottom,
            SubTabPlacement.Top or SubTabPlacement.Horizontal => SubTabPlacement.Top,
            _ => fallback
        };
    }
}
