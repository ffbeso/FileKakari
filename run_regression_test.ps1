#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 60,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'temp/regression'),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$process = $null
$exitCode = 1
$runDirectory = $null
$stdout = $null
$stderr = $null
try {
    $runId = [guid]::NewGuid().ToString('N')
    $runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $runId
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    Write-Host "Regression artifacts: $runDirectory"
    $project = Join-Path $PSScriptRoot 'FileKakari.RegressionTests/FileKakari.RegressionTests.csproj'
    if (-not $NoBuild) {
        & dotnet build $project --configuration $Configuration -m:1 *> (Join-Path $runDirectory 'build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Build failed. See build.log.' }
    }
    $assembly = Join-Path $PSScriptRoot "FileKakari.RegressionTests/bin/$Configuration/net10.0-windows/FileKakari.RegressionTests.dll"
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Test assembly missing: $assembly" }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.WorkingDirectory = $runDirectory
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add($assembly)
    $startInfo.ArgumentList.Add($runDirectory)
    $startInfo.ArgumentList.Add($runId)
    # Change only the child environment; inherited diagnostics must not alter the run.
    foreach ($key in @($startInfo.Environment.Keys)) {
        if ($key.StartsWith('FILEKAKARI_', [StringComparison]::OrdinalIgnoreCase)) {
            $null = $startInfo.Environment.Remove($key)
        }
    }
    $startInfo.Environment['FILEKAKARI_PERF_LOG'] = Join-Path $runDirectory 'perf.log'
    $process = [Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        throw "Regression tests timed out after $TimeoutSeconds seconds."
    }
    if ($process.ExitCode -ne 0) { throw "Regression tests failed (exit $($process.ExitCode)). See results.json and stderr.log." }
    $result = Get-Content -LiteralPath (Join-Path $runDirectory 'results.json') -Raw | ConvertFrom-Json
    if ($result.SchemaVersion -ne 1 -or $result.RunId -ne $runId -or $result.Passed -ne $true -or
        $result.Tests.Count -eq 0 -or @($result.Tests | Where-Object { $_.Passed -ne $true }).Count -gt 0) {
        throw 'Missing, incomplete, or unsuccessful regression result.'
    }
    Write-Host "PASS: $($result.Tests.Count) service regression checks."
    $exitCode = 0
}
catch {
    Write-Host "FAIL: $($_.Exception.Message)" -ForegroundColor Red
    if ($runDirectory) { $_.Exception.ToString() | Set-Content -LiteralPath (Join-Path $runDirectory 'runner-error.log') }
}
finally {
    if ($process) {
        try {
            # Own only this process tree. Never search for processes by name.
            if (-not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) { throw 'Test process did not exit after termination.' }
            }
            if ($stdout) { $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runDirectory 'stdout.log') }
            if ($stderr) { $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runDirectory 'stderr.log') }
        }
        catch {
            Write-Host "Cleanup failed: $($_.Exception.Message)" -ForegroundColor Red
            $exitCode = 1
        }
        finally { $process.Dispose() }
    }
}
exit $exitCode
