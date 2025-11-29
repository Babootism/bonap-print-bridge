# Bonap Print Bridge

Passerelle HTTPS locale pour exposer une API d'impression ESC/POS utilisable par le POS web Bonap.

## Configuration
- `src/Bonap.PrintBridge/appsettings.json` contient les valeurs par défaut :
  - `Bridge:Port` : `49001`
  - `Bridge:Token` : jeton obligatoire à transmettre via l'en-tête `X-Bridge-Token`.
  - `Bridge:DefaultPrinterName` : nom d'imprimante par défaut (optionnel).
  - `Bridge:DefaultDrawerPin` : `0` par défaut.
  - `Kestrel:Endpoints:Https` : écoute sur `https://127.0.0.1:49001` avec le certificat PFX généré.
- Les logs sont écrits dans `%ProgramData%\BonapPrintBridge\logs\bridge.log`.

## Pré-requis
- Windows (l'accès RAW à l'imprimante utilise Winspool).
- [.NET 8 SDK](https://dotnet.microsoft.com/download).
- Droits administrateur pour installer le certificat et le service Windows.

## Générer le certificat local
```powershell
# Depuis la racine du dépôt
powershell -ExecutionPolicy Bypass -File .\scripts\install-cert.ps1
# Le certificat est exporté dans certs\localhost.pfx avec le mot de passe "bonap-bridge"
```

## Lancer l'API en console
```powershell
# Depuis la racine du dépôt
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --project .\src\Bonap.PrintBridge\Bonap.PrintBridge.csproj
```
L'API écoute sur `https://127.0.0.1:49001` (certificat auto-signé).

## Endpoints
Tous les appels doivent inclure l'en-tête `X-Bridge-Token` correspondant à `Bridge:Token`, sauf `GET /health` qui reste publique. CORS autorise uniquement `Origin: https://bonap.ceramix.ovh`.

- `GET /health` → `{ ok: true, version, time, httpsEnabled, port, listeningUrls }`
- `GET /printers` → `[{ name, isDefault }]`
- `POST /print`
  ```json
  {
    "printerName": "optionnel",
    "jobName": "optionnel",
    "dataBase64": "...",
    "contentType": "raw-escpos"
  }
  ```
- `POST /drawer/open`
  ```json
  { "printerName": "optionnel", "pin": 0, "t1": 25, "t2": 250 }
  ```
- `POST /receipt/print`
  ```json
  {
    "printerName": "optionnel",
    "text": "Texte du ticket",
    "openDrawer": true,
    "pin": 0
  }
  ```
- `GET /logs/tail?lines=200` → retourne les dernières lignes du fichier `%ProgramData%\BonapPrintBridge\logs\bridge.log`.

## Interface d'admin locale
- Ouvrir `https://127.0.0.1:<port>/admin?token=<X-Bridge-Token>` pour charger la page (protégée par le jeton requis par l'en-tête `X-Bridge-Token`).
- La page affiche l'état `health`, la liste des imprimantes, envoie un ticket de test, ouvre le tiroir (PIN 0) et rafraîchit les logs via les endpoints ci-dessus.

### Exemples PowerShell (Invoke-RestMethod)
```powershell
$token = "change-me"
$baseUrl = "https://127.0.0.1:49001"

Invoke-RestMethod -Method Get "$baseUrl/health" -Headers @{ "X-Bridge-Token" = $token } -SkipCertificateCheck

Invoke-RestMethod -Method Get "$baseUrl/printers" -Headers @{ "X-Bridge-Token" = $token } -SkipCertificateCheck

$raw = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes("Hello ESC/POS"))
Invoke-RestMethod -Method Post "$baseUrl/print" -Headers @{ "X-Bridge-Token" = $token } -ContentType "application/json" -Body (@{ printerName = ""; dataBase64 = $raw; contentType = "raw-escpos" } | ConvertTo-Json) -SkipCertificateCheck

Invoke-RestMethod -Method Post "$baseUrl/drawer/open" -Headers @{ "X-Bridge-Token" = $token } -ContentType "application/json" -Body (@{ pin = 0 } | ConvertTo-Json) -SkipCertificateCheck

Invoke-RestMethod -Method Post "$baseUrl/receipt/print" -Headers @{ "X-Bridge-Token" = $token } -ContentType "application/json" -Body (@{ text = "Ticket de test"; openDrawer = $true } | ConvertTo-Json) -SkipCertificateCheck
```

## Installation en service Windows
1. Exécuter `powershell -ExecutionPolicy Bypass -File .\scripts\install-cert.ps1` en PowerShell administrateur (depuis la racine du dépôt).
2. Exécuter `powershell -ExecutionPolicy Bypass -File .\scripts\install-service.ps1` en PowerShell administrateur.
3. Tester `https://127.0.0.1:<port>/health` (où `<port>` correspond à `Bridge:Port`).

Le service `BonapPrintBridge` est publié en `Release`, en `win-x64`, auto-démarré et pointe vers l'exécutable généré (self-contained).

## Structure
- `src/Bonap.PrintBridge/Program.cs` : Minimal API HTTPS + endpoints d'impression / tiroir.
- `src/Bonap.PrintBridge/EscPos.cs` : helpers ESC/POS.
- `src/Bonap.PrintBridge/RawPrinterHelper.cs` : interop Winspool pour l'impression brute.
- `src/Bonap.PrintBridge/FileLoggerProvider.cs` : provider de log simple vers fichier.
