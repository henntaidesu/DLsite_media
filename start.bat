@echo off
rem Start R-18MediaLibrary (WPF). Must run from repo root: R-18MediaLibrary.db and log/ resolve via working directory.
cd /d "%~dp0"
dotnet run --project src\R-18MediaLibrary.csproj -c Release
