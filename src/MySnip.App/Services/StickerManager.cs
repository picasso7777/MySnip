using System.Windows.Media.Imaging;
using MySnip.App.Windows;

namespace MySnip.App.Services;

internal sealed class StickerManager
{
    private readonly List<StickerWindow> _stickers = [];
    private bool _allHidden;

    public int Count => _stickers.Count;

    public void Create(BitmapSource source)
    {
        var sticker = new StickerWindow(source, Remove);
        _stickers.Add(sticker);
        sticker.Show();
        sticker.Activate();
    }

    public void HideAll()
    {
        foreach (var sticker in _stickers) sticker.Hide();
        _allHidden = true;
    }

    public void ShowAll()
    {
        foreach (var sticker in _stickers) sticker.Show();
        _allHidden = false;
    }

    public void ToggleVisibility()
    {
        if (_allHidden) ShowAll(); else HideAll();
    }

    public void DisableAllClickThrough()
    {
        foreach (var sticker in _stickers) sticker.DisableClickThrough();
        ShowAll();
    }

    public void CloseAll()
    {
        foreach (var sticker in _stickers.ToArray()) sticker.Close();
        _stickers.Clear();
    }

    private void Remove(StickerWindow sticker) => _stickers.Remove(sticker);
}
