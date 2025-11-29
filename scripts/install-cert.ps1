param(
    [string]$PfxPath = (Join-Path $PSScriptRoot "..\certs\localhost.pfx"),
    [string]$CerPath = (Join-Path $PSScriptRoot "..\certs\localhost.cer"),
    [string]$Password = "bonap-bridge"
)

$ErrorActionPreference = "Stop"

Write-Host "Creating local HTTPS certificate for Bonap Print Bridge" -ForegroundColor Cyan

$certName = "Bonap Print Bridge Localhost"
$certStorePath = "Cert:\LocalMachine\My"
$rootStorePath = "Cert:\LocalMachine\Root"

if (-not (Test-Path -Path $certStorePath)) {
    throw "LocalMachine certificate store is not available. Please run as administrator."
}

$existing = Get-ChildItem -Path $certStorePath | Where-Object { $_.Subject -eq "CN=$certName" } | Select-Object -First 1
if (-not $existing) {
    $existing = New-SelfSignedCertificate -DnsName "localhost", "127.0.0.1" -FriendlyName $certName -CertStoreLocation $certStorePath -NotAfter (Get-Date).AddYears(5) -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256
}

$passwordSecure = ConvertTo-SecureString $Password -AsPlainText -Force

$certDirectory = Split-Path -Path $PfxPath -Parent
if (-not (Test-Path -Path $certDirectory)) {
    New-Item -ItemType Directory -Path $certDirectory | Out-Null
}

Export-PfxCertificate -Cert $existing -FilePath $PfxPath -Password $passwordSecure -Force | Out-Null
Export-Certificate -Cert $existing -FilePath $CerPath -Type CERT -Force | Out-Null
Import-Certificate -FilePath $CerPath -CertStoreLocation $rootStorePath | Out-Null

function Ensure-Directory([string]$Path) {
    if (-not (Test-Path -Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

$programData = [Environment]::GetFolderPath('CommonApplicationData')
$programDataCertDir = Join-Path $programData 'BonapPrintBridge\\certs'
$projectCertDir = Join-Path $PSScriptRoot '..\\src\\Bonap.PrintBridge\\certs'

Ensure-Directory -Path $programDataCertDir
Ensure-Directory -Path $projectCertDir

Copy-Item -Path $PfxPath -Destination (Join-Path $programDataCertDir 'localhost.pfx') -Force
Copy-Item -Path $CerPath -Destination (Join-Path $programDataCertDir 'localhost.cer') -Force
Copy-Item -Path $PfxPath -Destination (Join-Path $projectCertDir 'localhost.pfx') -Force
Copy-Item -Path $CerPath -Destination (Join-Path $projectCertDir 'localhost.cer') -Force

Write-Host "Certificate exported and trusted." -ForegroundColor Green
Write-Host "PfxPath: $PfxPath"
Write-Host "Password: $Password"
Write-Host "Thumbprint: $($existing.Thumbprint)"
