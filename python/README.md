# MySnip Python 版

適用於 Windows 10/11 x64。需有包含 Tkinter 的 Python 3.8 以上版本。

從儲存庫根目錄執行。啟動指令會檢查 Pillow 和 pywin32，缺少時會安裝。

```powershell
.\python\Run-MySnip.ps1
```

需要 MSS 擷取與 ZXing 條碼辨識時，可一併安裝擴充套件。

```powershell
.\python\Run-MySnip.ps1 -InstallExtras
```

按 `F1` 開始截圖。可拖曳跨過不同螢幕，在游標所在螢幕按 `F` 選取該螢幕，或按 `Shift+F` 選取全部螢幕。按 `Enter` 複製，`Ctrl+T` 貼到桌面，`Ctrl+S` 另存。按 `F3` 從剪貼簿貼圖。更多操作與參數見 [兩版功能及命令列](../README.md)。

```powershell
python .\python\mysnip.py snip --area -1280 50 1600 600 -o 'clipboard;pin'
python .\python\mysnip.py show-group-manager
python -m unittest discover -s python -v
```

## 建置 Windows 執行檔

使用已安裝 Pillow 與 pywin32 的 Windows x64 Python 執行下列指令。

```powershell
python -B .\python\build_portable.py
```

執行 `publish\python-single-file-win-x64\MySnip-Python.exe`。程式、Python runtime 和基本依賴都封在同一個檔案內。第一次啟動時會解壓縮到 `%LOCALAPPDATA%\MySnip\python-runtime`，之後重用該版本的快取。建置環境若有安裝 `mss` 或 `zxing-cpp`，打包時會一併帶入。單檔版也能呼叫 PATH 上已有解碼套件的 Python 讀取 QR Code／條碼。

可在檔案總管雙擊 `MySnip-Python.exe`，啟動後到 Windows 系統匣找 MySnip。若終端機目前顯示 `>>>`，請先用 `exit()` 離開 Python 互動模式，再從 PowerShell 執行程式。

| 資料 | 位置 |
| --- | --- |
| 設定 | `%LOCALAPPDATA%\MySnip\python-settings.json` |
| 截圖歷史 | `%LOCALAPPDATA%\MySnip\python-captures` |
| 貼圖與群組備份 | `%LOCALAPPDATA%\MySnip\python-stickers` |

OCR 使用 Windows 已安裝的辨識語言。QR Code 和條碼可用 ZXing 或目前環境中具備解碼功能的 OpenCV。兩版預設的全域快捷鍵相同，同時啟動時可在設定中分別指定。
