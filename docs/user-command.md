# User Command

User Commandは、ファイル一覧の右クリックメニューから外部プログラムやスクリプトを実行する機能です。

## 設定ファイル

設定は次のJSONファイルへ保存します。

    %LOCALAPPDATA%\FileKakari\commands.json

ルートはコマンドオブジェクトの配列です。

    [
      {
        "name": "Open in Notepad",
        "executable": "notepad.exe",
        "arguments": "\"{selectedFile}\"",
        "workingDirectory": "{currentDirectory}",
        "useShellExecute": true,
        "target": "Selection",
        "enabled": true,
        "extensions": [".txt", ".md"],
        "allowFiles": true,
        "allowDirectories": false,
        "allowMultiple": false
      }
    ]

versionやcommandsを持つルートオブジェクト形式には対応していません。

## プロパティ

| 名前 | 内容 | 既定値 |
| --- | --- | --- |
| name | メニュー表示名 | 空文字 |
| executable | 実行ファイルまたはスクリプト | 空文字 |
| arguments | コマンドライン引数 | 空文字 |
| workingDirectory | 作業フォルダ | 現在のフォルダ |
| useShellExecute | ProcessStartInfo.UseShellExecute | false |
| target | Any、Selection、CurrentDirectory | Any |
| enabled | メニューへ表示するか | true |
| extensions | 対象ファイルの拡張子 | 制限なし |
| allowFiles | ファイル選択を許可するか | true |
| allowDirectories | フォルダ選択を許可するか | true |
| allowMultiple | 複数選択を許可するか | true |

Selectionは選択項目がない場合に無効になります。

## 表示条件

次のすべてを満たすコマンドを表示します。

1. enabledがfalseではない
2. 複数選択時にallowMultipleがtrue
3. ファイルを含む場合にallowFilesがtrue
4. フォルダを含む場合にallowDirectoriesがtrue
5. extensionsを指定した場合、選択した全ファイルの拡張子が一致する

extensionsが未指定、空配列、または "*" を含む場合は制限しません。比較時は大文字小文字を区別しません。フォルダは拡張子判定から除外します。

拡張子は読み込み時と保存時に小文字の ".ext" 形式へ正規化します。

    cs    -> .cs
    *.CS  -> .cs
    *     -> *

## プレースホルダ

executable、arguments、workingDirectoryで使用できます。名前の大文字小文字は区別しません。

| 名前 | 展開内容 |
| --- | --- |
| {selectedFile} | 先頭選択項目のフルパス |
| {selectedFiles} | 全選択項目を引用符で囲んだ一覧 |
| {currentDirectory} | 現在表示中のフォルダ |
| {selectedFileName} | 先頭選択項目の名前 |
| {selectedFileBaseName} | 先頭選択項目の拡張子を除いた名前 |
| {selectedFileExtension} | 先頭選択項目の拡張子 |
| {selectedDirectory} | 選択フォルダ、または選択ファイルの親フォルダ |
| {currentDirectoryName} | 現在表示中のフォルダ名 |
| {selectedDirectoryName} | 選択フォルダ名、または選択ファイルの親フォルダ名 |
| {currentParentDirectory} | 現在表示中フォルダの親パス |
| {currentParentDirectoryName} | 現在表示中フォルダの親フォルダ名 |
| {commandDir} | User Command用スクリプトフォルダ |
| {timestamp} | yyyyMMdd_HHmmss形式の現在時刻 |

次の短縮名も使用できます。

| 短縮名 | 展開内容 |
| --- | --- |
| {path} | {selectedFile} |
| {current} | {currentDirectory} |
| {selected} | {selectedFiles} |
| {selectedPathsQuoted} | {selectedFiles} |
| {dir} | 先頭選択項目の親フォルダ |
| {name} | 先頭選択項目の名前 |
| {nameWithoutExtension} | 先頭選択項目の拡張子を除いた名前 |
| {parent} | {currentParentDirectory} |

パスを1件だけ渡す場合、必要な引用符はarguments側で指定します。selectedFilesは各パスを引用符で囲んで展開します。

## 例

現在のフォルダでPowerShellを開く例です。

    {
      "name": "PowerShell Here",
      "executable": "powershell.exe",
      "arguments": "-NoExit -Command Set-Location -LiteralPath \"{currentDirectory}\"",
      "workingDirectory": "{currentDirectory}",
      "useShellExecute": true,
      "target": "CurrentDirectory",
      "enabled": true,
      "extensions": [],
      "allowFiles": true,
      "allowDirectories": true,
      "allowMultiple": true
    }

コマンド終了後、実行元ペインのファイル一覧を再読み込みします。
