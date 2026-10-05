@echo off
cd /d "%~dp0"
dotnet publish "%~dp0Renamer.App\Renamer.App.csproj" -c Release -r win-x64 --self-contained true -p:UseSharedCompilation=false -o "%~dp0dist"
exit /b %errorlevel%
