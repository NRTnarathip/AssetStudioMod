@echo off
setlocal
chcp 65001 >nul

rem ============================================================
rem  AOV Hero Skill Texture Dumper
rem  - input : hero assetbundle folder
rem  - output: one folder per bundle, containing 128x128
rem            ETC_RGB4 / Clamp / Bilinear textures as PNG
rem ============================================================

set "ROOT=%~dp0"
set "INPUT=F:\AOV-Hack\MyGame\IQOO-Z10\2022.V3\assetbundle\battle\hero"
set "OUTPUT=F:\AOV-Hack\MyDumpHeroSkills"

rem Change this to "*battleskill*.assetbundle" if you only want
rem skill bundles instead of every bundle in battle\hero.
set "PATTERN=*.assetbundle"

if not exist "%INPUT%" (
    echo [ERROR] Input folder not found:
    echo         %INPUT%
    pause
    exit /b 1
)

echo ============================================================
echo  Input   : %INPUT%
echo  Output  : %OUTPUT%
echo  Pattern : %PATTERN%
echo ============================================================
echo.

dotnet run --project "%ROOT%Tools\AovHeroSkillDump\AovHeroSkillDump.csproj" -c Release -- "%INPUT%" "%OUTPUT%" "%PATTERN%"
set "EXITCODE=%ERRORLEVEL%"

echo.
if "%EXITCODE%"=="0" (
    echo Done. Output: %OUTPUT%
) else (
    echo Finished with errors. Exit code: %EXITCODE%
)
pause
exit /b %EXITCODE%
