@echo off
cd /d "%~dp0"
dotnet run --project "%~dp0Renamer.Tests\Renamer.Tests.csproj" -c Release -- "%~dp0test-results.json"
exit /b %errorlevel%
