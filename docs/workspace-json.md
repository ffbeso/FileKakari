# Workspace JSON

Workspaceは、複数のサブタブとペイン構成をまとめて保存・読み込みする機能です。

## ファイル

フォルダ直下の自動検出名は次のとおりです。

    .workspace.json
    .kakari-workspace.json

任意の名前で保存した *.workspace.json を直接開くこともできます。

自動検出名を使う場合、そのファイルのあるフォルダがWorkspaceのrootになります。任意名のファイルではrootPathを指定できます。

選択、スクロール位置、フィルターなどの実行時状態は、アプリのセッション状態へ保存します。

## 最小例

    {
      "name": "My Workspace",
      "layout": {
        "type": "paneGroup",
        "id": "primary",
        "tabs": [
          {
            "id": "root",
            "basePath": ".",
            "viewMode": "Details",
            "sortColumn": "Name",
            "sortAscending": true
          }
        ]
      }
    }

## 複数ペイン

splitはfirstとsecondの2ノードを持ちます。ノードにはpaneGroupまたは別のsplitを指定できます。

    {
      "name": "Review",
      "layout": {
        "type": "split",
        "id": "root-split",
        "orientation": "Horizontal",
        "ratio": 0.5,
        "first": {
          "type": "paneGroup",
          "id": "left",
          "selectedTabId": "input",
          "tabs": [
            {
              "id": "input",
              "basePath": "./Input",
              "viewMode": "Details"
            }
          ]
        },
        "second": {
          "type": "paneGroup",
          "id": "right",
          "selectedTabId": "output",
          "tabs": [
            {
              "id": "output",
              "basePath": "./Output",
              "viewMode": "List"
            }
          ]
        }
      }
    }

相対パスはWorkspace rootを基準に解決します。存在しないパスは読み込み対象になりません。

## Root

| 名前 | 内容 |
| --- | --- |
| workspaceId | Workspaceの識別子 |
| name | 表示名 |
| rootPath | 任意名ファイルで使用するrootパス |
| viewMode | fallback表示形式 |
| selectedTabIndex | rootのtabs形式で使う選択位置 |
| tabs | 単一ペイン向けの簡易タブ配列 |
| layout | paneGroupまたはsplit |

layoutがある場合はlayoutを優先します。

## Layout

共通フィールド:

| 名前 | 内容 |
| --- | --- |
| type | paneGroupまたはsplit |
| id | ノード識別子 |

paneGroup:

| 名前 | 内容 |
| --- | --- |
| selectedTabId | 選択中タブのid |
| selectedTabIndex | selectedTabIdがない場合の選択位置 |
| tabs | サブタブ配列 |

split:

| 名前 | 内容 |
| --- | --- |
| orientation | HorizontalまたはVertical |
| ratio | 0.1～0.9の分割比 |
| first / second | 子ノード |
| children | first / secondの代わりに使える子ノード配列 |

## Tab

| 名前 | 内容 |
| --- | --- |
| id | タブ識別子 |
| basePath | タブの基準フォルダ |
| path | basePathの別名として読み込み可能 |
| viewMode | Details、Compact、List |
| sortColumn | Name、Type、Size、Modifiedなど |
| sortAscending | 昇順ならtrue |
| isFolderLocked | フォルダ移動をロックするか |
| fixed | isFolderLockedの別名として読み込み可能 |

プロパティ名の大文字小文字は区別しません。JSONにはコメントや末尾カンマを記述できません。

## 保存

Workspaceの保存操作は、現在のペイン、サブタブ、表示形式、ソート、ロック状態をJSONへ書き出します。自動検出名へ保存した場合はrootPathを省略し、パスをrootからの相対パスへ変換します。

個人の選択やスクロール位置は共有JSONへ書かず、セッション状態として保持します。
