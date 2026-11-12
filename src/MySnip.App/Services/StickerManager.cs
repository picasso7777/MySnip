using System.ComponentModel;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MySnip.App.Windows;
using MySnip.Core.Models;

namespace MySnip.App.Services;

internal sealed class StickerManager
{
    private const string DefaultGroup = "預設";
    private readonly List<StickerWindow> _stickers = [];
    private readonly List<string> _groups = [DefaultGroup];
    private readonly HashSet<StickerWindow> _selection = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly VirtualDesktopService _desktops = new();
    private Guid? _activeDesktop;
    private string? _previousGroup;
    private StickerSession? _session;
    private bool _allHidden;
    private bool _closing;
    private int _suspensionCount;
    private bool _persist;

    public StickerManager() => _saveTimer.Tick += (_, _) => Save();

    public IReadOnlyList<string> Groups => _groups;
    public string ActiveGroup { get; private set; } = DefaultGroup;
    public bool Solo { get; private set; }
    public Action<string>? Notify { get; set; }
    public Action<StickerWindow>? Edit { get; set; }
    public Action<StickerWindow, System.Windows.IDataObject>? Drop { get; set; }
    public Action<StickerWindow, Uri>? OpenWebImage { get; set; }
    public AdvancedPreferences Preferences { get; private set; } = new();

    public void ConfigurePreferences(AdvancedPreferences options)
    {
        Preferences = options;
        RefreshDesktop();
        UpdateVisibility();
    }

    public void RefreshDesktop()
    {
        if (_desktops.ActiveDesktop() is not { } active || active == _activeDesktop) return;
        _activeDesktop = active;
        ClearSelection();
        Solo = false;
        UpdateVisibility();
    }

    public void BringGroupToCurrentDesktop()
    {
        RefreshDesktop();
        foreach (var sticker in _stickers.Where(sticker => sticker.Group == ActiveGroup))
        {
            sticker.DesktopId = _activeDesktop;
            if (_activeDesktop is { } active) _desktops.MoveToDesktop(sticker.WindowHandle, active);
        }
        UpdateVisibility();
        Changed();
    }

    public void Initialize(string directory, bool persist)
    {
        _session = new StickerSession(directory);
        _persist = persist;
        if (!persist || _session.Read() is not { } workspace) return;
        foreach (var group in workspace.Groups ?? [])
            if (!string.IsNullOrWhiteSpace(group) && !_groups.Contains(group, StringComparer.CurrentCultureIgnoreCase)) _groups.Add(group);
        ActiveGroup = _groups.Contains(workspace.ActiveGroup) ? workspace.ActiveGroup : DefaultGroup;
        _allHidden = workspace.AllHidden;
        var ids = new HashSet<Guid>();
        foreach (var layout in workspace.Stickers ?? [])
        {
            if (layout is null || layout.Id == Guid.Empty || !ids.Add(layout.Id) || _session.Restore(layout) is not { } content) continue;
            var sticker = new StickerWindow(content, this, layout);
            if (!_groups.Contains(sticker.Group)) sticker.Group = DefaultGroup;
            _stickers.Add(sticker);
        }
        UpdateVisibility();
    }

