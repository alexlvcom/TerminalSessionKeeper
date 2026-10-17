param([string]$IsccPath)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    [xml]$project = Get-Content src/TerminalSessionKeeper/TerminalSessionKeeper.csproj
    $version = [string]($project.Project.PropertyGroup | Where-Object { $_.Version }).Version
    if (!$version) { $version = '1.3.0' }
    if (!$IsccPath) {
        $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($compiler) { $IsccPath = $compiler.Source }
        else {
            $IsccPath = @(
                "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
                "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
            ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        }
    }
    if (!$IsccPath -or !(Test-Path $IsccPath)) {
        throw 'Install Inno Setup 6 or pass -IsccPath pointing to ISCC.exe.'
    }
    if (Test-Path publish/installer) { Remove-Item publish/installer -Recurse -Force }
    dotnet publish src/TerminalSessionKeeper/TerminalSessionKeeper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/installer
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    & $IsccPath "/DAppVersion=$version" installer/TerminalSessionKeeper.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Join-Path $root 'dist/TerminalSessionKeeper-Setup.exe'
    $hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  TerminalSessionKeeper-Setup.exe" | Set-Content dist/SHA256SUMS.txt -Encoding ascii
    Write-Host "Installer: $installer (version $version)"
}
finally { Pop-Location }
