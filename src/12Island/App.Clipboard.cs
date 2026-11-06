using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

// Reads what is copied while the app runs and keeps it in the clipboard history. One watcher for every panel: the store and the
// window that listens are the app's, so a panel opened later still shows every entry.
public partial class App
{
    internal ClipStore? Clips { get; private set; }
    private ClipboardWatcher? _clipWatch;
    private string _clipFolder = "";
    private bool _clipBusy;

    // The last text the panel itself put on the clipboard: copying an entry back must not add it again.
    private string? _clipOwnWrite;

    internal void StartClipboard()
    {
        if (!Settings.ClipboardEnabled)
        {
            _clipWatch?.Dispose(); _clipWatch = null;
            return;
        }
        if (Clips is null)
        {
            _clipFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "12Island");
            Clips = new ClipStore(_clipFolder, Settings.ClipboardLimit);
            Clips.Changed += ClipboardChanged;
        }
        else Clips.SetLimit(Settings.ClipboardLimit);
        if (_clipWatch is not null) return;
        try
        {
            _clipWatch = new ClipboardWatcher();
            _clipWatch.Changed += CaptureClipboard;
        }
        catch (InvalidOperationException) { _clipWatch = null; }
    }

    private void ClipboardChanged() => ClipboardView?.Invoke();

    // Raised after the history changed, so the pages draw it again.
    internal event Action? ClipboardView;

    // Puts an entry back on the clipboard as what it was: text, a picture, or the files it named.
    internal void CopyToClipboard(ClipEntry entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case ClipKind.Image when Clips?.PicturePath(entry) is { } path && File.Exists(path):
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit();
                    Clipboard.SetImage(image); break;
                case ClipKind.Files:
                    var files = new System.Collections.Specialized.StringCollection();
                    foreach (var line in entry.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) files.Add(line.Trim());
                    Clipboard.SetFileDropList(files); break;
                default:
                    _clipOwnWrite = entry.Text;
                    Clipboard.SetText(entry.Text); break;
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or IOException) { }
    }

    private void CaptureClipboard()
    {
        if (Clips is null || _clipBusy) return;
        // Another program may hold the clipboard for a moment while it writes; one more try shortly after is enough.
        if (!TryCapture(0)) RetryCapture();
    }

    private void RetryCapture()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        timer.Tick += (_, _) => { timer.Stop(); TryCapture(1); };
        timer.Start();
    }

    private bool TryCapture(int attempt)
    {
        if (Clips is null) return true;
        _clipBusy = true;
        try
        {
            var entry = ReadClipboard();
            if (entry is not null) Clips.Put(entry);
            return true;
        }
        catch (COMException) { return attempt > 0; }
        catch (Exception ex) when (ex is ExternalException or IOException or InvalidOperationException) { return true; }
        finally { _clipBusy = false; }
    }

    // What the clipboard holds as an entry, or null when it holds nothing this history keeps.
    private ClipEntry? ReadClipboard()
    {
        if (Clips is null) return null;
        if (Clipboard.ContainsImage() && Settings.ClipboardImages)
        {
            var image = Clipboard.GetImage();
            if (image is not null && image.PixelWidth > 0 && image.PixelHeight > 0 && image.PixelWidth <= 8192 && image.PixelHeight <= 8192)
            {
                string name = Guid.NewGuid().ToString("N") + ".png";
                var encoder = new PngBitmapEncoder { Frames = { BitmapFrame.Create(image) } };
                byte[] bytes;
                using (var memory = new MemoryStream())
                {
                    encoder.Save(memory); bytes = memory.ToArray();
                }
                using (var file = File.Create(Path.Combine(Clips.Pictures, name))) file.Write(bytes, 0, bytes.Length);
                return new ClipEntry
                {
                    Kind = ClipKind.Image, Image = name, Width = image.PixelWidth, Height = image.PixelHeight,
                    Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 12), Source = ForegroundApp()
                };
            }
        }
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files is { Count: > 0 })
                return new ClipEntry { Kind = ClipKind.Files, Text = string.Join("\n", files.Cast<string>()), Source = ForegroundApp() };
        }
        if (Clipboard.ContainsText())
        {
            string text = Clipboard.GetText();
            if (text.Length == 0) return null;
            // What this app itself just copied back is already in the history.
            if (text == _clipOwnWrite) { _clipOwnWrite = null; return null; }
            return new ClipEntry { Kind = ClipKind.Text, Text = text, Source = ForegroundApp() };
        }
        return null;
    }

    // The program in front when the copy was made, which tells apart copies that look the same.
    private static string ForegroundApp()
    {
        try
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return "";
            GetWindowThreadProcessId(window, out int pid);
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return ""; }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr window, out int pid);
}
