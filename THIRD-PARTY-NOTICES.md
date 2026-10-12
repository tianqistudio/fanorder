# Third-party notices

FanOrder is independently implemented. This source repository does not include Myth.Cool, GamePP USB Center, HWiNFO, GPK, vendor native add-ons, firmware, or driver installers.

Dependency versions are pinned in `Directory.Packages.props` and the individual `packages.lock.json` files. NuGet restores dependencies separately.

## Application dependencies

| Dependency | License / notice |
| --- | --- |
| LibreHardwareMonitorLib 0.9.6 | MPL-2.0 |
| BlackSharp.Core 1.0.7 | MPL-2.0 |
| DiskInfoToolkit 1.1.2 | MPL-2.0 |
| RAMSPDToolkit-NDD 1.4.2 | MPL-2.0 |
| HidSharp 2.6.4 | Apache-2.0; copyright 2010–2025 James F. Bellinger |
| Microsoft .NET libraries and runtime | MIT and accompanying third-party notices |

License texts are retained under `docs/licenses/`. LibreHardwareMonitor sources are available at https://github.com/LibreHardwareMonitor/LibreHardwareMonitor and HidSharp at https://software.seekye.com/hidsharp.

`Mono.Posix.NETStandard` remains in the restored dependency graph, but its runtime and native assets are explicitly excluded from this Windows payload. Binary packaging must verify that exclusion against the actual published files.

## Drivers and installer tooling

PawnIO is a separate prerequisite on hosts whose temperature monitoring requires it. Its installer is not redistributed here. Refer to https://pawnio.eu/ for its distributions and terms.

The retained WiX 7.0.0 projects use the WiX SDK, Bal and Util packages. WiX source licensing and package terms are distinct; copies of MS-RL and the WiX OSMF EULA are retained under `docs/licenses/`. Archiving those terms is not acceptance. WiX builds require the explicit acceptance gate in `installer/build.ps1`.

NSIS tooling is not included in this repository. A binary release must audit its actual dependency closure, preserve applicable notices and satisfy the corresponding redistribution terms.

## Tests

Test dependencies include Microsoft.NET.Test.Sdk, xUnit.net and its Visual Studio runner. Exact versions are recorded in the test lock files. Test packages must not be included in application installers.
