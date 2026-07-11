@echo off
rem Start DLsiteMedia (WPF). Must run from repo root: DLsiteMedia.db and log/ resolve via working directory.
cd /d "%~dp0"
dotnet run --project src\DLsiteMedia.csproj -c Release
