param (
    [Parameter(Mandatory = $true)][string]$PfxPath,
    [Parameter(Mandatory = $true)][string]$Password,
    [Parameter()][string]$StoreLocation = "CurrentUser",
    [Parameter()][string]$StoreName = "My"
)

Write-Host "Installing certificate from $PfxPath into $StoreLocation/$StoreName" -ForegroundColor Cyan

if (-not (Test-Path -Path $PfxPath)) {
    throw "PFX file not found: $PfxPath"
}

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
$cert.Import($PfxPath, $Password, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet)

$store = New-Object System.Security.Cryptography.X509Certificates.X509Store($StoreName, $StoreLocation)
$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
try {
    $store.Add($cert)
    Write-Host "Certificate installed successfully." -ForegroundColor Green
} finally {
    $store.Close()
}
