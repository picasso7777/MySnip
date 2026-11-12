using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using MySnip.App.Services;
using MySnip.App.Windows;
using MySnip.Core.Models;
using MySnip.Core.Services;
using MySnip.App.Interop;

namespace MySnip.App;

public sealed partial class App : System.Windows.Application
{
    private const int CaptureHotkeyId = 1;
    private const int PasteHotkeyId = 2;
    private const int StickerVisibilityHotkeyId = 3;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private GlobalHotkeyService? _hotkeys;
    private readonly JsonSettingsStore _settingsStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MySnip", "settings.json"));
    private readonly StartupService _startup = new();
    private readonly ScreenCaptureService _capture = new();
    private readonly ClipboardService _clipboard = new();
    private readonly StickerManager _stickers = new();
    private readonly HotCornerService _corners = new();
    private readonly CaptureHistory _history = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MySnip", "captures"));
    private AppSettings _settings = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private Icon? _appIcon;
    private bool _captureActive;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\MySnip.SingleInstance.v1", out var isFirst);
        _ownsMutex = isFirst;
        var command = new InstanceCommand(e.Args, Environment.CurrentDirectory);
        if (!isFirst)
        {
            if (!InstanceCommands.Forward(command))
            {
                if (e.Args.Length == 0)
                {
                    try { using var signal = EventWaitHandle.OpenExisting(@"Local\MySnip.Activate.v1"); signal.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                }
                else MessageBox.Show("執行中的 MySnip 無法接收命令。", "MySnip");
            }
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MySnip.Activate.v1");
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activationEvent, OnActivationSignaled, null, Timeout.Infinite, false);
        _settings = _settingsStore.Load();
        _stickers.Notify = ShowBalloon;
        _stickers.Edit = sticker => _ = EditStickerAsync(sticker);
        _stickers.Drop = HandleStickerDrop;
        _stickers.OpenWebImage = (sticker, uri) => _ = OpenWebImageAsync(uri, sticker: sticker);
        SessionEnding += (_, _) => _stickers.SaveNow();
        _hotkeys = new GlobalHotkeyService();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        CreateTray();
        ConfigureAdvanced();
        _stickers.Initialize(Path.Combine(Path.GetDirectoryName(_settingsStore.SettingsPath)!, "stickers"), _settings.RestoreStickers);
        _foreground = new ForegroundWatcher(OnForegroundChanged);
        _corners.Triggered += OnHotCorner;
        _corners.Configure(_settings.HotCorners);
        if (!RegisterHotkeys(showErrors: false))
            ShowBalloon("部分全域快捷鍵已被其他程式占用，請開啟設定更換快捷鍵。");
        _commandListener = new CancellationTokenSource();
        var listenerToken = _commandListener.Token;
        _ = Task.Run(() => InstanceCommands.ListenAsync(received =>
            Dispatcher.BeginInvoke(() => _ = RunCommandAsync(received)), listenerToken));
        if (e.Args.Length > 0) Dispatcher.BeginInvoke(() => _ = RunCommandAsync(command));
    }

    private void OnActivationSignaled(object? state, bool timedOut)
    {
        if (!timedOut) Dispatcher.BeginInvoke(ShowSettings);
    }

    private void OnHotkeyPressed(object? sender, int id)
    {
        if (id == 0) { ShowSettings(); return; }
        if (!_hotkeysEnabled || ForegroundWatcher.Matches(NativeMethods.GetForegroundWindow(), _settings.Advanced.IgnoreApplications)) return;
        if (id == CaptureHotkeyId) _ = BeginCaptureAsync();
        else if (id == PasteHotkeyId) PasteFromClipboard();
        else if (id == StickerVisibilityHotkeyId) _stickers.ToggleVisibility();
        else if (id >= FirstCommandHotkeyId) RunHotkeyCommand(id - FirstCommandHotkeyId);
    }

