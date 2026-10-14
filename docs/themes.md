# 明暗で対になるテーマ

設定のテーマ選択から切り替える。標準ブルーは既存の「ライト」「ダーク」、それ以外は名前にLight／Darkが付いたテーマを選ぶ。
初回起動時に配布用Themesフォルダからユーザーのテーマフォルダへ追加される。編集済みのユーザーファイルは上書きしない。未編集の同梱テーマの更新条件は後述。

| 色の組 | ライト | ダーク |
| --- | --- | --- |
| 標準ブルー | LightTheme.xaml | DarkTheme.xaml |
| グラファイト | GraphiteLightTheme.xaml | GraphiteDarkTheme.xaml |
| カプチーノ | CappuccinoLightTheme.xaml | CappuccinoDarkTheme.xaml |
| フォレスト | ForestLightTheme.xaml | ForestDarkTheme.xaml |
| オーシャン | OceanLightTheme.xaml | OceanDarkTheme.xaml |
| サクラ | SakuraLightTheme.xaml | SakuraDarkTheme.xaml |
| ラベンダー | LavenderLightTheme.xaml | LavenderDarkTheme.xaml |
| アンバー | AmberLightTheme.xaml | AmberDarkTheme.xaml |
| ローズ | RoseLightTheme.xaml | RoseDarkTheme.xaml |
| サイバーパンク | CyberpunkLightTheme.xaml | CyberpunkDarkTheme.xaml |
| マトリックス（端末の緑） | MatrixLightTheme.xaml | MatrixDarkTheme.xaml |
| シンセウェーブ（紫・ピンク・橙） | SynthwaveLightTheme.xaml | SynthwaveDarkTheme.xaml |
| ディープスペース（紺・氷青） | DeepSpaceLightTheme.xaml | DeepSpaceDarkTheme.xaml |
| ホログラム（青緑・紫） | HologramLightTheme.xaml | HologramDarkTheme.xaml |
| スチームパンク（真鍮・青緑） | SteampunkLightTheme.xaml | SteampunkDarkTheme.xaml |

旧名のGray／Cappuccino／Forest／Cyberpunkは同梱と選択一覧から除外した。古い設定は対応するLight／Darkへ解決する。ユーザーフォルダにある旧ファイルは削除しない。独自編集した旧テーマを再び一覧に出す場合は別名にコピーする。
サイバーパンクは紫の背景・シアンのアクセント・ライム色のアイコンへ変更した。前回の同梱内容とハッシュが一致するユーザーコピーだけを更新し、編集済みのコピーは維持する。

## 配色の方針

各組のライト／ダークは同じアクセント色相を使う。明るい背景では濃いアクセント、暗い背景では明るいアクセントにする。
背景は低彩度、通常文字と補助文字は高いコントラストを持たせる。選択中の行は色付き、非アクティブ選択は低彩度で区別する。
表示中のサブタブの目印は、アクティブ側がアクセント色、非アクティブ側が補助文字色となる。
スクロールバー、ヘッダー、ホバー、押下状態、区切り線も各組の色味に合わせる。

[配色見本](theme-gallery.html)は実際の色定義から生成した比較用HTMLであり、WPF画面のスクリーンショットではない。

## 編集と確認

同梱テーマの色は `tools/generate_themes.py` を編集し、`python tools/generate_themes.py` で再生成する。生成対象はこの表の30ファイルと配色見本のみ。
テーマは独立したXAMLファイルなので、ユーザー側では個別に編集できる。追加ライブラリは不要。

回帰テストでは30ファイルのWPF読込、リソースキーの一致、主要背景に対する通常文字・補助文字・選択文字のコントラスト比4.5以上、アクティブペインの目印の比3以上を確認する。これは全UI状態のアクセシビリティ適合を保証するものではない。

手動確認では設定からテーマを切り替え、詳細表示／アイコン表示、選択／非アクティブ選択、サブタブの横／縦配置、メニュー、スクロールバーを確認する。テーマ切替後の再起動で選択したテーマが維持されることも確認する。
