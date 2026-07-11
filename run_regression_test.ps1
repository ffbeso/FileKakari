# FileKakari Monaco Dedicated Thread & Regression Auto Test Script

$ErrorActionPreference = "Stop"

# 1. 関連プロセスのクリーン
Get-Process -Name *Monaco*, *prevhost*, FileKakari -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 2. テスト用ディレクトリとファイルを作成
$testDir = "d:\Works\VScode\FileKakari-public\TestFiles"
if (Test-Path $testDir) { Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $testDir | Out-Null

# ダミーファイル作成
[System.IO.File]::WriteAllText("$testDir\01_vpn_on.bat", "@echo off`r`necho VPN接続中...`r`npause", [System.Text.Encoding]::GetEncoding(932))
[System.IO.File]::WriteAllText("$testDir\01b_ascii_on.bat", "@echo off`r`necho VPN CONNECTING...`r`npause", [System.Text.Encoding]::ASCII)
[System.IO.File]::WriteAllText("$testDir\02_test.md", "# Test Markdown`nThis is a test.")
[System.IO.File]::WriteAllText("$testDir\03_test.txt", "Plain text content.")
[System.IO.File]::WriteAllBytes("$testDir\04_test.pdf", @(0..10)) # ダミーPDF
[System.IO.File]::WriteAllBytes("$testDir\05_test.mp4", @(0..10)) # ダミーMP4

Write-Output "Test files prepared."

# 3. session.json をバックアップして、テスト用セッションを作成
$sessionDir = "C:\Users\besok\AppData\Local\FileKakari"
$sessionPath = "$sessionDir\session.json"
$backupPath = "$sessionDir\session.json.bak"

if (Test-Path $sessionPath) {
    Copy-Item $sessionPath $backupPath -Force
    Write-Output "Original session.json backed up."
}

# テスト用 session.json の内容
$testSessionJson = @"
{
  "Version": 1,
  "SelectedTabIndex": 0,
  "Tabs": [
    {
      "TabId": "test_tab_01",
      "Path": "$($testDir.Replace('\', '\\'))",
      "IsWorkspace": false,
      "WorkspacePath": "",
      "RootPath": "",
      "SortColumn": "Name",
      "SortAscending": true,
      "ViewMode": 1,
      "IsFolderLocked": false,
      "LocalState": null,
      "IsUnsavedWorkspace": false,
      "WorkspaceId": null,
      "Name": null,
      "ActivePaneId": "primary",
      "Layout": {
        "NodeType": 0,
        "PaneId": "primary",
        "LeftChild": null,
        "RightChild": null,
        "Orientation": 0,
        "SplitId": null,
        "SplitRatio": 0.5
      }
    }
  ]
}
"@

[System.IO.File]::WriteAllText($sessionPath, $testSessionJson, [System.Text.Encoding]::UTF8)
Write-Output "Temporary session.json written."

# 4. perf.log の削除
$logPath = "d:\Works\VScode\FileKakari-public\perf.log"
if (Test-Path $logPath) { Remove-Item $logPath -Force }

# 5. FileKakari の起動 (自動テスト環境変数を有効化)
$exePath = "d:\Works\VScode\FileKakari-public\FileKakari\bin\Debug\net10.0-windows\FileKakari.exe"
$env:FILEKAKARI_PERF_LOG = $logPath
$env:FILEKAKARI_AUTO_TEST = "1"

Write-Output "Starting FileKakari in automated test mode..."
$fkProc = Start-Process -FilePath $exePath -PassThru

# 一時的な環境変数のクリア
$env:FILEKAKARI_PERF_LOG = $null
$env:FILEKAKARI_AUTO_TEST = $null

# 6. アプリの自動終了を待つ (タイムアウト 25 秒)
Write-Output "Waiting for automated test scenario to complete..."
$exited = $fkProc.WaitForExit(25000)

if (-not $exited) {
    Write-Output "Warning: Test timed out. Force-killing FileKakari..."
    $fkProc.Kill()
} else {
    Write-Output "FileKakari exited gracefully."
}

# session.json をリストア
if (Test-Path $backupPath) {
    Copy-Item $backupPath $sessionPath -Force
    Remove-Item $backupPath -Force
    Write-Output "Original session.json restored."
}

# 7. perf.log 成果の抽出・評価
Start-Sleep -Milliseconds 500
if (Test-Path $logPath) {
    $lines = Get-Content $logPath
    Write-Output "`n=== REGRESSION TEST LOG RESULTS ==="
    
    Write-Output "`n--- 1. Preview Routing Decisions (Monaco vs BuiltInText) ---"
    $lines | Where-Object { $_ -like "*PreviewRouting*" }

    Write-Output "`n--- 2. Dedicated Monaco Thread Actions ---"
    $lines | Where-Object { $_ -like "*MonacoPreviewThreadHost*" -or $_ -like "*BuildWindowCore (DedicatedThread)*" }
    
    Write-Output "`n--- 3. PDF Preview Actions ---"
    $lines | Where-Object { $_ -like "*Pdf*" }
    
    Write-Output "`n--- 4. MP4 Preview Actions ---"
    $lines | Where-Object { $_ -like "*Mp4*" -or $_ -like "*Video*" -or $_ -like "*Media*" }

    Write-Output "`n--- 5. General Fallbacks or Errors ---"
    $lines | Where-Object { $_ -like "*fallback*" -or $_ -like "*exception*" -or $_ -like "*fail*" }

    Write-Output "`n===================================="
} else {
    Write-Output "Error: perf.log was not generated."
}
