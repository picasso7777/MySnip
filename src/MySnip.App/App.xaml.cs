using System.Drawing;
using System.Threading;
using System.Windows;
using MySnip.App.Services;
using MySnip.App.Windows;
using MySnip.Core.Models;
using MySnip.Core.Services;

namespace MySnip.App;

public sealed partial class App : System.Windows.Application
{
    private const int CaptureHotkeyId = 1;
    private const int PasteHotkeyId = 2;
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
    private AppSettings _settings = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private Icon? _appIcon;
    private bool _captureActive;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\MySnip.SingleInstance.v1", out var isFirst);
        _ownsMutex = isFirst;
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(@"Local\MySnip.Activate.v1").Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MySnip.Activate.v1");
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activationEvent, OnActivationSignaled, null, Timeout.Infinite, false);
        _settings = _settingsStore.Load();
        _hotkeys = new GlobalHotkeyService();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        CreateTray();
        if (!RegisterHotkeys(showErrors: false))
            ShowBalloon("部分全域快捷鍵已被其他程式占用，請開啟設定更換快捷鍵。");
    }

    private void OnActivationSignaled(object? state, bool timedOut)
    {
        if (!timedOut) Dispatcher.BeginInvoke(ShowSettings);
    }

    private void OnHotkeyPressed(object? sender, int id)
    {
        if (id == 0) ShowSettings();
        else if (id == CaptureHotkeyId) _ = BeginCaptureAsync();
        else if (id == PasteHotkeyId) PasteFromClipboard();
    }

    private async Task BeginCaptureAsync()
    {
        if (_captureActive) return;
        _captureActive = true;
        _stickers.HideAll();
        try
        {
            // Hiding a sticker is synchronous, but waiting for the next render keeps it
            // out of the captured image without the old fixed 60 ms delay.
            if (_stickers.Count > 0)
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var frame = _capture.CaptureVirtualDesktop();
            var candidates = _capture.GetCaptureCandidates();
            var overlay = new CaptureOverlayWindow(frame, candidates, _clipboard,
                bitmap => _stickers.Create(bitmap), _settings.OutputDirectory, _settings.Tools);
            using var candidateCancellation = new CancellationTokenSource();
            overlay.Closed += (_, _) => candidateCancellation.Cancel();
            overlay.Loaded += LoadDetailedCandidates;
            overlay.ShowDialog();

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
            _stickers.ShowAll();
            _captureActive = false;
        }
    }

    private void PasteFromClipboard()
    {
        try
        {
            var png = _clipboard.TryGetPng();
            if (png is null)
            {
                ShowBalloon("剪貼簿中沒有可用的圖片或圖片檔案。");
                return;
            }
            _stickers.Create(BitmapFactory.FromPng(png));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            MessageBox.Show($"無法讀取剪貼簿圖片：{ex.Message}", "MySnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        Add("開始截圖\tF1", (_, _) => _ = BeginCaptureAsync());
        Add("從剪貼簿貼圖\tF3", (_, _) => PasteFromClipboard());
        Add("顯示 / 隱藏全部貼圖", (_, _) => _stickers.ToggleVisibility());
        Add("取消所有滑鼠穿透", (_, _) => _stickers.DisableAllClickThrough());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        Add("設定…", (_, _) => ShowSettings());
        Add("關於 MySnip", (_, _) => MessageBox.Show("MySnip 0.1.0\n離線 Windows 截圖與貼圖工具\n\n本程式為獨立實作，未使用 Snipaste 程式碼或資產。", "關於 MySnip"));
        Add("離開", (_, _) => Shutdown());

        try
        {
            _appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        }
        catch
        {
            _appIcon = (Icon)SystemIcons.Application.Clone();
        }

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "MySnip",
            Icon = _appIcon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => _ = BeginCaptureAsync();
        return;

        void Add(string text, EventHandler handler)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(text);
            item.Click += handler;
            menu.Items.Add(item);
        }
    }

    private void ShowSettings()
    {
        var window = new SettingsWindow(_settings);
        MainWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(MainWindow, window)) MainWindow = null; };
        if (window.ShowDialog() != true || window.Result is null) return;
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
    }

    private bool RegisterHotkeys(bool showErrors)
    {
        if (_hotkeys is null) return false;
        _hotkeys.Unregister(CaptureHotkeyId);
        _hotkeys.Unregister(PasteHotkeyId);
        var captureOk = _hotkeys.TryRegister(CaptureHotkeyId, _settings.CaptureHotkey);
        var pasteOk = _hotkeys.TryRegister(PasteHotkeyId, _settings.PasteHotkey);
        if (captureOk && pasteOk) return true;
        if (showErrors)
            MessageBox.Show("部分全域快捷鍵已被其他程式占用。請在設定中選擇其他功能鍵。", "MySnip 快捷鍵衝突", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
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
        _stickers.CloseAll();
        _hotkeys?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _appIcon?.Dispose();
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