    public void ConfigurePersistence(bool enabled)
    {
        if (_persist == enabled) return;
        _persist = enabled;
        if (enabled) Save();
        else
        {
            _saveTimer.Stop();
            try { _session?.Clear(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Notify?.Invoke($"無法清除貼圖備份：{ex.Message}"); }
        }
    }

    public void Create(BitmapSource source, PixelPoint? position = null) => Create(new StickerContent(source), position);

    public void Create(StickerContent content, PixelPoint? position = null)
    {
        RefreshDesktop();
        var sticker = new StickerWindow(content, this) { Group = ActiveGroup, DesktopId = _activeDesktop };
        if (position is { } anchor) sticker.SetInitialPosition(anchor);
        _stickers.Add(sticker);
        _allHidden = false;
        Solo = false;
        UpdateVisibility();
        if (sticker.IsVisible) sticker.Activate();
        Changed();
    }

    public IDisposable SuspendVisibility()
    {
        _suspensionCount++;
        UpdateVisibility();
        return new VisibilityScope(this);
    }

    public void ToggleVisibility()
    {
        _allHidden = !_allHidden;
        UpdateVisibility();
        Changed();
    }

    public void HideAll()
    {
        if (!_allHidden) ToggleVisibility();
    }

    public void DisableAllClickThrough()
    {
        foreach (var sticker in _stickers) sticker.SetClickThrough(false);
        ShowHidden();
    }

    public void Hide(StickerWindow sticker)
    {
        foreach (var target in Targets(sticker)) target.UserHidden = true;
        UpdateVisibility();
        Changed();
    }

    public void ShowHidden()
    {
        foreach (var sticker in _stickers.Where(sticker => sticker.Group == ActiveGroup)) sticker.UserHidden = false;
        _allHidden = false;
        Solo = false;
        UpdateVisibility();
        Changed();
    }

    public void Select(StickerWindow sticker)
    {
        if (!_selection.Add(sticker)) _selection.Remove(sticker);
        sticker.SetSelected(_selection.Contains(sticker));
        if (Solo && _selection.Count == 0) Solo = false;
        UpdateVisibility();
    }

    public void ToggleSolo(StickerWindow sticker)
    {
        if (!Solo && !_selection.Contains(sticker))
        {
            ClearSelection();
            Select(sticker);
        }
        Solo = !Solo;
        UpdateVisibility();
    }

    public StickerWindow[] Targets(StickerWindow origin) => _selection.Contains(origin) ? _selection.ToArray() : [origin];

    public bool Contains(StickerWindow sticker) => _stickers.Contains(sticker);

    public void SwitchGroup(string group)
    {
        if (!_groups.Contains(group)) return;
        if (group != ActiveGroup) _previousGroup = ActiveGroup;
        ActiveGroup = group;
        _allHidden = false;
        Solo = false;
        ClearSelection();
        UpdateVisibility();
        Changed();
    }

    public void SwitchToNextGroup() => SwitchGroup(_groups[(_groups.IndexOf(ActiveGroup) + 1) % _groups.Count]);

    public void SwitchToPreviousGroup()
    {
        if (_previousGroup is { } previous && _groups.Contains(previous)) SwitchGroup(previous);
        else SwitchToNextGroup();
    }

    public void EmptyGroup()
    {
        foreach (var sticker in _stickers.Where(sticker => sticker.Group == ActiveGroup).ToArray()) sticker.Close();
    }

    public void ToggleClickThrough()
    {
        var cursor = System.Windows.Forms.Control.MousePosition;
        var sticker = _stickers.LastOrDefault(window => window.IsVisible && window.ContainsScreenPoint(cursor.X, cursor.Y));
        if (sticker is null) DisableAllClickThrough();
        else { sticker.SetClickThrough(!sticker.IsClickThrough); Changed(); }
    }

    public void MoveToGroup(StickerWindow sticker, string group)
    {
        if (!_groups.Contains(group)) return;
        foreach (var target in Targets(sticker))
        {
            target.Group = group;
            _selection.Remove(target);
            target.SetSelected(false);
        }
        if (_selection.Count == 0) Solo = false;
        UpdateVisibility();
        Changed();
    }

    public void CreateGroup(string name)
    {
        if (!IsNewGroupName(name)) return;
        _groups.Add(name);
        SwitchGroup(name);
    }

    public bool IsNewGroupName(string value) => value.Length is > 0 and <= 40 &&
        !_groups.Contains(value, StringComparer.CurrentCultureIgnoreCase);

    public void RenameGroup(string name)
    {
        if (ActiveGroup == DefaultGroup || !IsNewGroupName(name)) return;
        _groups[_groups.IndexOf(ActiveGroup)] = name;
        foreach (var sticker in _stickers.Where(sticker => sticker.Group == ActiveGroup)) sticker.Group = name;
        SwitchGroup(name);
    }

    public void DeleteGroup()
    {
        if (ActiveGroup == DefaultGroup) return;
        foreach (var sticker in _stickers.Where(sticker => sticker.Group == ActiveGroup)) sticker.Group = DefaultGroup;
        _groups.Remove(ActiveGroup);
        SwitchGroup(DefaultGroup);
    }

    public void SortGroups()
    {
        _groups.Sort(1, _groups.Count - 1, StringComparer.CurrentCultureIgnoreCase);
        Changed();
    }

    public void Changed()
    {
        if (!_persist || _closing) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Remove(StickerWindow sticker)
    {
        _selection.Remove(sticker);
        _stickers.Remove(sticker);
        if (_closing) return;
        if (Solo && _selection.Count == 0) { Solo = false; UpdateVisibility(); }
        Changed();
    }

    public void PrepareShutdown()
    {
        Save();
        _closing = true;
    }

    public void SaveNow() => Save();

    public void CloseAll()
    {
        _saveTimer.Stop();
        _closing = true;
        foreach (var sticker in _stickers.ToArray()) sticker.Close();
        _desktops.Dispose();
    }

    private void Save()
    {
        _saveTimer.Stop();
        if (!_persist) return;
        try { _session?.Save(_groups.ToArray(), ActiveGroup, _allHidden, _stickers); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception)
        { Notify?.Invoke($"無法備份貼圖：{ex.Message}"); }
    }

    private void UpdateVisibility()
    {
        var follow = Preferences.StickerDesktop == StickerDesktopMode.FollowActiveDesktop;
        foreach (var sticker in _stickers)
        {
            var visible = _suspensionCount == 0 && !_allHidden && !sticker.UserHidden &&
                sticker.Group == ActiveGroup && (!Solo || _selection.Contains(sticker)) &&
                (follow || _activeDesktop is null || sticker.DesktopId is null || sticker.DesktopId == _activeDesktop);
            sticker.SetShown(visible);
            if (visible && _activeDesktop is { } desktop)
                _desktops.MoveToDesktop(sticker.WindowHandle, desktop);
        }
    }

    private void ClearSelection()
    {
        foreach (var sticker in _selection) sticker.SetSelected(false);
        _selection.Clear();
    }

    private sealed class VisibilityScope(StickerManager owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._suspensionCount--;
            owner.UpdateVisibility();
        }
    }
}
