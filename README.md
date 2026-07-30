# LibreGuard VPN Desktop

LibreGuard VPN Desktop is an open-source Windows client for the LibreGuard VPN service. It provides a desktop interface, a privileged VPN service, and support for IKEv2 and OpenVPN® connections.

## Features

- Windows desktop client with a companion VPN service.
- IKEv2 and OpenVPN® connection support.
- Google sign-in and account management.
- DNS filtering and kill-switch controls.
- Bundled setup flow for the dependencies used by the desktop client.

## Windows support

LibreGuard VPN Desktop supports 64-bit Windows 10 version 1809 or later and Windows 11. Installing the VPN service requires administrator approval.

## Install LibreGuard

When public builds are available, download the latest installer from [GitHub Releases](../../releases). Run the installer and follow the setup steps; it installs the desktop app and required VPN components.

LibreGuard service usage is governed by the [Terms of Service](https://libreguard.net/Terms) and [Privacy Policy](https://libreguard.net/Privacy).

## Build from source

Build on Windows with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), PowerShell, and Git for Windows.

```powershell
git clone <repository-url>
Set-Location .\libreguard-vpn-windows

dotnet restore ".\LibreGuard VPN Desktop.slnx"
dotnet build ".\LibreGuard VPN Desktop.slnx" --configuration Release
dotnet test ".\LibreGuard VPN Desktop.slnx" --configuration Release
```

## Contributing

Issues and pull requests are welcome. Please describe the problem or proposed change clearly, keep pull requests focused, and include relevant tests. For substantial changes, open an issue first so the community can discuss the approach.

## License and notices

Copyright © 2026 LibreGuard d.o.o.

LibreGuard VPN Desktop is licensed under the [GNU General Public License v2.0 or later](LICENSE). See [COPYRIGHT](COPYRIGHT) for the project license declaration and [third-party notices](licenses/THIRD-PARTY-NOTICES.txt) for release component attributions.

OpenVPN® is a registered trademark of OpenVPN Inc.
