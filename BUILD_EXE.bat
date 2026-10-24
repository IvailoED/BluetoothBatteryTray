@echo off
cd /d "%~dp0"
taskkill /IM BluetoothBatteryTray.exe /F >nul 2>&1
dotnet publish BluetoothBatteryTray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o "%~dp0publish_v30"
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
echo Ready: %~dp0publish_v30\BluetoothBatteryTray.exe
pause
