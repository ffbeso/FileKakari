# 明暗で対になるテーマ

設定のテーマ選択から切り替える。標準ブルーは既存の「ライト」「ダーク」、それ以外は名前にLight／Darkが付いたテーマを選ぶ。
初回起動時に配布用Themesフォルダからユーザーのテーマフォルダへ追加される。既存のユーザーファイルは上書きしない。

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

旧名のGray／Cappuccino／Forest／Cyberpunkは互換用に同梱する。新規配布ファイルの配色は対応する新テーマと同じだが、ユーザーフォルダに既にある旧名のファイルは維持される。新しい配色を使う場合は明暗付きの名前を選ぶ。

## 配色の方針

各組のライト／ダークは同じアクセント色相を使う。明るい背景では濃いアクセント、暗い背景では明るいアクセントにする。
背景は低彩度、通常文字と補助文字は高いコントラストを持たせる。選択中の行は色付き、非アクティブ選択は低彩度で区別する。
表示中のサブタブの目印は、アクティブ側がアクセント色、非アクティブ側が補助文字色となる。
スクロールバー、ヘッダー、ホバー、押下状態、区切り線も各組の色味に合わせる。

[配色見本](theme-gallery.html)は実際の色定義から生成した比較用HTMLであり、WPF画面のスクリーンショットではない。

## 編集と確認

同梱テーマの色は `tools/generate_themes.py` を編集し、`python tools/generate_themes.py` で再生成する。生成対象はこの表の20ファイル、旧名4ファイル、配色見本のみ。
テーマは独立したXAMLファイルなので、ユーザー側では個別に編集できる。追加ライブラリは不要。

回帰テストでは20ファイルのWPF読込、リソースキーの一致、主要背景に対する通常文字・補助文字・選択文字のコントラスト比4.5以上、アクティブペインの目印の比3以上を確認する。これは全UI状態のアクセシビリティ適合を保証するものではない。

手動確認では設定からテーマを切り替え、詳細表示／アイコン表示、選択／非アクティブ選択、サブタブの横／縦配置、メニュー、スクロールバーを確認する。テーマ切替後の再起動で選択したテーマが維持されることも確認する。
