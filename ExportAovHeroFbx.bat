@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul

rem ============================================================
rem  AOV/RoV Hero FBX Exporter  (AssetStudioMod-Aov)
rem  Exports a hero character rig (skeleton + skinned mesh) as
rem  one FBX with every animation clip bound as an action.
rem
rem  Usage:
rem    ExportAovHeroFbx.bat                       -> Damo defaults
rem    ExportAovHeroFbx.bat <rigFilter>           -> pick rig
rem    ExportAovHeroFbx.bat <rigFilter> <bundle|hero|all>
rem    ExportAovHeroFbx.bat <rigFilter> <src> <heroId>
rem
rem  Defaults: rigFilter=5281_DaMo_LOD1  src=all  heroId=528
rem ============================================================

set "RIG=%~1"
if "%RIG%"=="" set "RIG=5281_DaMo_LOD1"
set "SRC=%~2"
if "%SRC%"=="" set "SRC=all"
set "HERO_ID=%~3"
if "%HERO_ID%"=="" set "HERO_ID=528"

set "GAME=%AOV_GAME_DIR%"
if "%GAME%"=="" set "GAME=F:\AOV-Hack\MyGame\IQOO-Z10\2022.V3\assetbundle\battle"
set "WORK=%AOV_WORK_DIR%"
if "%WORK%"=="" set "WORK=F:\AOV-Hack\hero_work"
set "OUT=%AOV_OUT_DIR%"
if "%OUT%"=="" set "OUT=F:\AOV-Hack\hero_fbx"
set "TOOL=%AOV_TOOL_DLL%"
if "%TOOL%"=="" set "TOOL=F:\AOV-Hack\AssetStudioMod-Aov\Tools\AovHeroExport\bin\Release\net8.0\AovHeroExport.dll"

if not exist "%TOOL%" (
    echo [ERROR] Tool not found: %TOOL%
    echo         Build it first:  dotnet build F:\AOV-Hack\AssetStudioMod-Aov\Tools\AovHeroExport -c Release
    pause
    exit /b 1
)
if not exist "%GAME%\hero" (
    echo [ERROR] Game hero folder not found: %GAME%\hero
    pause
    exit /b 1
)

echo ============================================================
echo  Hero id   : %HERO_ID%
echo  Rig filter: %RIG%
echo  Clip src  : %SRC%
echo  Work dir  : %WORK%
echo  Output    : %OUT%
echo ============================================================
echo.

rem -- Refresh the work folder with every bundle of this hero
rem -- (base + all skins); clips may live in a different file
rem -- than the rig, so both families must be loaded together.
if not exist "%WORK%" mkdir "%WORK%"
del /q "%WORK%\*" >nul 2>&1
copy /y "%GAME%\hero\%HERO_ID%_*.assetbundle" "%WORK%\" >nul 2>&1
copy /y "%GAME%\skin\%HERO_ID%*_*.assetbundle" "%WORK%\" >nul 2>&1
copy /y "%GAME%\skin\%HERO_ID%_*.assetbundle" "%WORK%\" >nul 2>&1
set /a COUNT=0
for %%F in ("%WORK%\*.assetbundle") do set /a COUNT+=1
echo Copied %COUNT% bundle(s) to work folder.
if %COUNT%==0 (
    echo [ERROR] No bundles matched %HERO_ID%_* in %GAME%\hero or %GAME%\skin
    pause
    exit /b 1
)
echo.

echo --- Listing matching rigs (clips per bundle) ---
dotnet "%TOOL%" "%WORK%" "%OUT%" "%RIG%" list %SRC%
echo.

echo --- Exporting FBX ---
dotnet "%TOOL%" "%WORK%" "%OUT%" "%RIG%" export %SRC%
set "EXITCODE=%ERRORLEVEL%"

echo.
if "%EXITCODE%"=="0" (
    echo Done. Output: %OUT%
) else (
    echo Finished with errors. Exit code: %EXITCODE%
)
pause
exit /b %EXITCODE%