    private void OnHotCorner(HotCornerAction action)
    {
        if (_captureActive || MainWindow?.IsVisible == true) return;
        switch (action)
        {
            case HotCornerAction.Capture: _ = BeginCaptureAsync(); break;
            case HotCornerAction.Paste: PasteFromClipboard(); break;
            case HotCornerAction.ToggleStickers: _stickers.ToggleVisibility(); break;
        }
    }

    private async Task BeginCaptureAsync(double delaySeconds = 0, PixelRect? region = null, bool quickCopy = false,
        CaptureCommand? command = null)
    {
        if (_captureActive) return;
        _captureActive = true;
        IDisposable? hidden = null;
        try
        {
            delaySeconds = command?.Delay ?? delaySeconds;
            if (delaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            if (Dispatcher.HasShutdownStarted) return;
            var activeRegion = ScreenCaptureService.ActiveWindowBounds();
            if (command is not null) region = ResolveRegion(command, activeRegion);
            hidden = _stickers.SuspendVisibility();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            NativeMethods.DwmFlush();
            IReadOnlyList<CaptureOutputRequest> outputs = quickCopy
                ? [new(CaptureDestination.Clipboard)] : command?.Outputs ?? [];
            var hasOutputs = outputs.Count > 0;
            var autoOutput = hasOutputs && command?.Hold != true;
            if (autoOutput && region is { } quickRegion)
            {
                var quickFrame = _capture.CaptureRegion(quickRegion, _settings.IncludeCursor);
                var image = CaptureOutput.Decorate(BitmapFactory.FromFrame(quickFrame), false, false, command?.Shadow == true);
                hidden.Dispose();
                hidden = null;
                var saved = await WriteCommandOutputsAsync(outputs, quickFrame.Bounds, image, command?.Silent == true);
                RememberCapture(quickFrame.Bounds, image, saved, command?.SkipAutoSave == true);
                return;
            }

            var frame = _capture.CaptureVirtualDesktop(_settings.IncludeCursor);
            var candidates = _capture.GetCaptureCandidates();
            var overlay = new CaptureOverlayWindow(frame, candidates, _clipboard,
                bitmap => _stickers.Create(bitmap), _settings.OutputDirectory, _settings.Tools)
            {
                History = _history.Read(),
                LoadHistoryImage = _history.LoadImage,
                ActiveWindowRegion = activeRegion,
                RefreshFrame = () => _capture.CaptureVirtualDesktop(_settings.IncludeCursor),
                Notify = ShowBalloon,
                PreferencesChanged = SaveAnnotationPreferences,
                CommandOutput = hasOutputs,
                AutoCommandOutput = autoOutput,
                OutputShadow = command?.Shadow == true
            };
            overlay.ConfigurePreferences(_settings.Advanced);
            if (region is { } selection) overlay.SelectGlobalRegion(selection);
            using var candidateCancellation = new CancellationTokenSource();
            overlay.Closed += (_, _) => candidateCancellation.Cancel();
            overlay.Loaded += LoadDetailedCandidates;
            overlay.ShowDialog();
            if (overlay.CompletedCapture is { } result)
            {
                hidden.Dispose();
                hidden = null;
                if (result.Pinned && !outputs.Any(output => output.Destination == CaptureDestination.Pin))
                    _stickers.Create(result.Image, position: new PixelPoint(result.Region.X, result.Region.Y));
                var saved = result.Saved;
                if (hasOutputs)
                    saved |= await WriteCommandOutputsAsync(outputs, result.Region, result.Image, command?.Silent == true);
                RememberCapture(result.Region, result.Image, saved, command?.SkipAutoSave == true);
            }
            else if (overlay.CanceledCapture is { } canceled)
            {
                try { _history.Add(canceled.Region, canceled.Image, _settings.CaptureHistoryLimit, canceled: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { ShowBalloon($"無法記錄已取消的截圖：{ex.Message}"); }
            }

            async void LoadDetailedCandidates(object? sender, RoutedEventArgs args)
            {
                overlay.Loaded -= LoadDetailedCandidates;
                try
                {
                    // The overlay is already visible before any external UI Automation
                    // provider is contacted. Broken or slow providers can no longer delay F1.
                    var detailed = await Task.Run(
                        () => _capture.GetAutomationCaptureCandidates(candidateCancellation.Token),
                        candidateCancellation.Token);
                    if (!candidateCancellation.IsCancellationRequested && overlay.IsVisible)
                        overlay.AddCaptureCandidates(detailed);
                }
                catch (OperationCanceledException)
                {
                    // Closing the overlay cancels best-effort background discovery.
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageBox.Show($"無法開始截圖：{ex.Message}", "MySnip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            hidden?.Dispose();
            _captureActive = false;
        }
    }

    private void RememberCapture(PixelRect region, System.Windows.Media.Imaging.BitmapSource image, bool alreadySaved,
        bool skipAutoSave = false)
    {
        try { _history.Add(region, image, _settings.CaptureHistoryLimit); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowBalloon($"截圖完成，無法寫入歷史：{ex.Message}"); }

        if (!_settings.AutoSaveCaptures || alreadySaved || skipAutoSave) return;
        try { CaptureOutput.QuickSave(image, _settings.OutputDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { ShowBalloon($"截圖完成，無法自動儲存：{ex.Message}"); }
    }

    private void PasteFromClipboard(PixelPoint? position = null)
    {
        try
        {
            if (_clipboard.ReadImageUrl() is { } uri)
            {
                _ = OpenWebImageAsync(uri, position: position);
                return;
            }
            var contents = _clipboard.ReadStickers();
            if (contents.Count == 0)
            {
                ShowBalloon("剪貼簿中沒有可用的圖片、圖片檔案或文字。");
                return;
            }
            var cursor = System.Windows.Forms.Control.MousePosition;
            for (var i = 0; i < contents.Count; i++)
                _stickers.Create(contents[i], position is { } anchor ? new PixelPoint(anchor.X + i * 20, anchor.Y + i * 20)
                    : new PixelPoint(cursor.X + 12 + i * 20, cursor.Y + 12 + i * 20));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show($"無法讀取剪貼簿圖片：{ex.Message}", "MySnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task EditStickerAsync(StickerWindow sticker)
    {
        if (_captureActive) return;
        _captureActive = true;
        var source = sticker.ImageContent.Frames[0];
        var position = sticker.Snapshot().Position;
        using var hidden = _stickers.SuspendVisibility();
        try
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            NativeMethods.DwmFlush();
            var frame = _capture.CaptureVirtualDesktop();
            var display = DesktopGeometry.DisplayAt(ScreenCaptureService.GetDisplayBounds(), position, frame.Bounds);
            var scale = Math.Min(1, Math.Min(display.Width / (double)source.PixelWidth, display.Height / (double)source.PixelHeight));
            var region = new PixelRect(position.X, position.Y, Math.Max(1, (int)(source.PixelWidth * scale)),
                Math.Max(1, (int)(source.PixelHeight * scale))).MoveBy(0, 0, display);
            var overlay = new CaptureOverlayWindow(frame, [], _clipboard,
                bitmap => sticker.ReplaceImage(new StickerContent(bitmap)), _settings.OutputDirectory, _settings.Tools)
            { PreferencesChanged = SaveAnnotationPreferences };
            overlay.ConfigurePreferences(_settings.Advanced);
            overlay.EditSticker(source, region);
            overlay.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { ShowBalloon($"無法編輯貼圖：{ex.Message}"); }
        finally { _captureActive = false; }
    }

    private void HandleStickerDrop(StickerWindow sticker, IDataObject data)
    {
        try
        {
            if (WebStickerService.FindImageUrl(data) is { } uri)
                _ = OpenWebImageAsync(uri, sticker: sticker);
            else if (data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                var images = paths.Where(StickerContent.Supports).Select(StickerContent.Load).ToArray();
                if (images.Length > 0) sticker.ReplaceImage(images[0]);
                foreach (var image in images.Skip(1)) _stickers.Create(image);
            }
            else if (data.GetData("PNG", false) is MemoryStream png)
                sticker.ReplaceImage(new StickerContent(BitmapFactory.FromPng(png.ToArray())));
            else if (data.GetData("PNG", false) is byte[] bytes)
                sticker.ReplaceImage(new StickerContent(BitmapFactory.FromPng(bytes)));
            else if (data.GetData(DataFormats.Bitmap) is BitmapSource source)
                sticker.ReplaceImage(new StickerContent(source));
            else if (data.GetData(DataFormats.UnicodeText) is string content && !string.IsNullOrWhiteSpace(content))
                sticker.ReplaceImage(StickerText.TryParseColor(content, out var color) ? StickerText.ColorCard(color)
                    : new StickerContent(StickerText.Render(content, data.GetData(DataFormats.Html) as string)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { ShowBalloon($"無法開啟拖放的圖片：{ex.Message}"); }
    }

    private async Task OpenWebImageAsync(Uri uri, StickerWindow? sticker = null, PixelPoint? position = null)
    {
        try
        {
            var content = await WebStickerService.DownloadAsync(uri);
            if (Dispatcher.HasShutdownStarted) return;
            if (sticker is null) _stickers.Create(content, position);
            else if (_stickers.Contains(sticker)) sticker.ReplaceImage(content);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or
            System.Net.Http.HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        { ShowBalloon($"無法開啟網頁圖片：{ex.Message}"); }
    }

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var capture = Add("開始截圖", (_, _) => _ = BeginCaptureAsync());
        var paste = Add("從剪貼簿貼圖", (_, _) => PasteFromClipboard());
        Add("延遲截圖", (_, _) => _ = BeginCaptureAsync(_settings.CaptureDelaySeconds));
        Add("自訂截圖範圍…", (_, _) => _ = CaptureCustomRegionAsync());
        Add("截取全部螢幕", (_, _) => _ = BeginCaptureAsync(region: ScreenCaptureService.VirtualBounds(), quickCopy: true));
        var history = new System.Windows.Forms.ToolStripMenuItem("截圖歷史");
        menu.Items.Add(history);
        menu.Opening += (_, _) =>
        {
            capture.ShortcutKeyDisplayString = ShortcutText.Format(_settings.CaptureHotkey);
            paste.ShortcutKeyDisplayString = ShortcutText.Format(_settings.PasteHotkey);
            var items = history.DropDownItems.Cast<System.Windows.Forms.ToolStripItem>().ToArray();
            history.DropDownItems.Clear();
            foreach (var item in items) item.Dispose();
            var records = _history.Read();
            history.DropDownItems.Add("管理截圖歷史…", null, (_, _) => ShowCaptureHistory());
            if (records.Count == 0) return;
            if (records.FirstOrDefault(record => !record.Canceled) is { } last)
                history.DropDownItems.Add("重截上次範圍並複製", null, (_, _) => _ = BeginCaptureAsync(region: last.Region, quickCopy: true));
            history.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
            foreach (var record in records)
                history.DropDownItems.Add($"{record.CapturedAt:MM/dd HH:mm:ss}　{record.Region.Width} × {record.Region.Height}{(record.Canceled ? "（已取消）" : "")}",
                    null, (_, _) =>
                    {
                        try { _stickers.Create(_history.LoadImage(record)); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                        { ShowBalloon($"無法開啟截圖歷史：{ex.Message}"); }
                    });
        };
        Add("顯示 / 隱藏全部貼圖", (_, _) => _stickers.ToggleVisibility());
        Add("顯示群組中已隱藏的貼圖", (_, _) => _stickers.ShowHidden());
        Add("取消所有滑鼠穿透", (_, _) => _stickers.DisableAllClickThrough());
        var groups = new System.Windows.Forms.ToolStripMenuItem("貼圖群組");
        groups.DropDownOpening += (_, _) =>
        {
            var items = groups.DropDownItems.Cast<System.Windows.Forms.ToolStripItem>().ToArray();
            groups.DropDownItems.Clear();
            foreach (var item in items) item.Dispose();
            foreach (var group in _stickers.Groups)
            {
                var item = new System.Windows.Forms.ToolStripMenuItem(group) { Checked = group == _stickers.ActiveGroup };
                item.Click += (_, _) => _stickers.SwitchGroup(group);
                groups.DropDownItems.Add(item);
            }
            groups.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
            groups.DropDownItems.Add("管理貼圖群組…", null, (_, _) => ShowGroupManager());
            groups.DropDownItems.Add("新增群組…", null, (_, _) => EditGroupName(false));
            groups.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("重新命名群組…", null, (_, _) => EditGroupName(true))
                { Enabled = _stickers.ActiveGroup != _stickers.Groups[0] });
            groups.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("移除群組並將貼圖移至預設群組", null, (_, _) => _stickers.DeleteGroup())
                { Enabled = _stickers.ActiveGroup != _stickers.Groups[0] });
            groups.DropDownItems.Add("依名稱排序", null, (_, _) => _stickers.SortGroups());
            groups.DropDownItems.Add("將群組貼圖移到目前桌面", null, (_, _) => _stickers.BringGroupToCurrentDesktop());
        };
        groups.DropDownItems.Add("載入群組");
        menu.Items.Add(groups);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        Add("開啟截圖資料夾", (_, _) => OpenOutputDirectory());
        Add("設定…", (_, _) => ShowSettings());
        Add("關於 MySnip", (_, _) => MessageBox.Show("MySnip 0.1.0\nWindows 截圖與桌面貼圖工具", "關於 MySnip"));
        Add("離開", (_, _) => { _stickers.PrepareShutdown(); Shutdown(); });

        try
        {
            _appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        }
        catch
        {
            _appIcon = (Icon)SystemIcons.Application.Clone();
        }

        _trayMenu = menu;
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "MySnip",
            Icon = _appIcon,
            Visible = true
        };
        _tray.MouseClick += OnTrayClick;
        return;

        System.Windows.Forms.ToolStripMenuItem Add(string text, EventHandler handler)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(text);
            item.Click += handler;
            menu.Items.Add(item);
            return item;
        }
    }

    private void OpenOutputDirectory()
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_settings.OutputDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { ShowBalloon(ex.Message); }
    }

    private void EditGroupName(bool rename, Window? owner = null)
    {
        if (rename && _stickers.ActiveGroup == _stickers.Groups[0]) return;
        var name = InputDialog.Show(owner, rename ? "重新命名群組" : "新增群組", "輸入群組名稱",
            rename ? _stickers.ActiveGroup : "", _stickers.IsNewGroupName);
        if (name is null) return;
        if (rename) _stickers.RenameGroup(name); else _stickers.CreateGroup(name);
    }

    private void ShowSettings()
    {
        if (_settingsOpen) { MainWindow?.Activate(); return; }
        var window = new SettingsWindow(_settings);
        MainWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(MainWindow, window)) MainWindow = null; };
        _settingsOpen = true;
        try { if (window.ShowDialog() != true || window.Result is null) return; }
        finally { _settingsOpen = false; }
        var previous = _settings;
        _settings = window.Result;
        if (!RegisterHotkeys(showErrors: true))
        {
            _settings = previous;
            RegisterHotkeys(showErrors: false);
            return;
        }

        _startup.SetEnabled(_settings.StartWithWindows, Environment.ProcessPath!);
        _settingsStore.Save(_settings);
        _stickers.ConfigurePersistence(_settings.RestoreStickers);
        ConfigureAdvanced();
        SetCornersEnabled(_cornersEnabled);
        try { _history.Trim(_settings.CaptureHistoryLimit); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowBalloon($"無法更新截圖歷史：{ex.Message}"); }
    }

    private void ShowBalloon(string text)
    {
        if (_tray is null) return;
        _tray.BalloonTipTitle = "MySnip";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(3000);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _foreground?.Dispose();
        _superSnip?.Dispose();
        _commandListener?.Cancel();
        _commandListener?.Dispose();
        _corners.Dispose();
        _stickers.CloseAll();
        _hotkeys?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _trayMenu?.Dispose();
        _appIcon?.Dispose();
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
