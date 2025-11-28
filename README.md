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

Ouvrir le tiroir-caisse (impulsion ESC p) :
```bash
# impulsions par défaut t1=25, t2=250
dotnet run --project src/Bonap.PrintBridge/Bonap.PrintBridge.csproj "Nom de l'imprimante" --drawer

# sélectionner la broche 2 (m=1) avec des valeurs personnalisées
dotnet run --project src/Bonap.PrintBridge/Bonap.PrintBridge.csproj "Nom de l'imprimante" --drawer1 50 200
```

`t1` et `t2` doivent être des entiers compris entre `0` et `255` et seront envoyés via la commande ESC/POS `ESC p m t1 t2`.

Sur un système non Windows, la génération du payload ESC/POS fonctionne mais rien n'est envoyé à l'imprimante.

## Scripts PowerShell
- `scripts/install-cert.ps1` : installe un certificat PFX dans le magasin Windows.
- `scripts/install-service.ps1` : enregistre Bonap.PrintBridge comme service Windows avec les arguments fournis.

## Structure
- `src/Bonap.PrintBridge/Program.cs` : point d'entrée, construit un ticket de test et appelle l'envoi brut.
- `src/Bonap.PrintBridge/EscPos.cs` : helpers de commandes ESC/POS courantes.
- `src/Bonap.PrintBridge/RawPrinterHelper.cs` : interopération Winspool pour l'impression brute (Windows uniquement).
