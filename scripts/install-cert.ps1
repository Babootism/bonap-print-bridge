param(
    [string]$PfxPath = (Join-Path $PSScriptRoot "..\certs\localhost.pfx"),
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
    $existing = New-SelfSignedCertificate -DnsName "localhost", "127.0.0.1" -FriendlyName $certName -CertStoreLocation $certStorePath -NotAfter (Get-Date).AddYears(5) -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 2048 -SignatureAlgorithm "SHA256"
}

$passwordSecure = ConvertTo-SecureString $Password -AsPlainText -Force

$certDirectory = Split-Path -Path $PfxPath -Parent
if (-not (Test-Path -Path $certDirectory)) {
    New-Item -ItemType Directory -Path $certDirectory | Out-Null
}

Export-PfxCertificate -Cert $existing -FilePath $PfxPath -Password $passwordSecure -Force | Out-Null
Import-Certificate -FilePath $PfxPath -CertStoreLocation $rootStorePath | Out-Null

Write-Host "Certificate exported to $PfxPath and trusted on the local machine." -ForegroundColor Green
