# PocketBridge

PocketBridge is a Windows app for sending files between PCs and phones. It is written in C# with .NET 10.

## Download

Open the repository's Releases page and download PocketBridge.exe from the latest release. The executable is published as a self-contained Windows x64 app, so a separate .NET installation is not needed.

## Use it

- **Different Wi-Fi networks:** Choose **Send anywhere…**. In the transfer page, create a pairing code, then enter it on the other device or scan the QR code. The recipient must approve each incoming file. Transfers can work between countries when both networks allow a direct WebRTC connection.
- **Nearby Wi-Fi:** Windows PCs appear in the device list. Use **Quick connect QR…** to pair a phone or tablet on the same Wi-Fi. Open PocketBridge on both Windows PCs for nearby PC transfers.
- Folders are sent as ZIP files. Received files on Windows are saved in Downloads\PocketBridge.
- The first time nearby sharing runs, Windows Firewall may ask whether PocketBridge can communicate on a private network. Allow private-network access if you want nearby devices to discover the PC.

## Direct-mode limits and privacy

Direct mode uses the free PeerJS Cloud service for temporary pairing and signaling, then sends file data peer-to-peer. File data is encrypted by WebRTC and is not sent through the website or pairing service. The two connected devices and the STUN service can see each other's public IP addresses. The pairing service receives temporary peer IDs and connection signaling details.

This mode has no TURN relay. Some routers, mobile carriers, or managed networks block direct connections, so a transfer may fail on those networks. The direct-transfer page is limited to 50 MB per file or compressed folder. Keep both devices online with the page open during a transfer.

The direct-transfer window uses Microsoft Edge WebView2. Windows 11 includes its runtime; most Windows 10 devices have it. If the runtime is missing, install it from [Microsoft's WebView2 download page](https://developer.microsoft.com/microsoft-edge/webview2/).

## Build from source

Install the .NET 10 SDK, then run:

    dotnet publish .\PocketBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .\publish

The publish command creates publish\PocketBridge.exe.

## License

No open-source license has been assigned to this project. Third-party browser-library license notices are included in the repository's vendor folder.
