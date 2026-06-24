param(
    [int]$WebPort = 5178,
    [switch]$NoBuild,
    [switch]$SkipIndex,
    [switch]$NoCli,
    [switch]$WithMcp,
    [string]$EmbeddingProvider = "zhipu"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

$env:SMARTSTUDY_Agent__Embedding__Provider = $EmbeddingProvider

$pidDir = Join-Path $root ".run"
New-Item -ItemType Directory -Force -Path $pidDir | Out-Null

function Stop-ProcessTree {
    param([int]$RootPid)

    $children = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ParentProcessId -eq $RootPid }

    foreach ($child in $children) {
        Stop-ProcessTree -RootPid ([int]$child.ProcessId)
    }

    Stop-Process -Id $RootPid -Force -ErrorAction SilentlyContinue
}

function Start-SmartStudyProcess {
    param(
        [string]$Name,
        [string]$Command
    )

    $logPath = Join-Path $pidDir "$Name.log"
    $pidPath = Join-Path $pidDir "$Name.pid"

    if (Test-Path $pidPath) {
        $oldPid = Get-Content $pidPath -ErrorAction SilentlyContinue
        if ($oldPid -and (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) {
            Write-Host "[$Name] stopping stale PID=$oldPid before restart" -ForegroundColor Yellow
            Stop-ProcessTree -RootPid ([int]$oldPid)
            Remove-Item $pidPath -Force -ErrorAction SilentlyContinue
        }
    }

    $process = Start-Process powershell `
        -ArgumentList @("-NoExit", "-Command", $Command) `
        -WorkingDirectory $root `
        -PassThru

    Set-Content -Encoding UTF8 -Path $pidPath -Value $process.Id
    Set-Content -Encoding UTF8 -Path $logPath -Value "Started $Name at $(Get-Date -Format s), PID=$($process.Id)"
    Write-Host "[$Name] started, PID=$($process.Id)" -ForegroundColor Green
}

if (-not $NoBuild) {
    Write-Host "[build] dotnet build SmartStudy.sln --no-restore" -ForegroundColor Cyan
    dotnet build SmartStudy.sln --no-restore
}

if (-not $SkipIndex) {
    Write-Host "[index] build or refresh knowledge index, Embedding=$EmbeddingProvider" -ForegroundColor Cyan
    dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj --no-build -- index
}

$webCommand = "`$env:SMARTSTUDY_Agent__Embedding__Provider='$EmbeddingProvider'; dotnet run --no-build --project src\SmartStudy.Web\SmartStudy.Web.csproj --urls http://localhost:$WebPort"
Start-SmartStudyProcess -Name "web" -Command $webCommand

if (-not $NoCli) {
    $cliCommand = "`$env:SMARTSTUDY_Agent__Embedding__Provider='$EmbeddingProvider'; dotnet run --no-build --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- chat --stream"
    Start-SmartStudyProcess -Name "cli" -Command $cliCommand
}

if ($WithMcp) {
    $mcpCommand = "`$env:SMARTSTUDY_Agent__Embedding__Provider='$EmbeddingProvider'; dotnet run --no-build --project src\SmartStudy.Mcp\SmartStudy.Mcp.csproj"
    Start-SmartStudyProcess -Name "mcp" -Command $mcpCommand
    Write-Host "[mcp] Note: MCP is a stdio service. It is usually launched by an MCP Host and may exit without input." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "SmartStudy started." -ForegroundColor Green
Write-Host "Web: http://localhost:$WebPort" -ForegroundColor Cyan
if (-not $NoCli) { Write-Host "CLI: separate PowerShell window opened." -ForegroundColor Cyan }
Write-Host "Stop all: .\scripts\stop-all.ps1" -ForegroundColor Cyan
