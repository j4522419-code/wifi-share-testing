using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QRCoder;

namespace PocketBridgeDotNet;

public partial class MainWindow : Window
{
    private const int DiscoveryPort = 45831;
    private const int MaxHeader = 64 * 1024;
    private const long MaxFileSize = 2L * 1024 * 1024 * 1024;
    private const int ChunkSize = 256 * 1024;
    private const string PairAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ObservableCollection<DeviceRow> _rows = [];
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly ConcurrentDictionary<string, MobilePeer> _mobilePeers = new();
    private readonly ConcurrentDictionary<string, MobileTransfer> _mobileTransfers = new();
    private readonly ConcurrentDictionary<string, MobileUpload> _mobileUploads = new();
    private readonly ConcurrentQueue<IncomingRequest> _incoming = new();
    private readonly object _pairLock = new();
    private readonly object _fileLock = new();
    private readonly string _deviceId;
    private readonly string _deviceName = Environment.MachineName;
    private readonly string _downloadDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PocketBridge");
    private TcpListener? _tcpListener;
    private WebApplication? _webApp;
    private string? _pairCode;
    private DateTimeOffset _pairExpires;
    private string? _selectedPath;
    private bool _selectedIsFolder;
    private bool _closing;
    private int _tcpPort;
    private int _webPort;

    public MainWindow()
    {
        InitializeComponent();
        _deviceId = LoadDeviceId();
        ThisDeviceText.Text = $"This device: {_deviceName}";
        DeviceGrid.ItemsSource = _rows;
        Directory.CreateDirectory(_downloadDirectory);
        Loaded += async (_, _) => await StartNetworkAsync();
        Closing += OnClosing;
        _ = PromptLoopAsync(_shutdown.Token);
        _ = RefreshLoopAsync(_shutdown.Token);
    }

