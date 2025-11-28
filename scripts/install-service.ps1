param (
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter()][string]$ServiceName = "BonapPrintBridge",
    [Parameter()][string]$DisplayName = "Bonap Print Bridge",
    [Parameter()][string]$PrinterName = "Receipt Printer"
)

Write-Host "Installing $ServiceName for printer '$PrinterName'" -ForegroundColor Cyan

if (-not (Test-Path -Path $ExecutablePath)) {
    throw "Executable not found: $ExecutablePath"
}

$arguments = "`"$PrinterName`" Test depuis le service"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service already exists. Stopping and deleting..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$createResult = sc.exe create $ServiceName binPath= "`"$ExecutablePath`" $arguments" DisplayName= "$DisplayName" start= auto
Write-Host $createResult

sc.exe description $ServiceName "Passerelle d'impression ESC/POS" | Out-Null
Start-Service -Name $ServiceName

Write-Host "Service installed and started." -ForegroundColor Green
