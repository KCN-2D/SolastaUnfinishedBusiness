@echo off
setlocal

cd /d "%~dp0"

set "SolastaInstallDir=%~dp0SolastaFiles"
set "RequiredAssembly=%SolastaInstallDir%\Solasta_Data\Managed\Assembly-CSharp.dll"
set "RequiredHarmony=%SolastaInstallDir%\Solasta_Data\Managed\UnityModManager\0Harmony.dll"
set "RequiredUmm=%SolastaInstallDir%\Solasta_Data\Managed\UnityModManager\UnityModManager.dll"
set "OutputZip=%~dp0SolastaUnfinishedBusiness.zip"
set "OutputFolder=%~dp0SolastaFiles\Mods\SolastaUnfinishedBusiness"
set "OutputInfoJson=%OutputFolder%\Info.json"
set "TranslationsZip=%~dp0SolastaUnfinishedBusiness\Resources\Translations.zip"
set "DotnetVersion="
set "DotnetMajor="
set "GitShortSha="
set "CustomBuildLabel=CUSTOM local"

echo Building SolastaUnfinishedBusiness UMM test package...

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: dotnet was not found in PATH.
    echo Install .NET SDK 9 or newer and try again.
    exit /b 1
)

where tar >nul 2>nul
if errorlevel 1 (
    echo ERROR: tar.exe was not found in PATH.
    echo Install a recent Windows 10/11 build or make tar.exe available and try again.
    exit /b 1
)

for /f "usebackq delims=" %%I in (`dotnet --version`) do set "DotnetVersion=%%I"
for /f "tokens=1 delims=." %%I in ("%DotnetVersion%") do set "DotnetMajor=%%I"

where git >nul 2>nul
if not errorlevel 1 (
    for /f "usebackq delims=" %%I in (`git rev-parse --short HEAD 2^>nul`) do set "GitShortSha=%%I"
)

if defined GitShortSha (
    set "CustomBuildLabel=CUSTOM %GitShortSha%"
)

if not defined DotnetMajor (
    echo ERROR: Failed to determine the installed dotnet SDK version.
    exit /b 1
)

if %DotnetMajor% LSS 9 (
    echo ERROR: .NET SDK 9 or newer is required.
    echo Found:
    echo   %DotnetVersion%
    echo The project uses C# language version 13, which does not compile with .NET 8 SDKs.
    exit /b 1
)

if not exist "%RequiredAssembly%" (
    echo ERROR: Missing required file:
    echo   %RequiredAssembly%
    exit /b 1
)

if not exist "%RequiredHarmony%" (
    echo ERROR: Missing required file:
    echo   %RequiredHarmony%
    exit /b 1
)

if not exist "%RequiredUmm%" (
    echo ERROR: Missing required file:
    echo   %RequiredUmm%
    exit /b 1
)

if exist "%TranslationsZip%" (
    del /f /q "%TranslationsZip%"
    if errorlevel 1 exit /b %errorlevel%
)

echo Restoring packages...
dotnet restore SolastaUnfinishedBusiness.sln
if errorlevel 1 exit /b %errorlevel%

echo Cleaning Release Workflow...
dotnet clean SolastaUnfinishedBusiness.sln -c "Release Workflow"
if errorlevel 1 exit /b %errorlevel%

echo Building Release Workflow...
dotnet build SolastaUnfinishedBusiness.sln --no-restore -c "Release Workflow"
if errorlevel 1 exit /b %errorlevel%

if not exist "%OutputFolder%\SolastaUnfinishedBusiness.dll" (
    echo ERROR: Build completed but expected output was not found:
    echo   %OutputFolder%\SolastaUnfinishedBusiness.dll
    exit /b 1
)

if not exist "%OutputInfoJson%" (
    echo ERROR: Build completed but expected output was not found:
    echo   %OutputInfoJson%
    exit /b 1
)

echo Marking build output as %CustomBuildLabel%...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $path='%OutputInfoJson%'; $info = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json; $baseVersion = [string]$info.Version; $label = '%CustomBuildLabel%'; $info.DisplayName = 'Unfinished Business [' + $label + ']'; $info | Add-Member -NotePropertyName CustomBuild -NotePropertyValue $true -Force; $info | Add-Member -NotePropertyName CustomBuildLabel -NotePropertyValue $label -Force; $info | Add-Member -NotePropertyName CustomBuildBaseVersion -NotePropertyValue $baseVersion -Force; $json = $info | ConvertTo-Json -Depth 8; [System.IO.File]::WriteAllText($path, $json + [Environment]::NewLine)"
if errorlevel 1 exit /b %errorlevel%

if exist "%OutputZip%" (
    del /f /q "%OutputZip%"
    if errorlevel 1 exit /b %errorlevel%
)

echo Packaging %OutputZip%...
pushd "%~dp0SolastaFiles\Mods"
if errorlevel 1 (
    echo ERROR: Failed to access the packaging folder:
    echo   %~dp0SolastaFiles\Mods
    exit /b 1
)

tar -acf "%OutputZip%" "SolastaUnfinishedBusiness"
set "TarExit=%errorlevel%"
popd

if not "%TarExit%"=="0" exit /b %TarExit%

echo Verifying %OutputZip%...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem; $zip=[System.IO.Compression.ZipFile]::OpenRead('%OutputZip%'); try { $entry = $zip.GetEntry('SolastaUnfinishedBusiness/Info.json'); if (-not $entry) { throw 'Archive is missing SolastaUnfinishedBusiness/Info.json.' }; $badEntries=$zip.Entries | Where-Object { $_.FullName.Contains('\') }; if ($badEntries) { throw ('Archive contains non-UMM-compatible entry paths: ' + (($badEntries | Select-Object -First 5 -ExpandProperty FullName) -join ', ')) }; $reader = New-Object System.IO.StreamReader($entry.Open()); try { $info = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }; if (-not $info.DisplayName.Contains('[CUSTOM ')) { throw 'Archive Info.json is missing the custom display name marker.' }; if (-not $info.CustomBuild) { throw 'Archive Info.json is missing CustomBuild=true.' } } finally { $zip.Dispose() }"
if errorlevel 1 exit /b %errorlevel%

echo Done.
echo Created:
echo   %OutputZip%
echo.
echo Next:
echo   1. Open UMM and go to the Mods tab.
echo   2. Drag %OutputZip% onto the Mods tab to install it.
echo   3. Use only UMM versions 0.24.0 through 0.27.10.

endlocal
