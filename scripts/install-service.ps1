param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$ServiceName = "BonapPrintBridge",
    [string]$DisplayName = "Bonap Print Bridge",
    [string]$PublishDir = (Join-Path $PSScriptRoot "..\publish")
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "..\src\Bonap.PrintBridge\Bonap.PrintBridge.csproj"
Write-Host "Publishing $projectPath" -ForegroundColor Cyan

$publishCmd = "dotnet publish `"$projectPath`" -c $Configuration -r $Runtime --self-contained true -o `"$PublishDir`""
Write-Host $publishCmd -ForegroundColor Yellow
Invoke-Expression $publishCmd

$exePath = Join-Path $PublishDir "Bonap.PrintBridge.exe"
if (-not (Test-Path -Path $exePath)) {
    throw "Executable not found at $exePath"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service already exists. Stopping and removing..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$create = sc.exe create $ServiceName binPath= "`"$exePath`"" DisplayName= "$DisplayName" start= auto
Write-Host $create

sc.exe description $ServiceName "Bonap Print Bridge local HTTPS API" | Out-Null

Start-Service -Name $ServiceName
Write-Host "Service $ServiceName installed and started." -ForegroundColor Green
