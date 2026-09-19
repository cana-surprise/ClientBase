<#
  Собирает установщик «База клиентов»:
    1. публикует программу «автономно» (внутри уже есть .NET — на другом компьютере ничего ставить не нужно);
    2. упаковывает её в Setup.exe (Inno Setup).
  Результат: dist\BazaKlientov-Setup-<версия>.exe
  Версия берётся из ClientBase.csproj (<Version>) — перед выпуском новой версии поднимите её.
#>
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$csproj = [xml](Get-Content (Join-Path $root "ClientBase.csproj") -Raw -Encoding UTF8)
$version = $csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "В ClientBase.csproj не найдена <Version>." }
Write-Host "Версия: $version"

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 не найден. Установите: winget install JRSoftware.InnoSetup" }

$publish = Join-Path $root "publish-installer"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "Публикация программы..."
dotnet publish (Join-Path $root "ClientBase.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с ошибкой." }

Write-Host "Сборка установщика..."
& $iscc "/DAppVersion=$version" (Join-Path $root "Installer\ClientBase.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup завершился с ошибкой." }

$setup = Join-Path $root "dist\BazaKlientov-Setup-$version.exe"
Write-Host ("Готово: {0} ({1:N1} МБ)" -f $setup, ((Get-Item $setup).Length / 1MB))
