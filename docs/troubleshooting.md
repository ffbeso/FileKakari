# トラブルシューティング

## 起動またはビルドができない

.NET 10 SDKが利用できることを確認してください。

    dotnet --info
    dotnet build FileKakari/FileKakari.csproj

## フォルダを開けない

- パスが存在するか確認する
- 対象フォルダへのアクセス権を確認する
- ネットワークドライブや取り外し済みドライブの状態を確認する

FileKakariはShell名前空間や仮想フォルダを通常フォルダとして扱いません。

## ショートカットが反応しない

外部ランチャーやウィンドウ管理ツールがキーを先に取得していないか確認してください。Alt+Left、Alt+Rightなどは外部ツールと競合する場合があります。

IMEやテキスト入力へフォーカスがある場合、標準の文字入力と編集操作が優先されます。

## User Commandが表示されない

- commands.jsonが有効なJSON配列か確認する
- enabled、extensions、allowFiles、allowDirectories、allowMultipleを確認する
- 実行ファイルのパスと引用符を確認する

詳細は[User Command](user-command.md)を参照してください。

## Workspaceが読み込まれない

- JSONファイルと参照先フォルダが存在するか確認する
- JSONにコメントや末尾カンマがないか確認する
- type、viewMode、orientationの値を確認する

詳細は[Workspace JSON](workspace-json.md)を参照してください。

## 性能ログ

FILEKAKARI_PERF_LOGへ出力先を設定できます。未設定時にログビューアーが探索する標準名はfilekakari-perf.logです。

    $env:FILEKAKARI_PERF_LOG = "C:\Temp\filekakari-perf.log"
    dotnet run --project FileKakari/FileKakari.csproj
