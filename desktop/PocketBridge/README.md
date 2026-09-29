# PocketBridge

PocketBridge is a small Windows app for sending files between a PC and nearby devices on the same Wi-Fi network. It is written in C# with .NET 10.

## Download

Open the repository's **Releases** page and download `PocketBridge.exe` from the latest release. The executable is published as a self-contained Windows x64 app, so a separate .NET installation is not needed.

The first time it runs, Windows Firewall may ask whether PocketBridge can communicate on a private network. Allow private-network access for devices on your Wi-Fi to find and connect to the PC.

## Use it

- **PC to PC:** Open PocketBridge on both Windows PCs. Select the other PC, choose a file or folder, and send. The receiving PC asks before saving.
- **Phone or tablet:** On the PC, choose **Quick connect QR…** and scan the code with the other device's camera. Both devices must use the same Wi-Fi. The page can send files to the PC and receive files from it while it stays open.
- Folders are sent as ZIP files. Received files are saved in `Downloads\PocketBridge`.

The app only advertises and serves transfers on the local network. Pairing codes expire after 10 minutes and are accepted once. Incoming transfers require approval on the receiving device. Transfers use the local network without encryption, so use PocketBridge on a Wi-Fi network you trust.

## Build from source

Install the .NET 10 SDK, then run:

```powershell
dotnet publish .\PocketBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .\publish
```

The publish command creates `publish\PocketBridge.exe`.

## License

No open-source license has been assigned to this project. You may download and run the published application; source-code reuse and redistribution are not granted by this repository.
