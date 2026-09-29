using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace PocketBridgeDotNet;

public partial class CrossNetworkWindow : Window
{
    private const string TransferPage = "https://j4522419-code.github.io/wifi-share-testing/?desktop=1";
    private readonly string _downloadDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "PocketBridge");

    public CrossNetworkWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_downloadDirectory);
            await TransferBrowser.EnsureCoreWebView2Async();
            TransferBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            TransferBrowser.CoreWebView2.DownloadStarting += OnDownloadStarting;
            TransferBrowser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }
            };
            TransferBrowser.Source = new Uri(TransferPage);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "PocketBridge could not open direct-transfer mode.\n\nMake sure this device has an internet connection and the Microsoft Edge WebView2 Runtime is installed. If needed, install it from https://developer.microsoft.com/microsoft-edge/webview2/.\n\n" + ex.Message,
                "Could not start direct transfer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        var fileName = SafeFileName(Path.GetFileName(args.ResultFilePath));
        args.ResultFilePath = GetAvailablePath(fileName);
        args.Handled = true;
    }

    private string GetAvailablePath(string fileName)
    {
        var target = Path.Combine(_downloadDirectory, fileName);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(_downloadDirectory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        throw new IOException("Could not choose a unique destination filename.");
    }

    private static string SafeFileName(string value)
    {
        var fileName = Regex.Replace(value, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").TrimEnd(' ', '.');
        return string.IsNullOrWhiteSpace(fileName) ? "received-file" : fileName[..Math.Min(fileName.Length, 200)];
    }
}
