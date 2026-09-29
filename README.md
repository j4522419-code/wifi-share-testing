# WiFi Share Testing

This repository contains two separate ways to share files:

## Windows desktop app (C#)

The full PocketBridge Windows app source is in [`desktop/PocketBridge`](desktop/PocketBridge). It can send files and folders between a Windows PC and nearby devices on the same Wi-Fi network.

- [Download the latest Windows app](https://github.com/j4522419-code/wifi-share-testing/releases/latest/download/PocketBridge.exe)
- [Browse the C# source](desktop/PocketBridge)
- [Build instructions](desktop/PocketBridge/README.md)

The desktop app is local-network software. It does not use the website for transfers and it does not send files between countries.

## Browser experiment

The [hosted browser prototype](https://j4522419-code.github.io/wifi-share-testing/) tries direct browser-to-browser transfers using WebRTC. It is independent of the Windows app: people exchange connection codes manually, direct mode can reveal public IP addresses, and some networks block direct connections. It has no relay fallback.

## Releasing the Windows app

Pushing a version tag such as `v1.0.0` starts a Windows build and attaches a self-contained `PocketBridge.exe` to a GitHub Release. A separate .NET installation is not needed to run that published build.

This project does not assign an open-source license.
