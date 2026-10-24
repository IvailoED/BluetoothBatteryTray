<img width="1672" height="941" alt="Brand" src="https://github.com/user-attachments/assets/041a8627-3d01-4a8d-882e-ea2c07159191" />
                                                             
A modern battery monitoring experience for Windows 11.

BluetoothBatteryTray is a Windows desktop application designed to help you monitor the battery levels of compatible Bluetooth and wireless devices directly from your system tray.

Featuring a premium Golden Rain interface, customizable battery indicators, intelligent notifications, multi-device management, and multi-monitor support, BluetoothBatteryTray keeps essential battery information accessible without interrupting your workflow.

Features

Battery Monitoring

Display battery percentages directly in the Windows system tray.

Monitor multiple compatible wireless devices.

Automatically refresh available battery information.

Support Bluetooth and selected USB/HID devices when battery data is accessible.

Clearly indicate unavailable battery information.

Smart Device Management

Automatically discover supported devices.

Quickly switch between monitored devices.

Configure device aliases and priorities.

Automatically select another available device when necessary.

Configure device-specific notification preferences.

Smart Notifications

Low-battery notifications at configurable thresholds.

Device connection and disconnection alerts.

Notification controls for individual devices.

Protection against repeated notifications.

Battery Analytics

View battery history for 24 hours, 7 days, or 30 days.

Explore battery discharge trends.

Estimate battery consumption and remaining runtime when sufficient historical data is available.

Export available history data to CSV.

Multi-Monitor Support

Display battery indicators on secondary monitors.

Move secondary indicators to preferred positions.

Save indicator positions.

Customize battery indicator appearance.

Premium Golden Rain Interface

Dark-themed dashboard.

Animated golden particles and light effects.

Smooth visual transitions.

Compact and expanded dashboard modes.

Interactive battery information tooltips.

Additional Features

Quick Device Switch.

Global keyboard shortcut: Ctrl + Alt + B.

Optional automatic startup with Windows.

Manual battery refresh.

Persistent application settings and device profiles.

System Requirements

Windows 11

64-bit operating system

Microsoft Edge WebView2 Runtime

Compatible Bluetooth or wireless peripheral

.NET 8 SDK for building from source

Installation

Open the repository's Releases section.

Download the latest available version.

Extract the downloaded ZIP archive, if applicable.

Run BluetoothBatteryTray.exe.

Locate the battery indicator in the Windows system tray.

Open the application settings and select your preferred device.

Some computers may require Microsoft Edge WebView2 Runtime.

Building from Source

Install the .NET 8 SDK.

Install Visual Studio 2022 or Visual Studio Code with C# development support.

Download or clone the repository.

Open a terminal in the project directory.

Run:

dotnet restore
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

The executable will be generated in the publish output directory.

How to Use

System Tray

The battery indicator displays the currently selected device's reported battery level.

Left-click: Open Quick Device Switch to select a device.

Right-click: Open the application dashboard and settings.

Hover: Display additional battery information.

Keyboard Shortcut

Press Ctrl + Alt + B to open the dashboard.

Battery Alerts

Configure the available notification thresholds and choose which devices should generate notifications.

Battery History

Select a supported time period to review previously recorded battery readings and available statistics.

Multi-Monitor Indicators

Enable secondary monitor indicators to display battery information on additional screens.

Device Compatibility

Battery availability depends on the device, connection method, firmware, driver, and information exposed to Windows.

BluetoothBatteryTray can only display battery percentages when supported data is available.

Some wireless devices using proprietary 2.4 GHz USB receivers may require manufacturer-specific integrations.

Universal battery reporting for all wireless devices is not guaranteed.

Privacy

BluetoothBatteryTray is designed to process battery information locally.

Review the application's source code and dependencies before deployment.

Known Limitations

Not every wireless device exposes battery information to Windows.

Battery estimates depend on available historical readings.

Some devices may expose battery percentages only through manufacturer software.

Windows controls the physical size of system tray icons.

Certain Bluetooth connection events may require periodic device scanning.

Technology Stack

C#

.NET 8

Windows Forms

Microsoft Edge WebView2

HTML, CSS, JavaScript

Windows PnP and device APIs

Feedback and Bug Reports

Found a bug or have a feature suggestion?

Open an issue in this repository and include:

Windows version

Device manufacturer and model

Connection type (Bluetooth or USB receiver)

Application version

Steps to reproduce the problem

Relevant error messages or screenshots

Disclaimer

BluetoothBatteryTray is an independent project and is not affiliated with Microsoft or any peripheral manufacturer.

Windows and Microsoft Edge are trademarks of Microsoft Corporation.


BluetoothBatteryTray — Your Battery. Always in Sight.
