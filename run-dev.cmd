@echo off
REM Runs the app straight from source - always the current code, no publish step.
REM Use this while developing; the published exe in publish\ is the release artifact
REM and only changes when you rebuild it.
cd /d "%~dp0"
dotnet run --project src\MtgaCollectionAdvisor.Desktop\MtgaCollectionAdvisor.Desktop.csproj -c Release
