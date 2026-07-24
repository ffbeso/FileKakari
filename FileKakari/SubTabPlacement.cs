namespace FileKakari;

public enum SubTabPlacement
{
    Top,
    Left,
    Right,
    Bottom,

    // 旧データ互換用 (読み込み時に Top / Left に正規化)
    Horizontal = Top,
    Vertical = Left
}
