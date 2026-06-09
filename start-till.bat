@echo off
echo Stopping any running instances...
taskkill /F /IM SterlingLams.Web.exe /T 2>NUL
taskkill /F /IM dotnet.exe /T 2>NUL
timeout /t 2 /nobreak >NUL

echo Building latest code...
cd /d "%~dp0src\SterlingLams.Web"
dotnet publish -c Release -o "C:\Temp\sterling-pub" 2>NUL
if errorlevel 1 (
    echo Build failed. Check for errors.
    pause
    exit /b 1
)

echo Starting SterlingLams on http://localhost:5000 ...
set ASPNETCORE_ENVIRONMENT=Development
dotnet "C:\Temp\sterling-pub\SterlingLams.Web.dll" --urls "http://localhost:5000"
