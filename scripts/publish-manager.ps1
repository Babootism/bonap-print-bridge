param(
    [string]$Configuration = "Release"
)

$projectPath = Join-Path $PSScriptRoot "..\src\Bonap.PrintBridge.Manager\Bonap.PrintBridge.Manager.csproj"
$outputPath = Join-Path $PSScriptRoot "..\publish\manager"

Write-Host "Publishing manager from $projectPath to $outputPath (Configuration=$Configuration)..."

dotnet publish $projectPath -c $Configuration -r win-x64 -o $outputPath --self-contained:$false -p:PublishSingleFile=false
