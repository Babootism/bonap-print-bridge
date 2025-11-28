# Bonap Print Bridge

Passerelle minimale pour envoyer des commandes ESC/POS à une imprimante Windows via .NET 8.

## Pré-requis
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Windows pour l'envoi direct aux imprimantes via l'API Winspool

## Construction et exécution
```bash
dotnet build src/Bonap.PrintBridge/Bonap.PrintBridge.csproj
```

Pour envoyer un ticket de test :
```bash
dotnet run --project src/Bonap.PrintBridge/Bonap.PrintBridge.csproj "Nom de l'imprimante" "Bonjour depuis Bonap !"
```

Pour ouvrir le tiroir-caisse via ESC/POS (broche 0 ou 1) :
```bash
dotnet run --project src/Bonap.PrintBridge/Bonap.PrintBridge.csproj "Nom de l'imprimante" --drawer
dotnet run --project src/Bonap.PrintBridge/Bonap.PrintBridge.csproj "Nom de l'imprimante" --drawer1
```

Sur un système non Windows, la génération du payload ESC/POS fonctionne mais rien n'est envoyé à l'imprimante.

## Scripts PowerShell
- `scripts/install-cert.ps1` : installe un certificat PFX dans le magasin Windows.
- `scripts/install-service.ps1` : enregistre Bonap.PrintBridge comme service Windows avec les arguments fournis.

## Structure
- `src/Bonap.PrintBridge/Program.cs` : point d'entrée, construit un ticket de test et appelle l'envoi brut.
- `src/Bonap.PrintBridge/EscPos.cs` : helpers de commandes ESC/POS courantes.
- `src/Bonap.PrintBridge/RawPrinterHelper.cs` : interopération Winspool pour l'impression brute (Windows uniquement).
