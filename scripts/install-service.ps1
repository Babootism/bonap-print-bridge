param(
    [ValidateSet("install", "reinstall", "start", "stop", "status")]
    [string]$Action = "reinstall",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$ServiceName = "BonapPrintBridge",
    [string]$DisplayName = "Bonap Print Bridge",
    [string]$PublishDir
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
if (-not $PublishDir) {
    $PublishDir = Join-Path $repoRoot "publish"
}

$publishTarget = $PublishDir
$PublishDir = Resolve-Path -Path $publishTarget -ErrorAction SilentlyContinue
if (-not $PublishDir) {
    $PublishDir = New-Item -ItemType Directory -Path $publishTarget -Force
}

$PublishDir = $PublishDir.ToString()
$projectPath = Join-Path $repoRoot "src\Bonap.PrintBridge\Bonap.PrintBridge.csproj"
$exePath = Join-Path $PublishDir "Bonap.PrintBridge.exe"

function Publish-Bridge {
    Write-Host "Publishing $projectPath" -ForegroundColor Cyan
    $publishCmd = "dotnet publish `"$projectPath`" -c $Configuration -r $Runtime --self-contained true -o `"$PublishDir`""
    Write-Host $publishCmd -ForegroundColor Yellow
    Invoke-Expression $publishCmd

    if (-not (Test-Path -Path $exePath)) {
        throw "Executable not found at $exePath"
    }
}

function Stop-ServiceIfExists {
    param(
        [string]$Name
    )

    $existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($existing -and $existing.Status -ne "Stopped") {
        Write-Host "Stopping $Name..." -ForegroundColor Yellow
        Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
        $existing.WaitForStatus("Stopped", (New-TimeSpan -Seconds 10))
    }
}

function Install-ServiceBridge {
    param(
        [bool]$Force
    )

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing -and -not $Force) {
        throw "Service $ServiceName already exists. Use -Action reinstall to recreate it."
    }

    if ($existing) {
        Write-Host "Service already exists. Stopping and removing..." -ForegroundColor Yellow
        Stop-ServiceIfExists -Name $ServiceName
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }

    Write-Host "Creating service $ServiceName..." -ForegroundColor Cyan
    & sc.exe create $ServiceName binPath= "`"$exePath`"" DisplayName= "`"$DisplayName`"" start= auto | Out-Null
    sc.exe description $ServiceName "Bonap Print Bridge local HTTPS API" | Out-Null
    Start-Service -Name $ServiceName
    Write-Host "Service $ServiceName installed and started." -ForegroundColor Green
}

function Show-ServiceStatus {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        Write-Host "" -ForegroundColor DarkGray
        $service | Select-Object Status, Name, DisplayName, StartType | Format-Table -AutoSize
    }
    else {
        Write-Host "Service $ServiceName is not installed." -ForegroundColor Yellow
    }
}

switch ($Action) {
    "install" {
        Publish-Bridge
        Install-ServiceBridge -Force:$false
    }
    "reinstall" {
        Publish-Bridge
        Install-ServiceBridge -Force:$true
    }
    "start" {
        Start-Service -Name $ServiceName
        Write-Host "Service $ServiceName started." -ForegroundColor Green
    }
    "stop" {
        Stop-ServiceIfExists -Name $ServiceName
        Write-Host "Service $ServiceName stopped." -ForegroundColor Green
    }
    "status" { }
}

Show-ServiceStatus
