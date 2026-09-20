@echo off
setlocal EnableExtensions EnableDelayedExpansion
title C-Sweet
cd /d "%~dp0"

where dotnet.exe >nul 2>nul
if errorlevel 1 goto missing_sdk

dotnet.exe --list-sdks | "%SystemRoot%\System32\findstr.exe" /B /C:"10." >nul
if errorlevel 1 goto missing_sdk

where docker.exe >nul 2>nul
if errorlevel 1 goto missing_docker

docker.exe info >nul 2>nul
if not errorlevel 1 goto docker_ready

if exist "%ProgramFiles%\Docker\Docker\Docker Desktop.exe" (
    echo Starting Docker Desktop...
    start "" "%ProgramFiles%\Docker\Docker\Docker Desktop.exe"
)

echo Waiting for the Docker engine. This usually takes less than two minutes...
set /a docker_attempts=0
:wait_for_docker
docker.exe info >nul 2>nul
if not errorlevel 1 goto docker_ready
set /a docker_attempts+=1
if %docker_attempts% GEQ 60 goto docker_not_ready
set /a docker_status=docker_attempts %% 5
if !docker_status! EQU 0 (
    set /a docker_elapsed=docker_attempts * 2
    echo Docker is still starting... !docker_elapsed! seconds elapsed.
)
timeout.exe /t 2 /nobreak >nul
goto wait_for_docker

:docker_ready
set "launch_profile=https"
dotnet.exe dev-certs https --check --trust >nul 2>nul
if not errorlevel 1 goto certificate_ready

echo Trusting the local HTTPS development certificate...
echo Windows may ask you to confirm this one-time developer certificate setup.
dotnet.exe dev-certs https --trust
if errorlevel 1 goto certificate_fallback
dotnet.exe dev-certs https --check --trust >nul 2>nul
if not errorlevel 1 goto certificate_ready

:certificate_fallback
echo HTTPS certificate trust is unavailable. Starting the local dashboard over HTTP.
echo You can repair certificate trust later with: dotnet dev-certs https --trust
set "launch_profile=http"

:certificate_ready
echo Starting C-Sweet...
echo The browser will open automatically when the application is ready.
echo Keep this window open while using C-Sweet.
echo.
dotnet.exe run --project "src\CSweet.AppHost\CSweet.AppHost.csproj" --launch-profile "%launch_profile%"
if errorlevel 1 goto failed
exit /b 0

:missing_sdk
echo C-Sweet needs the Microsoft .NET 10 SDK when it is run from source.
echo The official download page will open now. Install the Windows x64 SDK,
echo then double-click Start-CSweet again.
start "" "https://dotnet.microsoft.com/download/dotnet/10.0"
pause
exit /b 1

:missing_docker
echo C-Sweet uses Docker Desktop to run PostgreSQL and other trusted development infrastructure.
echo Docker is not used as the security boundary for untrusted agents.
echo The official Docker Desktop download page will open now.
start "" "https://www.docker.com/products/docker-desktop/"
pause
exit /b 1

:docker_not_ready
echo Docker Desktop is installed but its Linux container engine did not become ready.
echo Open Docker Desktop, wait until it reports that the engine is running, then try again.
pause
exit /b 1

:failed
echo.
echo C-Sweet stopped before it was ready. The messages above contain the diagnostic details.
pause
exit /b 1
