# WiFi Share Testing / PocketBridge

This repository contains a C# Windows app and a browser-based direct file transfer page.

## Windows desktop app

The PocketBridge Windows app source is in [desktop/PocketBridge](desktop/PocketBridge). It has two modes:

- **Nearby Wi-Fi:** discover Windows PCs and pair phones or tablets with a QR code on the same local network.
- **Send anywhere:** open the built-in transfer page, create a pairing code or QR, and connect a phone or another browser over the internet. Devices do not need to be on the same Wi-Fi.

- [Download the latest Windows app](https://github.com/j4522419-code/wifi-share-testing/releases/latest/download/PocketBridge.exe)
- [Build instructions](desktop/PocketBridge/README.md)

## Browser transfer page

The [hosted transfer page](https://j4522419-code.github.io/wifi-share-testing/) uses the same direct-transfer mode as the Windows app. A free PeerJS Cloud service handles pairing and WebRTC signaling; files are sent directly between devices and are not uploaded to the website or signaling service. WebRTC encrypts peer-to-peer data.

Direct mode reveals each device's public IP address to the other device and to the STUN service. Some routers or networks block direct connections; this free mode has no TURN relay fallback. Direct-mode transfers are limited to 50 MB per file or compressed folder. Both devices must keep the transfer page open.

The Windows app embeds this page with Microsoft Edge WebView2. Windows 11 includes the WebView2 Runtime, and most Windows 10 devices have it. If it is missing, install it from [Microsoft's WebView2 download page](https://developer.microsoft.com/microsoft-edge/webview2/).

## Release builds

Pushing a version tag such as v1.1.0 starts a Windows build and attaches a self-contained PocketBridge.exe to a GitHub Release. A separate .NET installation is not needed to run the published build.

The project does not assign a license for its own source code. Third-party browser libraries and their license notices are in [vendor](vendor).
