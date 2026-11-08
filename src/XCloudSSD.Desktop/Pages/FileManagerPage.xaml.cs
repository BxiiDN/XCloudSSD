using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Desktop.Infrastructure;

namespace XCloudSSD.Desktop.Pages;

/// <summary>A file or folder row. All operations go through the mounted drive letter (the real filesystem).</summary>
public sealed class FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string Location { get; init; } = "";
    public string SizeText => IsDir ? "" : Fmt.Bytes(Size);
    public string TypeText => IsDir ? L.T("Folder") : (Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } e ? e : L.T("File"));
    public string ModifiedText => Modified == DateTime.MinValue ? "" : Modified.ToString("g", CultureInfo.CurrentCulture);
}

public partial class FileManagerPage : UserControl, IPage
{
    private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };
    private static readonly string[] TextExt = { ".txt", ".log", ".json", ".xml", ".ini", ".cfg", ".md", ".csv", ".acf", ".vdf", ".bat", ".cmd", ".ps1", ".yaml", ".yml" };

    private List<FileEntry> _all = new();
    private string _current = "";
    private string _sortKey = "Name";
    private bool _sortAsc = true;
    private List<string> _clip = new();
    private bool _clipCut;
    private string _drivesSig = "";
    private CancellationTokenSource? _previewCts;

    public FileManagerPage() => InitializeComponent();

    private Window Owner => Window.GetWindow(this)!;

    public void OnShown()
    {
        if (_current.Length > 0) _ = LoadAsync(_current);
    }

    public void OnStatus(ServiceStatusDto? s)
    {
        var drives = s?.Accounts.Where(a => a.Mounted).Select(a => new DiskView(a)).ToList() ?? new();
        var sig = string.Join(";", drives.Select(d => d.Id + d.Dto.DriveLetter + d.Dto.Label));
        if (sig == _drivesSig) return;
        _drivesSig = sig;
        var selectedLetter = (DriveBox.SelectedItem as DiskView)?.Dto.DriveLetter;
        DriveBox.ItemsSource = drives;
        DriveBox.SelectedItem = drives.FirstOrDefault(d => d.Dto.DriveLetter == selectedLetter) ?? drives.FirstOrDefault();
        if (drives.Count == 0)
        {
            Files.ItemsSource = null;
            StatusText.Text = L.T("NoMountedDrives");
        }
    }

    private void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveBox.SelectedItem is DiskView d && !_current.StartsWith($"{d.Dto.DriveLetter}:", StringComparison.OrdinalIgnoreCase))
            _ = LoadAsync($"{d.Dto.DriveLetter}:\\");
    }

    // ------------------------------------------------------------------ listing

    private async Task LoadAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        SetBusy(true, L.F("Loading", path));
        try
        {
            var entries = await Task.Run(() =>
            {
                var di = new DirectoryInfo(path);
                return di.EnumerateFileSystemInfos().Select(i => new FileEntry
                {
                    Name = i.Name,
                    FullPath = i.FullName,
                    IsDir = (i.Attributes & FileAttributes.Directory) != 0,
                    Size = i is System.IO.FileInfo f ? f.Length : 0,
                    Modified = i.LastWriteTime,
                }).ToList();
            });
            _current = path.EndsWith('\\') ? path : path + "\\";
            PathBox.Text = _current;
            _all = entries;
            SearchBox.Text = "";
            Render();
            StatusText.Text = L.F("ItemsCount", entries.Count(e => e.IsDir), entries.Count(e => !e.IsDir), Fmt.Bytes(entries.Sum(e => e.Size)));
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            PathBox.Text = _current;
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private void Render()
    {
        IEnumerable<FileEntry> q = _all;
        var filter = SearchBox.Text.Trim();
        if (filter.Length > 0) q = q.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        Func<FileEntry, object> key = _sortKey switch
        {
            "Size" => e => e.Size,
            "Type" => e => e.TypeText,
            "Modified" => e => e.Modified,
            "Location" => e => e.Location,
            _ => e => e.Name,
        };
        var dirsFirst = q.OrderBy(e => e.IsDir ? 0 : 1);
        Files.ItemsSource = (_sortAsc ? dirsFirst.ThenBy(key) : dirsFirst.ThenByDescending(key)).ToList();
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string tag }) return;
        if (_sortKey == tag) _sortAsc = !_sortAsc;
        else
        {
            _sortKey = tag;
            _sortAsc = true;
        }
        Render();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Render();

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = LoadAsync(PathBox.Text.Trim());
    }

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private void GoUp()
    {
        var parent = Directory.GetParent(_current.TrimEnd('\\'))?.FullName;
        if (parent is not null) _ = LoadAsync(parent);
        else if (_current.Length > 0) _ = LoadAsync(_current[..3]);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(_current);

    private async void CloudSearch_Click(object sender, RoutedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        if (text.Length == 0 || DriveBox.SelectedItem is not DiskView d) return;
        SetBusy(true, L.F("Searching", text));
        try
        {
            var hits = await ServiceHub.Client.CallAsync<List<SearchResultDto>>(Commands.Search,
                new SearchArgs { AccountId = d.Id, Text = text, Max = 300 }, 60_000) ?? new();
            Files.ItemsSource = hits.Select(h => new FileEntry
            {
                Name = h.Name,
                FullPath = h.Path,
                IsDir = h.IsFolder,
                Size = h.Size,
                Modified = h.Modified.LocalDateTime,
                Location = Path.GetDirectoryName(h.Path) ?? "",
            }).ToList();
            StatusText.Text = L.F("SearchResults", hits.Count, text);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------ open / preview

    private List<FileEntry> Selected => Files.SelectedItems.Cast<FileEntry>().ToList();

    private void Files_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Selected.FirstOrDefault() is not { } entry) return;
        if (entry.IsDir)
        {
            _ = LoadAsync(entry.FullPath);
            return;
        }
        if (entry.Location.Length > 0 && !string.Equals(entry.Location.TrimEnd('\\') + "\\", _current, StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadAsync(entry.Location);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
    }

    private async void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _previewCts?.Cancel();
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Collapsed;
        var sel = Selected;
        if (sel.Count != 1)
        {
            PreviewInfo.Text = sel.Count > 1 ? L.F("SelectedCount", sel.Count, Fmt.Bytes(sel.Sum(s => s.Size))) : "";
            return;
        }
        var f = sel[0];
        PreviewInfo.Text = f.IsDir ? $"{f.Name}\n{L.T("Folder")}" : $"{f.Name}\n{f.SizeText} · {f.ModifiedText}";
        if (f.IsDir) return;
        var ext = Path.GetExtension(f.Name).ToLowerInvariant();
        var cts = _previewCts = new CancellationTokenSource();
        try
        {
            if (ImageExt.Contains(ext) && f.Size <= 40L << 20)
            {
                // Read from the virtual disk (streamed from the cloud into RAM only).
                var bytes = await Task.Run(() => File.ReadAllBytes(f.FullPath), cts.Token);
                if (cts.IsCancellationRequested) return;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 560;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                PreviewImage.Source = bmp;
                PreviewImage.Visibility = Visibility.Visible;
            }
            else if (TextExt.Contains(ext))
            {
                var text = await Task.Run(() =>
                {
                    using var fs = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var buf = new byte[64 * 1024];
                    var n = fs.Read(buf, 0, buf.Length);
                    return System.Text.Encoding.UTF8.GetString(buf, 0, n);
                }, cts.Token);
                if (cts.IsCancellationRequested) return;
                PreviewText.Text = text;
                PreviewText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PreviewInfo.Text += "\n" + ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ------------------------------------------------------------------ commands

    private void Files_KeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Delete: Delete_Click(sender, e); break;
            case Key.F2: Rename_Click(sender, e); break;
            case Key.Enter: OpenSelected(); break;
            case Key.Back: GoUp(); break;
            case Key.F5: _ = LoadAsync(_current); break;
            case Key.C when ctrl: Copy_Click(sender, e); break;
            case Key.X when ctrl: Cut_Click(sender, e); break;
            case Key.V when ctrl: Paste_Click(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current.Length == 0) return;
        var name = Dialogs.Input(Owner, L.T("NewFolder"), L.T("NamePrompt"), L.T("NewFolderDefault"));
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunOp(L.T("NewFolder"), _ => Directory.CreateDirectory(Path.Combine(_current, name)));
    }

    private async void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (_current.Length == 0) return;
        var name = Dialogs.Input(Owner, L.T("NewFile"), L.T("NamePrompt"), L.T("NewFileDefault"));
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunOp(L.T("NewFile"), _ =>
        {
            var p = Path.Combine(_current, name);
            if (File.Exists(p) || Directory.Exists(p)) throw new IOException(L.F("AlreadyExists", name));
            using (File.Create(p)) { }
        });
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_current.Length == 0) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.T("Upload") };
        if (dlg.ShowDialog(Owner) != true) return;
        var target = _current;
        await RunOp(L.T("Upload"), p => CopyInto(dlg.FileNames, target, move: false, p));
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("DownloadTo") };
        if (dlg.ShowDialog(Owner) != true) return;
        await RunOp(L.T("Download"), p => CopyInto(sel.Select(s => s.FullPath), dlg.FolderName, move: false, p));
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => SetClip(false);
    private void Cut_Click(object sender, RoutedEventArgs e) => SetClip(true);

    private void SetClip(bool cut)
    {
        _clip = Selected.Select(s => s.FullPath).ToList();
        _clipCut = cut;
        if (_clip.Count == 0) return;
        var list = new StringCollection();
        list.AddRange(_clip.ToArray());
        try
        {
            Clipboard.SetFileDropList(list);
        }
        catch (Exception)
        {
            // Clipboard busy; the internal clipboard still works.
        }
        StatusText.Text = L.F(cut ? "CutCount" : "CopiedCount", _clip.Count);
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (_current.Length == 0) return;
        var sources = _clip;
        var move = _clipCut;
        if (sources.Count == 0)
        {
            try
            {
                if (Clipboard.ContainsFileDropList()) sources = Clipboard.GetFileDropList().Cast<string>().ToList();
            }
            catch (Exception)
            {
            }
            move = false;
        }
        if (sources.Count == 0) return;
        var target = _current;
        await RunOp(L.T("Paste"), p => CopyInto(sources, target, move, p));
        if (move) _clip = new();
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is not { } f) return;
        var name = Dialogs.Input(Owner, L.T("Rename"), L.T("NamePrompt"), f.Name);
        if (string.IsNullOrWhiteSpace(name) || name == f.Name) return;
        var dest = Path.Combine(Path.GetDirectoryName(f.FullPath)!, name);
        await RunOp(L.T("Rename"), _ =>
        {
            if (f.IsDir) Directory.Move(f.FullPath, dest);
            else File.Move(f.FullPath, dest);
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        if (!Dialogs.Confirm(Owner, L.T("Delete"), L.F("DeleteConfirm", sel.Count), L.T("Delete"))) return;
        await RunOp(L.T("Delete"), _ =>
        {
            foreach (var f in sel)
            {
                if (f.IsDir) Directory.Delete(f.FullPath, recursive: true);
                else File.Delete(f.FullPath);
            }
        });
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is not { } f) return;
        try
        {
            string text;
            if (f.IsDir)
            {
                var di = new DirectoryInfo(f.FullPath);
                var children = di.EnumerateFileSystemInfos().ToList();
                text = $"{L.T("ColName")}: {f.Name}\n{L.T("Path")}: {f.FullPath}\n{L.T("ColType")}: {L.T("Folder")}\n" +
                       $"{L.T("Contains")}: {children.Count}\n{L.T("Created")}: {di.CreationTime:g}\n{L.T("ColModified")}: {di.LastWriteTime:g}\n" +
                       $"{L.T("Attributes")}: {di.Attributes}";
            }
            else
            {
                var fi = new System.IO.FileInfo(f.FullPath);
                text = $"{L.T("ColName")}: {f.Name}\n{L.T("Path")}: {f.FullPath}\n{L.T("ColType")}: {f.TypeText}\n" +
                       $"{L.T("ColSize")}: {Fmt.Bytes(fi.Length)} ({fi.Length:N0} bytes)\n{L.T("Created")}: {fi.CreationTime:g}\n" +
                       $"{L.T("ColModified")}: {fi.LastWriteTime:g}\n{L.T("Attributes")}: {fi.Attributes}";
            }
            Dialogs.Info(Owner, L.T("Properties"), text);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _current.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Files_Drop(object sender, DragEventArgs e)
    {
        if (_current.Length == 0 || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var target = _current;
        await RunOp(L.T("Upload"), p => CopyInto(paths, target, move: false, p));
    }

    // ------------------------------------------------------------------ operations

    private void SetBusy(bool busy, string text = "")
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length > 0) StatusText.Text = text;
    }

    private async Task RunOp(string title, Action<IProgress<string>> op)
    {
        var progress = new Progress<string>(t => StatusText.Text = t);
        SetBusy(true, title + "…");
        try
        {
            await Task.Run(() => op(progress));
            StatusText.Text = L.F("OpDone", title);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
            StatusText.Text = ex.Message;
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            await LoadAsync(_current);
        }
    }

    /// <summary>Copies (or moves) files/folders into a folder with sequential streaming writes.</summary>
    private static void CopyInto(IEnumerable<string> sources, string targetDir, bool move, IProgress<string> progress)
    {
        foreach (var src in sources)
        {
            var name = Path.GetFileName(src.TrimEnd('\\'));
            var dest = Path.Combine(targetDir, name);
            if (string.Equals(Path.GetFullPath(src).TrimEnd('\\'), Path.GetFullPath(dest).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                dest = Path.Combine(targetDir, L.F("CopyOf", name));
            var sameVolume = string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(targetDir), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(src))
            {
                if (move && sameVolume)
                {
                    Directory.Move(src, dest);
                    continue;
                }
                CopyDirectory(src, dest, progress);
                if (move) Directory.Delete(src, true);
            }
            else
            {
                if (move && sameVolume)
                {
                    File.Move(src, dest, overwrite: false);
                    continue;
                }
                CopyFile(src, dest, progress);
                if (move) File.Delete(src);
            }
        }
    }

    private static void CopyDirectory(string src, string dest, IProgress<string> progress)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)), progress);
        foreach (var file in Directory.EnumerateFiles(src))
            CopyFile(file, Path.Combine(dest, Path.GetFileName(file)), progress);
    }

    private static void CopyFile(string src, string dest, IProgress<string> progress)
    {
        const int bufSize = 4 << 20;
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, bufSize, FileOptions.SequentialScan);
        using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufSize);
        var total = input.Length;
        var buf = new byte[bufSize];
        long done = 0;
        var sw = Stopwatch.StartNew();
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            output.Write(buf, 0, n);
            done += n;
            if (sw.ElapsedMilliseconds > 400)
            {
                progress.Report($"{Path.GetFileName(src)} — {Fmt.Bytes(done)} / {Fmt.Bytes(total)}");
                sw.Restart();
            }
        }
        progress.Report(L.F("Committing", Path.GetFileName(dest)));
        // Closing the handle commits the file to the cloud and verifies it before returning.
    }
}
