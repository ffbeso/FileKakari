# アーキテクチャ

## 概要

FileKakariはC#、WPF、.NET 10で実装したWindowsデスクトップアプリです。常駐サービス、独自インデックスDB、Windows Searchを前提にしません。

## 主な構成

| 領域 | 主な責務 |
| --- | --- |
| MainWindowとpartial class | WPFイベント、ペイン表示、選択、Dispatcher更新 |
| Controller | ナビゲーション、入力、ペイン表示などのUI調整 |
| FileService / IFileEnumerator | フォルダ列挙 |
| FileOperationService | コピー、移動、作成、リネーム、削除 |
| UndoService | アプリ内の直前操作の取り消し |
| WorkspaceService | Workspace JSONの読み書きとレイアウト構築 |
| SessionStateService | タブ、Workspace、表示状態の保存 |
| UserCommandExecutionService | User Commandの展開と外部プロセス起動 |
| FolderWatchService | 表示中フォルダの外部変更検知 |

## ファイル一覧

ファイル一覧はWPF ListViewを使用します。仮想化とrecyclingを有効にし、列挙結果は小さなバッチでUIへ追加します。

フォルダ列挙はUIスレッド外で実行します。新しい読み込みが始まった場合は世代IDとCancellationTokenで古い結果を無効化します。

## タブとWorkspace

通常フォルダとWorkspaceはWorkspaceSessionで管理します。WorkspaceはFolderPane、FolderTab、分割レイアウトを持ちます。各タブはパス、表示形式、ソート、選択、スクロール位置を保持します。

共有構成はWorkspace JSONへ保存します。実行時の選択やスクロール位置はセッション状態へ保存します。JSON仕様は[Workspace JSON](workspace-json.md)を参照してください。

## ファイル操作

ファイル操作はサービス層で実行し、MainWindowは対象ペイン、確認表示、更新後の選択復元を担当します。削除は確認後にWindowsのゴミ箱へ送ります。

コピーと移動の後は、移動元と移動先を再読み込みします。移動先では実行結果のパスを選択し、一覧内へ表示します。

## 外部変更

FolderWatchServiceが変更を短時間まとめ、FileWatcherRefreshCoordinatorがpending更新と自己操作後の抑制を管理します。実際の再読み込みと選択・スクロール復元はMainWindowで行います。

## 設定

主な保存先は次のとおりです。

| 内容 | 保存先 |
| --- | --- |
| アプリ設定 | %APPDATA%\FileKakari |
| セッション、User Command | %LOCALAPPDATA%\FileKakari |
| Workspace共有構成 | ユーザーが保存した .workspace.json または *.workspace.json |

## 開発上の原則

- UIスレッドで重いI/Oを行わない
- ファイル操作失敗時に元の表示状態を失わない
- ListViewの仮想化を維持する
- WPF依存処理はView層に閉じ込める
- 追加依存は明確な効果がある場合に限る
