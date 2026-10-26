param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'publish'))
$ErrorActionPreference = 'Stop'
dotnet publish (Join-Path $PSScriptRoot 'src\WhatsAppTasks.App\WhatsAppTasks.App.csproj') -c Release -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'No fue posible publicar WhatsApp Tareas.' }
Write-Output "Aplicación publicada en: $OutputDirectory"
