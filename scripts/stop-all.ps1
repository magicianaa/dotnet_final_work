$ErrorActionPreference = "Continue"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$pidDir = Join-Path $root ".run"

if (-not (Test-Path $pidDir)) {
    Write-Host "No .run directory found. No processes recorded by start-all.ps1." -ForegroundColor Yellow
    return
}

Get-ChildItem $pidDir -Filter "*.pid" | ForEach-Object {
    $name = $_.BaseName
    $pidValue = Get-Content $_.FullName -ErrorAction SilentlyContinue
    if (-not $pidValue) { return }

    $childProcesses = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ParentProcessId -eq [int]$pidValue } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }

    foreach ($child in $childProcesses) {
        Write-Host "[$name] stopping child PID=$($child.Id) ($($child.ProcessName))" -ForegroundColor Cyan
        Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue
    }

    $process = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
    if ($process) {
        Write-Host "[$name] stopping PID=$pidValue" -ForegroundColor Cyan
        Stop-Process -Id $pidValue -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Host "[$name] PID=$pidValue no longer exists" -ForegroundColor Yellow
    }

    Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
}

Get-Process SmartStudy.Web -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "[web] stopping orphan PID=$($_.Id)" -ForegroundColor Cyan
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}

Write-Host "SmartStudy process records cleaned." -ForegroundColor Green
