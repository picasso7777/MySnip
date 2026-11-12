using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MySnip.App.Interop;
using MySnip.App.Services;
using MySnip.Core.Models;

namespace MySnip.App.Windows;

public sealed partial class CaptureOverlayWindow
{
    private const string PaletteDragFormat = "MySnip.PaletteIndex";
    private AdvancedPreferences _preferences = new();
    private List<string> _palette = [];
    private string[] _defaultPalette = [];
    private Dictionary<AnnotationTool, string> _toolColors = [];
    private Color _defaultDrawingColor;
    private Point _palettePress;
    private bool _styleDirty;
    private bool _recordedCanceled;

    internal Action<AdvancedPreferences>? PreferencesChanged { get; init; }
    internal (PixelRect Region, BitmapSource Image)? CanceledCapture { get; private set; }

    internal void ConfigurePreferences(AdvancedPreferences preferences)
    {
        _preferences = preferences;
        _defaultDrawingColor = _drawingColor;
        _toolColors = new Dictionary<AnnotationTool, string>(preferences.ToolColors);
        _defaultPalette = ColorPalette.Children.OfType<Button>().Select(button => (string)button.Tag).ToArray();
        _palette = (preferences.Palette.Length > 0 ? preferences.Palette : _defaultPalette).ToList();
        BuildPalette();
        BuildPenSizes();
        Magnifier.Width = preferences.MagnifierSize + 34;
        Magnifier.Height = preferences.MagnifierSize + 64;
        MagnifierViewport.Width = MagnifierViewport.Height = preferences.MagnifierSize;
        Magnifier.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preferences.MagnifierBackground));
        MagnifierCrosshair.Visibility = preferences.MagnifierCrosshair ? Visibility.Visible : Visibility.Collapsed;
        CurrentColorPreview.ToolTip = "滾輪調整透明度";
        CurrentColorPreview.PreviewMouseWheel += (_, e) =>
        {
            OpacitySlider.Value = Math.Clamp(OpacitySlider.Value + Math.Sign(e.Delta) * 0.05, OpacitySlider.Minimum, OpacitySlider.Maximum);
            e.Handled = true;
        };
        ColorPalette.ContextMenu = CreatePaletteMenu();
    }

    private void SaveAdvancedChanges()
    {
        if (_styleDirty)
            PreferencesChanged?.Invoke(_preferences with { Palette = _palette.ToArray(), ToolColors = _toolColors });
    }

    private void SaveCanceledCapture()
    {
        if (!_preferences.RecordCanceledCaptures || _editingSticker || CompletedCapture is not null || _recordedCanceled)
            return;
        _recordedCanceled = true;
        try { CanceledCapture = !_selection.IsEmpty ? (GlobalSelection(), RenderSelection()) : (_frame.Bounds, _bitmap); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { System.Diagnostics.Trace.TraceWarning("Cannot record canceled capture: {0}", ex.Message); }
    }

    private void SetDrawingColor(Color color)
    {
        _drawingColor = color;
        if (_preferences.RememberToolColors && _tool != AnnotationTool.Select)
        {
            _toolColors[_tool] = color.ToString();
            _styleDirty = true;
        }
        UpdateColorPreview();
    }

    private void RestoreToolColor()
    {
        if (!_preferences.RememberToolColors) return;
        _drawingColor = _toolColors.TryGetValue(_tool, out var color)
            ? (Color)ColorConverter.ConvertFromString(color) : _defaultDrawingColor;
    }

    private void BuildPenSizes()
    {
        PenPresets.Children.Clear();
        for (var i = 0; i < _preferences.PenSizes.Length; i++)
        {
            var index = i;
            var button = new Button { Content = _preferences.PenSizes[i], MinWidth = 26, Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(1), ToolTip = "按一下套用，滾輪調整此預設" };
            button.Click += (_, _) => WidthSlider.Value = _preferences.PenSizes[index];
            button.PreviewMouseWheel += (_, e) =>
            {
                var widths = _preferences.PenSizes.ToArray();
                widths[index] = Math.Clamp(widths[index] + Math.Sign(e.Delta), 1, 32);
                _preferences = _preferences with { PenSizes = widths };
                button.Content = widths[index];
                WidthSlider.Value = widths[index];
                _styleDirty = true;
                e.Handled = true;
            };
            PenPresets.Children.Add(button);
        }
    }

    private void BuildPalette()
    {
        ColorPalette.Children.Clear();
        for (var i = 0; i < _palette.Count; i++)
        {
            var index = i;
            var color = _palette[i];
            var swatch = new Button
            {
                Tag = color, ToolTip = color, Style = (Style)ColorPalette.FindResource("PaletteButton"),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), AllowDrop = true
            };
            swatch.Click += ColorSwatch_Click;
            swatch.PreviewMouseLeftButtonDown += (_, e) => _palettePress = e.GetPosition(ColorPalette);
            swatch.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var delta = e.GetPosition(ColorPalette) - _palettePress;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                DragDrop.DoDragDrop(swatch, new DataObject(PaletteDragFormat, index), DragDropEffects.Move);
                e.Handled = true;
            };
            swatch.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(PaletteDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            swatch.Drop += (_, e) =>
            {
                if (e.Data.GetData(PaletteDragFormat) is not int source || source == index || source < 0 || source >= _palette.Count) return;
                var moved = _palette[source];
                _palette.RemoveAt(source);
                _palette.Insert(index, moved);
                PaletteChanged();
                e.Handled = true;
            };
            swatch.ContextMenu = CreatePaletteMenu(index);
            ColorPalette.Children.Add(swatch);
        }
        UpdateColorPreview();
    }

    private ContextMenu CreatePaletteMenu(int? index = null)
    {
        var menu = new ContextMenu();
        if (index is { } swatch)
        {
            Add("以目前顏色取代", () => { _palette[swatch] = _drawingColor.ToString(); PaletteChanged(); });
            if (_palette.Count > 1) Add("移除這個顏色", () => { _palette.RemoveAt(swatch); PaletteChanged(); });
        }
        if (_palette.Count < 40) Add("將目前顏色加入色盤", () => { _palette.Add(_drawingColor.ToString()); PaletteChanged(); });
        Add("匯入色盤…", ImportPalette);
        Add("匯出色盤…", ExportPalette);
        Add("還原預設色盤", () => { _palette = _defaultPalette.ToList(); PaletteChanged(); });
        return menu;

        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void PaletteChanged()
    {
        _styleDirty = true;
        BuildPalette();
        ColorPalette.ContextMenu = CreatePaletteMenu();
    }

    private void ImportPalette()
    {
        var picker = new OpenFileDialog { Filter = "MySnip 色盤|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 4096) throw new InvalidDataException("色盤檔案過大。");
            var colors = JsonSerializer.Deserialize<string[]>(File.ReadAllText(picker.FileName));
            if (colors is not { Length: >= 1 and <= 40 } || colors.Any(color => !AdvancedPreferences.IsColor(color)))
                throw new InvalidDataException("色盤須包含 1–40 個 #AARRGGBB 色碼。");
            _palette = colors.ToList();
            PaletteChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { MessageBox.Show(this, ex.Message, "MySnip 色盤"); }
    }

    private void ExportPalette()
    {
        var picker = new SaveFileDialog { Filter = "MySnip 色盤|*.json", FileName = "MySnip-palette.json" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, JsonSerializer.Serialize(_palette, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, ex.Message, "MySnip 色盤"); }
    }

    private void ToggleNativeOverlay(bool visible)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
            NativeMethods.SetWindowPos(handle, 0, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize |
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate |
                (visible ? NativeMethods.SwpShowWindow : NativeMethods.SwpHideWindow));
    }
}
