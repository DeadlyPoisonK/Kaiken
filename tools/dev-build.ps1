<#
  Build de prueba del modo desarrollo de Kaiken (ver src\Kaiken\DevMode.cs).

  Compila el proyecto con un nombre de assembly único (KaikenDev_yyyyMMdd_HHmmss), incluida la
  carpeta src\Kaiken\Dev\, y deja la DLL en %APPDATA%\Kaiken\dev\<versión>\. De ahí la toman el
  botón Dev de la cinta y las operaciones dev_* del puente en vivo, sin reiniciar Revit.

  Uso:
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev-build.ps1 -RevitVersion 2025

  Ojo: desde la shell de Claude Code en la app de escritorio NO sirve. La app es MSIX y Windows
  redirige lo que sus procesos escriben en %APPDATA% a una copia privada que Revit no ve. Correrlo
  desde una PowerShell abierta fuera de la app, o con la herramienta MCP build_dev_addin.
#>
param(
    [ValidatePattern('^\d{4}$')]
    [string]$RevitVersion = "2025"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$name    = "KaikenDev_" + (Get-Date -Format "yyyyMMdd_HHmmss")
$srcDir  = (Resolve-Path (Join-Path $PSScriptRoot "..\src\Kaiken")).Path
$project = Join-Path $srcDir "Kaiken.csproj"
$stage   = Join-Path $srcDir "bin\Dev\$RevitVersion"
$obj     = Join-Path $srcDir "obj\Dev\$RevitVersion"
$devDir  = Join-Path $env:APPDATA "Kaiken\dev\$RevitVersion"

# Cada build tiene otro nombre: sin esto se acumulan en bin\Dev y obj\Dev.
Remove-Item (Join-Path $stage "KaikenDev_*"), (Join-Path $obj "KaikenDev_*") -ErrorAction SilentlyContinue

# Intermedios aparte (obj\Dev\<versión>) para no mezclarse con el build normal del add-in.
# Las rutas terminan en "/" y no en "\": un \" final rompe el paso de argumentos a dotnet.
dotnet build $project -c Debug -nologo -v:minimal -clp:NoSummary `
    "-p:RevitVersion=$RevitVersion" "-p:AssemblyName=$name" "-p:KaikenDev=true" `
    "-p:IntermediateOutputPath=$obj/" "-p:OutDir=$stage/"
if ($LASTEXITCODE -ne 0) {
    Write-Output "BUILD FALLÓ (Revit $RevitVersion)"
    exit 1
}

New-Item -ItemType Directory -Force $devDir | Out-Null
Copy-Item (Join-Path $stage "$name.dll"), (Join-Path $stage "$name.pdb") $devDir

# Builds viejos: los que Revit tiene cargados están bloqueados hasta cerrarlo y no se borran.
Get-ChildItem $devDir -Filter "KaikenDev_*.dll" | Where-Object { $_.BaseName -ne $name } | ForEach-Object {
    Remove-Item $_.FullName -ErrorAction SilentlyContinue
    if (-not (Test-Path $_.FullName)) {
        Remove-Item ([IO.Path]::ChangeExtension($_.FullName, ".pdb")) -ErrorAction SilentlyContinue
    }
}

Write-Output "OK (Revit $RevitVersion): $(Join-Path $devDir "$name.dll")"
