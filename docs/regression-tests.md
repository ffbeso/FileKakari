# 回帰テスト

Windows、.NET 10 SDK、PowerShell 7以降で実行します。

```powershell
pwsh -NoProfile -File ./run_regression_test.ps1
```

本体を参照する専用コンソールをビルドし、実際のサービスを検証します。
UTF-8／CP932／ASCII／Markdownのテキスト内容、サイズ制限、ファイル消失、キャンセル、
PNGのデコード、フォルダ優先の昇降順列挙とメタデータ、セッションの往復保存、不正JSONが対象です。
新しいテストは `FileKakari.RegressionTests/Program.cs` の `CheckAsync` 呼び出しとして追加します。

追加の回帰項目として、Undo途中失敗後の再試行、監視ルートの正規化、ジャンクション循環の拒否、
列挙キャンセル、アイコンの遅延更新通知、重複する復元処理の抑制、保存失敗時の元ファイル保持、
保存要求の順序、大文字・小文字だけのファイル／フォルダのリネームとUndoを確認します。
ジャンクションのテストではWindows標準のPowerShellで検証用リンクを作成してから解除します。
リンク作成が禁止される環境ではテストを失敗として報告します。

今回の修正の実操作確認では、次を確認してください。

* 大量のEXEやショートカットを含むフォルダで、一覧が先に表示され、スクロール後もアイコンが更新される。
* 読み込み速度の異なる複数ペインでWorkspaceを切り替え、選択とスクロール位置が保持される。
* Workspaceを保存してすぐ閉じ、再起動後に保存先・タブ・ペイン構成が復元される。
* ファイルとフォルダをF2で大文字・小文字だけ変更し、Ctrl+Zで元の表記へ戻せる。

成功時は終了コード0、テスト失敗・ビルド失敗・タイムアウト・結果欠落時は1です。
実行ごとに `temp/regression/<一意なID>/` を作り、`results.json`、標準出力／エラーログ、
性能ログ、ビルドログ、検証用ファイルを残します。失敗原因は各チェックの `Error` を確認してください。
起動前の失敗などで結果JSONを生成できない場合は `runner-error.log` を確認します。
成果物は自動削除しないため、不要になった実行フォルダは手動で削除できます。

```powershell
# ビルド済みの同じ構成を繰り返し検証（ソース変更後は通常実行で再ビルド）
pwsh -NoProfile -File ./run_regression_test.ps1 -NoBuild
# 保存先とテスト実行の制限時間を指定（ビルド時間は制限時間に含まない）
pwsh -NoProfile -File ./run_regression_test.ps1 -Configuration Release -TimeoutSeconds 120 -OutputDirectory 'D:\Test Results'
```

通常のFileKakariウィンドウは起動せず、ユーザーの設定・セッションを読み書きしません。
親プロセスの環境変数も変更しません。タイムアウト時に終了するのは、このスクリプトが起動した
テストプロセスとその子だけです。既に起動しているFileKakari、Explorer、Preview Handlerは終了しません。

このテストはサービス層の回帰テストです。Shell Preview Handler／PowerToys／WebView2の表示、
PDF・動画・音声の再生、WPF上の選択・スクロール・Workspace復元は別途実操作で確認してください。
旧スクリプトの `FILEKAKARI_AUTO_TEST` は本体に実装がなく、破損したPDF／メディアを置いても
表示の成否を検証できなかったため、合格判定には使用しません。