    private static string LoadDeviceId()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PocketBridge");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "device-id");
        try
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _)) return existing;
        }
        catch (IOException) { }
        var id = Guid.NewGuid().ToString("D");
        File.WriteAllText(path, id, Encoding.ASCII);
        return id;
    }

    private async Task StartNetworkAsync()
    {
        try
        {
            _tcpListener = new TcpListener(IPAddress.Any, 0);
            _tcpListener.Start(12);
            _tcpPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(_shutdown.Token);
            _ = DiscoveryLoopAsync(_shutdown.Token);
            await StartWebServerAsync();
            SetStatus("Looking for PocketBridge devices on this Wi-Fi…");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not start local sharing: {ex.Message}", true);
        }
    }

    private async Task StartWebServerAsync()
    {
        _webPort = GetUnusedPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(MainWindow).Assembly.FullName });
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(_webPort));
        var app = builder.Build();
        app.MapGet("/", () => Results.Content(MobilePage, "text/html; charset=utf-8"));
        app.MapGet("/api/poll", (Func<HttpContext, Task<IResult>>)PollPhone);
        app.MapGet("/api/download", (HttpContext context) => DownloadToPhone(context));
        app.MapPost("/api/register", (RegisterRequest request, HttpContext context) => RegisterPhone(request, context));
        app.MapPost("/api/decision", (DecisionRequest request) => DecideTransfer(request));
        app.MapPost("/api/upload-request", (UploadRequest request, HttpContext context) => RequestPhoneUpload(request, context));
        app.MapPost("/api/upload", (Func<HttpContext, Task<IResult>>)ReceivePhoneUpload);
        await app.StartAsync(_shutdown.Token);
        _webApp = app;
    }

    private static int GetUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string GetLocalAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse("192.0.2.1"), 80);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch (SocketException) { return "127.0.0.1"; }
    }

    private async Task DiscoveryLoopAsync(CancellationToken token)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.EnableBroadcast = true;
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        socket.Client.ReceiveTimeout = 500;
        var advert = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket("pocketbridge", 1, _deviceId, _deviceName, _tcpPort), JsonOptions);
        _ = AdvertiseLoopAsync(socket, advert, token);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveAsync(token);
                var packet = JsonSerializer.Deserialize<DiscoveryPacket>(received.Buffer, JsonOptions);
                if (packet is null || packet.App != "pocketbridge" || packet.Version != 1 || packet.Id == _deviceId || packet.Port is < 1 or > 65535) continue;
                _peers[packet.Id] = new Peer(packet.Id, Limit(packet.Name, 80), received.RemoteEndPoint.Address, packet.Port, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (!token.IsCancellationRequested) { }
            catch (JsonException) { }
        }
    }

    private static async Task AdvertiseLoopAsync(UdpClient socket, byte[] advert, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await socket.SendAsync(advert, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (!token.IsCancellationRequested) { }
            try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        if (_tcpListener is null) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(token);
                _ = HandleIncomingPcAsync(client, token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (!token.IsCancellationRequested) { }
        }
    }

    private async Task HandleIncomingPcAsync(TcpClient client, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            client.ReceiveTimeout = 300_000;
            client.SendTimeout = 300_000;
            await using var stream = client.GetStream();
            var offer = await ReadJsonAsync<OfferRequest>(stream, token);
            if (offer.Type != "offer") throw new InvalidDataException("Invalid transfer request.");
            var fileName = SafeFileName(offer.FileName);
            if (offer.Size < 0 || offer.Size > MaxFileSize)
            {
                await WriteJsonAsync(stream, new TransferReply(false, "This file is too large (2 GB limit)."), token);
                return;
            }
            var approval = await AskApprovalAsync(Limit(offer.Sender, 80), fileName, offer.Size, token);
            if (!approval)
            {
                await WriteJsonAsync(stream, new TransferReply(false, "The recipient declined the transfer."), token);
                return;
            }
            string target;
            lock (_fileLock)
            {
                target = AvailablePath(fileName);
                temporary = target + ".partial";
                using (File.Create(temporary)) { }
            }
            await WriteJsonAsync(stream, new TransferReply(true, ""), token);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize, true))
            {
                await CopyExactlyAsync(stream, output, offer.Size, token);
            }
            File.Move(temporary, target);
            temporary = null;
            await WriteJsonAsync(stream, new TransferReply(true, "File received."), token);
            SetStatus($"Received {Path.GetFileName(target)} from {Limit(offer.Sender, 80)}.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or JsonException or OperationCanceledException)
        {
            SetStatus($"Incoming transfer failed: {ex.Message}", true);
        }
        finally
        {
            if (temporary is not null) TryDelete(temporary);
            client.Dispose();
        }
    }

    private async Task<bool> AskApprovalAsync(string sender, string fileName, long size, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _incoming.Enqueue(new IncomingRequest(sender, fileName, size, completion));
        using var registration = token.Register(() => completion.TrySetResult(false));
        return await completion.Task;
    }

    private async Task PromptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_incoming.TryDequeue(out var request))
            {
                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var mb = request.Size / (1024d * 1024d);
                        var text = $"{request.Sender} wants to send:\n\n{request.FileName}\n{mb:N2} MB\n\nSave it to Downloads\\PocketBridge?";
                        request.Completion.TrySetResult(MessageBox.Show(this, text, "Incoming file", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
                    });
                }
                catch (InvalidOperationException) { request.Completion.TrySetResult(false); }
            }
            try { await Task.Delay(200, token); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token);
                var now = DateTimeOffset.UtcNow;
                foreach (var pair in _peers)
                    if (now - pair.Value.Seen > TimeSpan.FromSeconds(12)) _peers.TryRemove(pair.Key, out _);
                foreach (var pair in _mobilePeers)
                    if (now - pair.Value.Seen > TimeSpan.FromHours(24)) _mobilePeers.TryRemove(pair.Key, out _);
                foreach (var pair in _mobileUploads)
                    if (pair.Value.Expires < now && _mobileUploads.TryRemove(pair.Key, out var upload)) TryDelete(upload.TemporaryPath);

                await Dispatcher.InvokeAsync(() =>
                {
                    var selected = (DeviceGrid.SelectedItem as DeviceRow)?.Id;
                    var online = _peers.Values.Select(p => new DeviceRow(p.Id, p.Name, "PocketBridge PC", "Available", false))
                        .Concat(_mobilePeers.Values.Where(p => now - p.Seen <= TimeSpan.FromSeconds(18))
                            .Select(p => new DeviceRow("phone:" + p.Id, p.Name, "Web browser", "Connected", true)))
                        .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                    _rows.Clear();
                    foreach (var row in online) _rows.Add(row);
                    if (selected is not null) DeviceGrid.SelectedItem = _rows.FirstOrDefault(r => r.Id == selected);
                    if (string.IsNullOrEmpty(StatusText.Text) || StatusText.Text.StartsWith("Looking for", StringComparison.Ordinal))
                        StatusText.Text = online.Count == 0 ? "Looking for PocketBridge devices on this Wi-Fi…" : $"{online.Count} device(s) connected or available.";
                    UpdateSendButton();
                });
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a file to send", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var info = new FileInfo(dialog.FileName);
        if (info.Length > MaxFileSize) { MessageBox.Show(this, "PocketBridge supports files up to 2 GB.", "File too large", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _selectedPath = dialog.FileName;
        _selectedIsFolder = false;
        SelectionText.Text = $"{info.Name}  ·  {info.Length / (1024d * 1024d):N2} MB";
        SelectionText.Foreground = System.Windows.Media.Brushes.Black;
        UpdateSendButton();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to send", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _selectedPath = dialog.FolderName;
        _selectedIsFolder = true;
        SelectionText.Text = $"{Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar))}  ·  will be sent as a ZIP";
        SelectionText.Foreground = System.Windows.Media.Brushes.Black;
        UpdateSendButton();
    }

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSendButton();
    private void UpdateSendButton() => SendButton.IsEnabled = _selectedPath is not null && DeviceGrid.SelectedItem is DeviceRow;

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceGrid.SelectedItem is not DeviceRow device || _selectedPath is null) return;
        SendButton.IsEnabled = false;
        var path = _selectedPath;
        var isFolder = _selectedIsFolder;
        try
        {
            if (isFolder)
            {
                SetStatus($"Preparing {Path.GetFileName(path)} as a ZIP…");
                var zip = await Task.Run(() => ZipFolder(path));
                try { await SendPathAsync(device, zip); }
                finally { TryDelete(zip); }
            }
            else await SendPathAsync(device, path);
        }
        catch (Exception ex) { SetStatus($"Could not send file: {ex.Message}", true); }
        finally { UpdateSendButton(); }
    }

    private static string ZipFolder(string folderPath)
    {
        var folder = new DirectoryInfo(folderPath);
        var temp = Path.Combine(Path.GetTempPath(), "PocketBridge-" + Guid.NewGuid().ToString("N") + ".zip");
        var rootName = string.IsNullOrWhiteSpace(folder.Name) ? "Folder" : folder.Name;
        long total = 0;
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                archive.CreateEntry(rootName.TrimEnd('/') + "/");
                foreach (var file in Directory.EnumerateFiles(folder.FullName, "*", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    total += info.Length;
                    if (total > MaxFileSize) throw new InvalidDataException("The folder contents are larger than the 2 GB limit.");
                    var relative = Path.GetRelativePath(folder.FullName, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, rootName + "/" + relative, CompressionLevel.Optimal);
                }
            }
            if (new FileInfo(temp).Length > MaxFileSize) throw new InvalidDataException("The ZIP archive is larger than the 2 GB limit.");
            return temp;
        }
        catch { TryDelete(temp); throw; }
    }

    private async Task SendPathAsync(DeviceRow device, string path)
    {
        if (device.IsPhone)
        {
            if (!_mobilePeers.TryGetValue(device.Id[6..], out var phone)) throw new IOException("That device is no longer connected.");
            await SendToPhoneAsync(phone, path);
        }
        else
        {
            if (!_peers.TryGetValue(device.Id, out var peer)) throw new IOException("That device is no longer online.");
            await SendToPcAsync(peer, path);
        }
    }

    private async Task SendToPcAsync(Peer peer, string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileSize) throw new IOException("The file is larger than the 2 GB limit.");
        SetStatus($"Requesting permission from {peer.Name}…");
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(peer.Address, peer.Port, _shutdown.Token);
        await using var stream = client.GetStream();
        await WriteJsonAsync(stream, new OfferRequest("offer", _deviceName, info.Name, info.Length), _shutdown.Token);
        var reply = await ReadJsonAsync<TransferReply>(stream, _shutdown.Token);
        if (!reply.Ok) throw new IOException(string.IsNullOrWhiteSpace(reply.Reason) ? "The recipient declined the transfer." : reply.Reason);
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, true))
            await CopyExactlyAsync(input, stream, info.Length, _shutdown.Token);
        var result = await ReadJsonAsync<TransferReply>(stream, _shutdown.Token);
        if (!result.Ok) throw new IOException(result.Reason);
        SetStatus($"Sent {info.Name} to {peer.Name}.");
    }

    private async Task SendToPhoneAsync(MobilePeer peer, string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileSize) throw new IOException("The file is larger than the 2 GB limit.");
        var id = Guid.NewGuid().ToString("D");
        var transfer = new MobileTransfer(peer.Id, path, RandomToken(), new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_mobileTransfers.TryAdd(id, transfer)) throw new InvalidOperationException();
        try
        {
            peer.Offers.Enqueue(new BrowserOffer(id, info.Name, info.Length, _deviceName, transfer.Ticket));
            SetStatus($"Waiting for {peer.Name} to approve the transfer…");
            if (!await transfer.Decision.Task.WaitAsync(TimeSpan.FromMinutes(3), _shutdown.Token)) throw new IOException("The device declined the transfer.");
            SetStatus($"Sending {info.Name} to {peer.Name}…");
            await transfer.Downloaded.Task.WaitAsync(TimeSpan.FromMinutes(5), _shutdown.Token);
            SetStatus($"Sent {info.Name} to {peer.Name}.");
        }
        finally { _mobileTransfers.TryRemove(id, out _); }
    }

    private async Task<IResult> PollPhone(HttpContext context)
    {
        var id = context.Request.Query["client_id"].ToString();
        var token = context.Request.Query["token"].ToString();
        if (!TryGetMobilePeer(id, token, out var peer)) return Results.Json(new { error = "Device is not paired. Pair again from the app." }, statusCode: 401);
        peer.Seen = DateTimeOffset.UtcNow;
        peer.Offers.TryDequeue(out var offer);
        return Results.Json(new { offer });
    }

    private async Task<IResult> RegisterPhone(RegisterRequest request, HttpContext context)
    {
        var id = Limit(request.ClientId?.Trim() ?? "", 80);
        if (id.Length == 0) return Results.BadRequest(new { error = "Missing client id" });
        var name = Regex.Replace(Limit(request.Name ?? "My device", 40), "[\\x00-\\x1f]", "").Trim();
        if (name.Length == 0) name = "My device";
        MobilePeer? peer = null;
        if (_mobilePeers.TryGetValue(id, out var current) && FixedEquals(request.Token ?? "", current.Token))
        {
            peer = current;
            peer.Name = name;
            peer.Seen = DateTimeOffset.UtcNow;
        }
        else
        {
            lock (_pairLock)
            {
                if (_pairCode is null || DateTimeOffset.UtcNow > _pairExpires || !FixedEquals((request.Code ?? "").Trim().ToUpperInvariant(), _pairCode))
                    return Results.Json(new { error = "That code is invalid or expired. Enter the current code from PocketBridge." }, statusCode: 401);
                _pairCode = null;
                peer = new MobilePeer(id, name, RandomToken(), DateTimeOffset.UtcNow, context.Connection.RemoteIpAddress?.ToString() ?? "");
                _mobilePeers[id] = peer;
            }
        }
        SetStatus($"Paired and connected: {name}.");
        return Results.Json(new { ok = true, token = peer.Token });
    }

    private IResult DecideTransfer(DecisionRequest request)
    {
        if (!TryGetMobilePeer(request.ClientId ?? "", request.Token ?? "", out _) ||
            !_mobileTransfers.TryGetValue(request.TransferId ?? "", out var transfer) || transfer.ClientId != request.ClientId)
            return Results.NotFound(new { error = "Transfer is no longer available." });
        transfer.Decision.TrySetResult(request.Accept);
        return Results.Json(new { ok = true });
    }

    private async Task<IResult> RequestPhoneUpload(UploadRequest request, HttpContext context)
    {
        if (!TryGetMobilePeer(request.ClientId ?? "", request.Token ?? "", out var peer))
            return Results.Json(new { error = "Pair this device before sending files." }, statusCode: 401);
        if (request.Size < 0 || request.Size > MaxFileSize) return Results.BadRequest(new { error = "Files must be 2 GB or smaller." });
        var fileName = SafeFileName(request.FileName ?? "file");
        var approval = await AskApprovalAsync(peer.Name, fileName, request.Size, _shutdown.Token);
        if (!approval) return Results.Json(new { error = "The transfer was declined on the PC." }, statusCode: 403);
        string target;
        var id = Guid.NewGuid().ToString("D");
        var ticket = RandomToken();
        string temp;
        try
        {
            lock (_fileLock) { target = AvailablePath(fileName); temp = target + ".partial"; using (File.Create(temp)) { } }
        }
        catch (IOException ex) { return Results.Json(new { error = "Could not create the destination file: " + ex.Message }, statusCode: 500); }
        var upload = new MobileUpload(peer.Id, peer.Token, ticket, fileName, peer.Name, request.Size, target, temp, DateTimeOffset.UtcNow.AddHours(1));
        _mobileUploads[id] = upload;
        return Results.Json(new { ok = true, transfer_id = id, ticket });
    }

    private async Task<IResult> ReceivePhoneUpload(HttpContext context)
    {
        var id = context.Request.Query["transfer_id"].ToString();
        var clientId = context.Request.Query["client_id"].ToString();
        var ticket = context.Request.Query["ticket"].ToString();
        var token = context.Request.Headers["X-PocketBridge-Token"].ToString();
        if (!context.Request.ContentLength.HasValue || context.Request.ContentLength.Value < 0 || context.Request.ContentLength.Value > MaxFileSize)
            return Results.BadRequest(new { error = "Invalid upload size." });
        if (!_mobileUploads.TryGetValue(id, out var upload) || upload.ClientId != clientId || upload.Size != context.Request.ContentLength.Value ||
            !FixedEquals(ticket, upload.Ticket) || !FixedEquals(token, upload.Token) || !_mobileUploads.TryRemove(id, out _))
            return Results.NotFound(new { error = "Upload request expired or is invalid." });
        try
        {
            await using (var output = new FileStream(upload.TemporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize, true))
                await CopyExactlyAsync(context.Request.Body, output, upload.Size, context.RequestAborted);
            lock (_fileLock) File.Move(upload.TemporaryPath, upload.TargetPath);
            SetStatus($"Received {upload.FileName} from {upload.Sender}.");
            return Results.Json(new { ok = true, filename = upload.FileName }, statusCode: 201);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            TryDelete(upload.TemporaryPath);
            return Results.Json(new { error = "Could not save upload: " + ex.Message }, statusCode: 500);
        }
    }

    private IResult DownloadToPhone(HttpContext context)
    {
        var id = context.Request.Query["transfer_id"].ToString();
        var clientId = context.Request.Query["client_id"].ToString();
        var ticket = context.Request.Query["ticket"].ToString();
        if (!_mobileTransfers.TryGetValue(id, out var transfer) || transfer.ClientId != clientId || !transfer.Decision.Task.IsCompletedSuccessfully ||
            !transfer.Decision.Task.Result || !FixedEquals(ticket, transfer.Ticket)) return Results.NotFound(new { error = "Transfer unavailable." });
        var file = new FileInfo(transfer.Path);
        if (!file.Exists) return Results.NotFound(new { error = "Transfer file is unavailable." });
        return new DownloadFileResult(transfer, file.Name);
    }

    private bool TryGetMobilePeer(string id, string token, out MobilePeer peer)
    {
        if (_mobilePeers.TryGetValue(id, out var found) && FixedEquals(token, found.Token)) { peer = found; return true; }
        peer = null!;
        return false;
    }

    private void PairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webApp is null) { MessageBox.Show(this, "The browser receiver could not start.", "PocketBridge", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        var address = GetLocalAddress();
        if (address.StartsWith("127.", StringComparison.Ordinal)) { MessageBox.Show(this, "Could not find this PC's Wi-Fi address.", "PocketBridge", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        string code;
        lock (_pairLock)
        {
            _pairCode = code = RandomPairCode();
            _pairExpires = DateTimeOffset.UtcNow.AddMinutes(10);
        }
        var url = $"http://{address}:{_webPort}/?code={code}";
        var qrData = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var qrBytes = new PngByteQRCode(qrData).GetGraphic(7);
        var image = new BitmapImage();
        using (var memory = new MemoryStream(qrBytes))
        {
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = memory; image.EndInit(); image.Freeze();
        }
        var window = new Window { Title = "Quick connect a device", Owner = this, Width = 420, Height = 590, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "Pair a device", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "Scan this QR code on a device using the same Wi-Fi. The code fills in automatically; tap Pair this device.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 12), Foreground = System.Windows.Media.Brushes.DimGray });
        panel.Children.Add(new Image { Source = image, Width = 230, Height = 230, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = code, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 30, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.SteelBlue, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 8) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var copyLink = new Button { Content = "Copy link", Margin = new Thickness(4) };
        var copyCode = new Button { Content = "Copy code", Margin = new Thickness(4) };
        var state = new TextBlock { Text = "Waiting for a device to enter the code…", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 5), Foreground = System.Windows.Media.Brushes.DimGray };
        copyLink.Click += (_, _) => { Clipboard.SetText(url); state.Text = "Local link copied. Open it on the device to pair."; };
        copyCode.Click += (_, _) => { Clipboard.SetText(code); state.Text = "Pairing code copied. Enter it on the device."; };
        buttons.Children.Add(copyLink); buttons.Children.Add(copyCode); panel.Children.Add(buttons); panel.Children.Add(state);
        panel.Children.Add(new TextBlock { Text = "The code expires in 10 minutes. Keep PocketBridge open and both devices on the same Wi-Fi.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 10, 0, 0) });
        window.Content = panel;
        _ = Task.Run(async () =>
        {
            while (window.IsVisible && !_shutdown.IsCancellationRequested)
            {
                if (_pairCode is null) { await Dispatcher.InvokeAsync(() => state.Text = "A device paired successfully."); break; }
                await Task.Delay(700);
            }
        });
        window.ShowDialog();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_downloadDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _downloadDirectory) { UseShellExecute = true });
    }

    private void SetStatus(string text, bool error = false)
    {
        if (_closing || !Dispatcher.CheckAccess())
        {
            if (!_closing) _ = Dispatcher.InvokeAsync(() => SetStatus(text, error));
            return;
        }
        StatusText.Text = text;
        StatusText.Foreground = error ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.SlateGray;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _shutdown.Cancel();
        try { _tcpListener?.Stop(); } catch (SocketException) { }
        if (_webApp is not null) _ = _webApp.StopAsync();
        _webApp?.DisposeAsync();
    }

    private string AvailablePath(string filename)
    {
        var target = Path.Combine(_downloadDirectory, filename);
        if (!File.Exists(target) && !Directory.Exists(target) && !File.Exists(target + ".partial")) return target;
        var stem = Path.GetFileNameWithoutExtension(filename);
        var extension = Path.GetExtension(filename);
        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(_downloadDirectory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !File.Exists(candidate + ".partial")) return candidate;
        }
        throw new IOException("Could not choose a unique destination filename.");
    }

    private static string SafeFileName(string value)
    {
        var fileName = value.Replace('\\', '/').Split('/').LastOrDefault() ?? "received-file";
        fileName = Regex.Replace(fileName, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").TrimEnd(' ', '.');
        if (fileName.Length > 200) fileName = fileName[..200];
        var device = fileName.Split('.')[0].ToUpperInvariant();
        if (fileName.Length == 0 || device is "CON" or "PRN" or "AUX" or "NUL" || Regex.IsMatch(device, "^(COM|LPT)[1-9]$")) return "received-file";
        return fileName;
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
    private static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string RandomPairCode() => new(Enumerable.Range(0, 8).Select(_ => PairAlphabet[RandomNumberGenerator.GetInt32(PairAlphabet.Length)]).ToArray());
    private static bool FixedEquals(string first, string second)
    {
        var a = Encoding.UTF8.GetBytes(first); var b = Encoding.UTF8.GetBytes(second);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > MaxHeader) throw new InvalidDataException("Message is too large.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }

    private static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is <= 0 or > MaxHeader) throw new InvalidDataException("Invalid message size.");
        var payload = new byte[size]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions) ?? throw new InvalidDataException("Invalid message.");
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long size, CancellationToken token)
    {
        var buffer = new byte[ChunkSize];
        while (size > 0)
        {
            var count = (int)Math.Min(buffer.Length, size);
            var read = await source.ReadAsync(buffer.AsMemory(0, count), token);
            if (read == 0) throw new EndOfStreamException("Transfer ended before the whole file arrived.");
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            size -= read;
        }
    }

    private sealed record DeviceRow(string Id, string Name, string Type, string Status, bool IsPhone);
    private sealed record Peer(string Id, string Name, IPAddress Address, int Port, DateTimeOffset Seen);
    private sealed record DiscoveryPacket(string App, int Version, string Id, string Name, int Port);
    private sealed record OfferRequest(string Type, string Sender, string FileName, long Size);
    private sealed record TransferReply(bool Ok, string Reason);
    private sealed record IncomingRequest(string Sender, string FileName, long Size, TaskCompletionSource<bool> Completion);
    private sealed record RegisterRequest(string? ClientId, string? Name, string? Code, string? Token);
    private sealed record DecisionRequest(string? ClientId, string? Token, string? TransferId, bool Accept);
    private sealed record UploadRequest(string? ClientId, string? Token, string? FileName, long Size);
    private sealed record BrowserOffer(string TransferId, string FileName, long Size, string Sender, string Ticket);
    private sealed class MobilePeer(string id, string name, string token, DateTimeOffset seen, string address)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public string Token { get; } = token;
        public DateTimeOffset Seen { get; set; } = seen;
        public string Address { get; } = address;
        public ConcurrentQueue<BrowserOffer> Offers { get; } = new();
    }
    private sealed record MobileUpload(string ClientId, string Token, string Ticket, string FileName, string Sender, long Size, string TargetPath, string TemporaryPath, DateTimeOffset Expires);
    private sealed record MobileTransfer(string ClientId, string Path, string Ticket, TaskCompletionSource<bool> Decision, TaskCompletionSource Downloaded);

    private sealed class DownloadFileResult(MobileTransfer transfer, string fileName) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(fileName);
            context.Response.Headers.CacheControl = "no-store";
            await using var source = new FileStream(transfer.Path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, true);
            context.Response.ContentLength = source.Length;
            try
            {
                await source.CopyToAsync(context.Response.Body, ChunkSize, context.RequestAborted);
                transfer.Downloaded.TrySetResult();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                transfer.Downloaded.TrySetException(ex);
                throw;
            }
        }
    }

    private const string MobilePage = """
<!doctype html><html lang="en"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="theme-color" content="#10192b"><title>PocketBridge</title>
<style>*{box-sizing:border-box}body{margin:0;background:#10192b;color:#f3f6fc;font:16px system-ui,sans-serif}main{max-width:520px;margin:8vh auto;padding:28px}section{background:#1a2740;border:1px solid #2a3956;border-radius:18px;padding:24px}h1{margin:0 0 8px;font-size:27px}p{color:#bdc9dc;line-height:1.5}.hint{font-size:14px}input,button{width:100%;border-radius:10px;padding:14px;font:inherit}input{margin:10px 0;background:#0f192b;color:white;border:1px solid #52617c}input[type=file]{padding:10px}input.code{letter-spacing:5px;text-transform:uppercase;text-align:center;font-weight:700}button{border:0;background:#75a8ff;color:#08162e;font-weight:700}button:disabled{opacity:.55}#state,#uploadState{min-height:24px;color:#9fc3ff;margin-top:16px}h2{font-size:20px;margin:22px 0 4px}hr{border:0;border-top:1px solid #34445f;margin:24px 0}</style>
<main><section><h1>PocketBridge</h1><p>Connect this device to PocketBridge on the same Wi-Fi to send files to the PC or receive files from it.</p><label for="code">Pairing code from the PC app</label><input class="code" id="code" maxlength="8" autocomplete="one-time-code" placeholder="8 LETTERS / NUMBERS"><label for="name">Name shown on the PC</label><input id="name" maxlength="40" autocomplete="nickname" placeholder="My device"><button id="connect">Pair this device</button><p id="state" role="status">Enter the code shown in the PocketBridge app.</p><p class="hint">Keep this page open and the device awake to receive a file. The browser may ask where to save it.</p><hr><h2>Send files to this PC</h2><p class="hint">Choose files here. PocketBridge on the PC will ask before saving them.</p><label for="files">Files to send</label><input id="files" type="file" multiple><button id="sendFiles" disabled>Send to PC</button><p id="uploadState" role="status"></p></section></main>
<script>const $=id=>document.getElementById(id),key='pocketbridge-client-id';let clientId=localStorage.getItem(key);if(!clientId){clientId=(crypto.randomUUID?crypto.randomUUID():String(Math.random()).slice(2)+Date.now());localStorage.setItem(key,clientId)}let token=sessionStorage.getItem('pocketbridge-token')||'',active=false;const qrCode=new URLSearchParams(location.search).get('code');if(qrCode){$('code').value=qrCode.toUpperCase();$('state').textContent='Pairing code filled from QR. Choose a name, then tap Pair this device.'}async function post(path,data){const r=await fetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data),cache:'no-store'});let d={};try{d=await r.json()}catch{}if(!r.ok)throw new Error(d.error||'Connection failed');return d}async function poll(){if(!active)return;try{const r=await fetch('/api/poll?client_id='+encodeURIComponent(clientId)+'&token='+encodeURIComponent(token),{cache:'no-store'});if(!r.ok){let e={};try{e=await r.json()}catch{}throw new Error(e.error||'PC connection lost')}const data=await r.json();if(data.offer){const o=data.offer,yes=window.confirm(o.sender+' wants to send '+o.fileName+' ('+(o.size/1048576).toFixed(2)+' MB). Accept?');await post('/api/decision',{clientId,token,transferId:o.transferId,accept:yes});if(yes){const q=new URLSearchParams({client_id:clientId,transfer_id:o.transferId,ticket:o.ticket}),a=document.createElement('a');a.href='/api/download?'+q;a.download=o.fileName;a.style.display='none';document.body.appendChild(a);a.click();a.remove();$('state').textContent='Download started: '+o.fileName+'.'}else $('state').textContent='Transfer declined.'}}catch(e){$('state').textContent=e.message+' — enter a new code from the PC app';active=false;token='';sessionStorage.removeItem('pocketbridge-token');$('code').disabled=false;$('connect').disabled=false;$('connect').textContent='Pair again';$('sendFiles').disabled=true;return}setTimeout(poll,700)}$('name').value=localStorage.getItem('pocketbridge-name')||'My device';$('connect').addEventListener('click',async()=>{const name=$('name').value.trim()||'My device';$('state').textContent='Connecting…';$('connect').disabled=true;try{const d=await post('/api/register',{clientId,name,code:$('code').value.trim(),token});token=d.token;sessionStorage.setItem('pocketbridge-token',token);localStorage.setItem('pocketbridge-name',name);$('code').disabled=true;active=true;$('connect').textContent='Connected';$('state').textContent='Paired. Keep this page open to send and receive files.';$('sendFiles').disabled=false;poll()}catch(e){$('state').textContent=e.message;$('code').disabled=false;token='';sessionStorage.removeItem('pocketbridge-token');$('connect').disabled=false;$('connect').textContent='Pair again';$('sendFiles').disabled=true}});$('sendFiles').addEventListener('click',async()=>{const files=Array.from($('files').files||[]);if(!active||!token){$('uploadState').textContent='Pair this device first.';return}if(!files.length){$('uploadState').textContent='Choose one or more files first.';return}$('sendFiles').disabled=true;for(const file of files){try{$('uploadState').textContent='Waiting for PC approval: '+file.name;const d=await post('/api/upload-request',{clientId,token,fileName:file.name,size:file.size});$('uploadState').textContent='Approved. Sending '+file.name+'…';const q=new URLSearchParams({client_id:clientId,transfer_id:d.transfer_id,ticket:d.ticket}),r=await fetch('/api/upload?'+q,{method:'POST',headers:{'Content-Type':'application/octet-stream','X-PocketBridge-Token':token},body:file,cache:'no-store'});let result={};try{result=await r.json()}catch{}if(!r.ok)throw new Error(result.error||'Upload failed');$('uploadState').textContent='Sent '+file.name+' to the PC.'}catch(e){$('uploadState').textContent=e.message;break}}$('sendFiles').disabled=!active});if(token){$('code').disabled=true;$('connect').textContent='Reconnect'}</script></html>
""";
}
