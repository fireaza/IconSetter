@echo off
REM Builds a single, standalone IconSetter.exe that needs no installer and no
REM separate .NET runtime install on the target machine.
REM
REM Requirements to RUN this script: the .NET 8 SDK (not just the runtime).
REM Download: https://dotnet.microsoft.com/download/dotnet/8.0
REM
REM The resulting exe (in bin\Release\net8.0-windows\win-x64\publish\) is all
REM that needs to be copied/shared - everything else is embedded in it.

cd /d "%~dp0"
dotnet publish -c Release
echo.
echo ============================================================
echo Done. Your standalone exe is at:
echo   bin\Release\net8.0-windows\win-x64\publish\IconSetter.exe
echo ============================================================
pause
