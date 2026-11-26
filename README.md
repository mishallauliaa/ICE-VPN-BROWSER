# ICE BROWSER — Windows x64 (Release Candidate 1.4.0)

ICE BROWSER is a Windows desktop browser built with C# WPF and Microsoft Edge WebView2. VPN controls integrate with the official Cloudflare WARP client installed on the same Windows device.

## Included features
- Chromium-based browsing through WebView2
- Multiple tabs, context-menu close, keyboard shortcuts
- Address bar with Google search fallback
- Bookmarks saved in the local user profile
- Persistent normal browsing profile
- Temporary private tabs, removed when ICE BROWSER closes
- Basic domain-based ad/tracker blocking
- EasyList-derived domain filter download/update
- Download capture and progress status, saved in the Windows Downloads folder
- Last-session restore (up to 10 HTTP/HTTPS tabs)
- Settings for WARP auto-connect, AdBlock, and session restore
- Clear browsing data for the normal WebView2 profile
- Navigation-load duration indicator

## VPN behavior and limitations
ICE BROWSER does not operate its own VPN network. Its VPN button invokes `warp-cli` from the official Cloudflare WARP application. Install WARP, complete its setup/registration, and confirm it works independently before using the ICE BROWSER VPN button.

Cloudflare WARP Free:
- Is free subject to Cloudflare's terms and availability.
- Does not provide arbitrary country/server selection like a commercial VPN.
- Works at the Windows device/network level, not only inside ICE BROWSER. Other applications may also use the WARP tunnel.
- May require administrator approval or a compatible WARP CLI version. ICE BROWSER does not bypass WARP permissions.
- Is not an anonymity guarantee and does not make users invisible to websites or network operators.

Official WARP download: https://one.one.one.one/

## Browser privacy and filtering notes
- Private tabs use a separate temporary WebView2 profile and attempt to delete it when ICE BROWSER exits. This does not erase downloads or activity stored by websites, DNS, ISP, employer, or network equipment.
- The ad blocker is a basic host-domain filter. It is not a full EasyList-compatible engine; cosmetic rules, URL-path rules, and some first-party ads are not blocked.
- Clearing browsing data clears WebView2 profile data for the normal profile; it does not remove files downloaded to Downloads.
- Load time is navigation duration, not internet throughput in Mbps.

## Build requirements
- Windows 10/11 x64
- .NET 8 SDK
- Inno Setup 6
- Internet access for NuGet restore and WebView2 bootstrapper download

The build script downloads Microsoft's official WebView2 Evergreen Bootstrapper and includes it in the installer. The installer runs it silently before launching ICE BROWSER. Cloudflare WARP is not bundled; users install it separately from Cloudflare.

## Build the installer on Windows
1. Extract this project folder.
2. Install .NET 8 SDK and Inno Setup 6.
3. Open PowerShell in the extracted project directory.
4. Run:

   `powershell -ExecutionPolicy Bypass -File .\Scripts\build-windows.ps1`

5. The self-contained app output is created in `publish\`.
6. The installer is created in `dist\ICE-BROWSER-Setup-1.4.0-x64.exe`.

The script stops on build or installer errors. If Inno Setup is absent, it reports the missing prerequisite instead of claiming the installer was created.

## Release validation status
The project files and XAML structure were checked in the authoring environment, and XAML event-handler names were checked against the source. This environment does not have Windows, the .NET SDK, or Inno Setup, so a Windows compilation, installer execution, VPN connection test, and runtime/browser QA have **not** been performed here. Treat this package as a release candidate until it is built and tested on a Windows 10/11 x64 machine.

## Suggested Windows QA before distribution
- Install on a clean Windows 10/11 x64 VM.
- Confirm WebView2 Runtime installation and first launch.
- Test navigation, HTTPS sites, tabs, keyboard shortcuts, bookmarks, and session restore.
- Test private profile cleanup after normal exit and forced termination.
- Test download completion, cancellation, and duplicate filenames.
- Test AdBlock on common sites and verify false positives.
- Install Cloudflare WARP separately; test connect, disconnect, reboot, and permission failures.
- Test with WARP unavailable to ensure normal browsing still works.
- Test CPU/RAM with 1, 5, and 10 tabs.
- Verify installer upgrade/uninstall behavior and Windows Defender/SmartScreen reputation.


## Build using GitHub Actions (optional)
1. Create a private or public GitHub repository and upload this project.
2. Open the repository's **Actions** tab.
3. Select **Build ICE BROWSER Windows Installer** and choose **Run workflow**, or push a version tag such as `v1.4.0`.
4. After the workflow succeeds, open its run and download the `ICE-BROWSER-Setup-1.4.0-x64` artifact.

The workflow uses a Windows runner, installs .NET 8 and Inno Setup 6, then runs the same PowerShell build script. A successful workflow is required before distributing the installer.
