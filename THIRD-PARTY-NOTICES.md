# Third-party software

Companion Deck uses the following software. Each keeps its own license, which applies to it and not to
Companion Deck. Their licenses and source are at the addresses given.

| Component | Used for | License | Source |
|---|---|---|---|
| .NET 8 runtime and libraries (Microsoft) | Runs the Windows program (included in the installer) | MIT | https://github.com/dotnet/runtime |
| NAudio (Mark Heath) | Audio devices, mixing, recording | MIT | https://github.com/naudio/NAudio |
| TraceEvent (Microsoft) | Reading frame rates from Windows event tracing | MIT | https://github.com/microsoft/perfview |
| LibreHardwareMonitorLib | CPU temperature and fan sensors | MPL-2.0 (used unmodified) | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| System.Drawing.Common, System.Management (Microsoft) | Program icons, hardware queries | MIT | https://github.com/dotnet/runtime |

## Installed separately, never bundled

- **Android platform-tools (ADB), Google.** The installer downloads it from Google's own servers and checks that it is
  signed by Google. It is licensed by Google under the Android SDK terms and the Apache License 2.0 for its
  open-source parts. https://developer.android.com/tools/releases/platform-tools
- **PawnIO driver.** Optional. If you choose it, it is installed with `winget`, not by Companion Deck. It lets the
  sensor library read CPU temperature. https://pawnio.eu/

## Mozilla Public License notice (LibreHardwareMonitorLib)

The LibreHardwareMonitorLib files are distributed unmodified, in the form Microsoft's NuGet service provides them.
Their source code is available at the address above under the Mozilla Public License 2.0, a copy of which is at
https://www.mozilla.org/MPL/2.0/.
