# MySnip 0.1.0

MySnip 是一套 Windows 10/11 x64 截圖與桌面貼圖工具。Windows 版使用 C#／WPF，Python 版使用 Tkinter／Pillow。兩版可各自啟動，並各自保存設定、歷史與貼圖群組。Python 啟動方式見 [Python 版說明](python/README.md)。

| 功能 | Windows（WPF）與 Python 共用操作 |
| --- | --- |
| 多螢幕截圖 | `F1` 進入選取。拖曳可跨螢幕，`F` 選取游標所在螢幕，`Shift+F` 選取整個虛擬桌面 |
| 自動選取 | 預覽視窗與 Windows UI Automation 元件，`Tab` 切換候選，`W` 選取視窗 |
| 截圖輸出 | `Enter` 複製、`Ctrl+T` 貼圖、`Ctrl+S` 另存、`Ctrl+Shift+S` 快速儲存。右鍵可開啟 OCR、條碼、Windows 分享與列印 |
| 截圖歷史 | `,` 和 `.` 瀏覽，從系統匣管理清單、複製、貼圖或重截 |
| 桌面貼圖 | `F3` 從剪貼簿貼圖，`Shift+F3` 顯示／隱藏。可拖入檔案、圖片或圖片網址，右鍵拖曳靜態貼圖會把裁切結果貼成另一張圖 |
| 貼圖管理 | 群組、備份還原、虛擬桌面、縮圖、滑鼠穿透、GIF 播放與逐格檢視 |
| 設定 | Windows 登入啟動、主題、放大鏡、熱角、系統匣動作、自訂命令快捷鍵與 Win＋左鍵拖曳 |

## 圖示

MySnip 使用自有的「截圖框＋貼圖圖釘」圖示，原始向量檔位於 `assets\MySnip.svg`，多尺寸 Windows 圖示位於 `assets\MySnip.ico`。它會套用到執行檔、系統匣與安裝捷徑；建置時由 `tools\Generate-MySnipIcon.ps1` 重新產生 ICO。

## 安裝與第一次使用

1. 執行 `dist\MySnip-Setup-x64.exe`。安裝位置預設為 `%LocalAppData%\Programs\MySnip`，不需系統管理員權限。
2. MySnip 啟動後常駐系統匣。左鍵點擊匣圖示開始截圖，中鍵從剪貼簿貼圖。
3. 按 `F1` 開始截圖，按 `F3` 將剪貼簿內容貼到桌面。
4. 若快捷鍵被其他程式占用，從系統匣的「設定…」改用其他 F1–F12 功能鍵。

未簽章的安裝程式可能顯示 Windows SmartScreen 警告。請只使用自行建置或可信來源提供的檔案。

## 截圖與標註

- **多螢幕**。按 `F1` 後，直接從一台螢幕拖曳到另一台即可跨螢幕截圖。將游標移到副螢幕後按 `F` 可選取該台螢幕，`Shift+F` 選取全部。系統匣的「截取全部螢幕」會直接複製整個桌面。
- `F1`：隱藏既有貼圖後擷取虛擬桌面；滑鼠移動時優先預覽游標下最小的 UI 矩形。超過目前螢幕一半面積的大型候選不會自動框選，空白處也不顯示選框。點擊接受區塊，拖曳則建立自訂範圍；按 `W` 可明確選取整個視窗。
- 選取前會顯示游標附近的像素放大鏡、全域實體座標與 RGB 色值。按住 `Shift` 顯示 HEX，按 `C` 複製色碼後可用 `F3` 貼成色卡。
- 右鍵或按工具列「⋯」可開啟白板、透明白板、固定比例、輸入尺寸、UI 元素範圍、重新擷取與輸出樣式。系統匣提供延遲截圖、自訂實體座標和重截上一次的範圍。
- 在截圖畫面按 `,`／`.` 瀏覽截圖歷史。系統匣的「截圖歷史」可直接把選取的紀錄貼到桌面。
- 選取框可拖曳、以四邊或四角控制點縮放；方向鍵微調 1 px，`Shift + 方向鍵` 微調 10 px。
- 選取後顯示兩層式圖示工具列：第一列切換工具與完成動作，第二列依工具提供形狀、箭頭、粗細、濾鏡強度、文字字型、色盤與透明度設定。
- 標註工具包含矩形、圓角矩形、橢圓、直線、折線、雙向箭頭、畫筆、螢光筆、文字、編號、馬賽克、高斯模糊、區域放大與橡皮擦。按 `V` 進入選取模式後，可右鍵點擊既有標註修改文字或筆觸、旋轉或刪除。
- 工具列可改色彩、粗細、透明度；設定頁可保存下次截圖的預設樣式。
- `Ctrl+Z` / `Ctrl+Y`：復原 / 重做。
- `Enter` 或 `Ctrl+C` 複製並完成，`Ctrl+S` 另存 PNG/JPEG，`Ctrl+Shift+S` 快速儲存 PNG，`Ctrl+T` 貼到桌面，`Esc` 取消。
- 右鍵選單可擷取文字、讀取 QR Code／條碼、列印或在其他應用程式開啟。OCR 使用已安裝的 Windows 辨識語言。條碼功能使用 PATH 裡的 Python，可執行 `python -m pip install zxing-cpp Pillow` 安裝解碼器。若環境已裝有 OpenCV，也可用它讀取該版本支援的格式。

## 桌面貼圖

- `F3` 支援圖片、純文字、常見 HTML 標籤、色碼及多個本機圖片檔。也能貼上或拖放可直接存取的圖片網址。
- 拖曳移動；滾輪縮放；`Ctrl + 滾輪` 調整透明度。
- 右鍵選單可旋轉 90°、翻轉、裁切、灰階、反相、選擇透明區域背景、指定縮放、切換置頂或滑鼠穿透。靜態貼圖按 `Space` 編輯標註，按 `Enter` 套用。
- 靜態貼圖雙擊會隱藏，`Shift+雙擊` 切換縮圖。GIF 可用 `Space` 暫停或播放，`1`／`2` 逐格查看，滾輪調整播放速度。
- 用 `Ctrl+點擊` 選取多張貼圖，可一起移動、調整透明度或切換群組。系統匣可新增、切換、重新命名及移除群組。`Shift+F3` 顯示或隱藏目前群組的貼圖。
- 若貼圖開啟滑鼠穿透後無法點選，從系統匣選「取消所有滑鼠穿透」。
- 預設會在下次啟動時還原貼圖與群組，可在設定中關閉。

## 隱私與設定

- 透過圖片網址貼圖時會連線至圖片來源。截圖、標註、OCR 與本機貼圖在本機處理。
- 設定寫入 `%LocalAppData%\MySnip\settings.json`。
- 截圖歷史和貼圖備份分別位於 `%LocalAppData%\MySnip\captures` 和 `%LocalAppData%\MySnip\stickers`。歷史預設保留 20 筆，設為 0 會清除歷史並停止記錄。
- 設定可調整延遲秒數、自動儲存、是否擷取游標，以及游標停在四個螢幕角落時的動作。
- 損毀的設定檔會被更名為 `settings.json.corrupt-時間戳`，程式則恢復預設值。
- 開機啟動預設關閉；開啟後只寫入目前使用者的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- 解除安裝會清除開機啟動項與程式檔，但保留上述使用者設定。

## 從原始碼建置

需求：

- Windows 10/11 x64
- `global.json` 指定的 .NET 9 SDK（9.0.308 或同一功能版本的較新修補版）
- 若需產生安裝檔：Inno Setup 6（腳本會搜尋 PATH、每使用者安裝路徑與 Program Files）

Debug 建置與測試：

```powershell
dotnet restore MySnip.sln
dotnet build MySnip.sln -c Debug --no-restore
dotnet test MySnip.sln -c Debug --no-build
```

完整 Release、self-contained 發佈與安裝器：

```powershell
.\build-release.ps1
```

只產生發佈目錄、不執行 Inno Setup：

```powershell
.\build-release.ps1 -SkipInstaller
```

在已有 .NET 9 Desktop Runtime 的 Windows 上，可建置單一應用程式檔案。

```powershell
dotnet publish src/MySnip.App/MySnip.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/wpf-win-x64
```

Python 版的可攜執行檔建置方式見 [Python 版說明](python/README.md#建置-windows-執行檔)。

若具備有效的程式碼簽章憑證，傳入憑證指紋；電腦同時需能找到 `signtool.exe`：

```powershell
.\build-release.ps1 -CertificateThumbprint 'YOUR_CERTIFICATE_THUMBPRINT'
```

輸出：

- `publish\win-x64\`：win-x64 self-contained、未裁切的應用程式。
- `artifacts\MySnip-tests.trx`：Release 測試報告。
- `dist\MySnip-Setup-x64.exe`：單一使用者 Inno Setup 安裝程式。

## 專案結構

- `src\MySnip.Core`：實體像素幾何、設定模型、服務介面與 Undo/Redo。
- `src\MySnip.App`：WPF 使用者介面、Win32 全域熱鍵/螢幕擷取、剪貼簿、系統匣與貼圖。
- `tests\MySnip.Core.Tests`：座標、選取縮放、設定復原、快捷鍵、Undo/Redo 與貼圖轉換測試。
- `installer`：Inno Setup 安裝腳本。
- `python` 為獨立的 Tkinter 版本，包含 Windows 擷取、標註、貼圖、持久化與命令列功能。

## 命令列

兩版接受相同參數。Windows 版用 `MySnip.exe`，Python 版用 `python .\python\mysnip.py`。座標使用 Windows 虛擬桌面的實體像素，位在主螢幕左側或上方時可用負座標。當同一版本已在執行，命令會交給該實例處理。

```powershell
.\MySnip.exe snip --full -o clipboard
.\MySnip.exe snip --active-screen --delay 2 -o quick-save
.\MySnip.exe snip --area -1280 50 800 600 -o pin
.\MySnip.exe snip --shadow -o 'clipboard;pin'
.\MySnip.exe snip --last --hold -o 'C:\Temp\MySnip.png'
.\MySnip.exe paste --plain '確認完成' --pos 200 150
.\MySnip.exe paste --files '.\reference.png'
.\MySnip.exe switch-group '工作'
python .\python\mysnip.py snip --area -1280 50 1600 600 -o 'clipboard;pin'
```

| 命令 | 選項 |
| --- | --- |
| `snip` | `--area X Y W H`、`--size W H`、`--full`、`--last`、`--active-window`、`--active-screen`、`--custom`、`--delay 秒數`、`--hold`、`--shadow`、`--no-shadow` |
| `snip -o` | `clipboard`、`pin`、`quick-save`、`file-dialog`、`ocr_clipboard`、`barcode_clipboard`、`copy-file`、`printer`、`success` 或 PNG／JPEG 路徑。可合併多種輸出，另可加入 `silent`、`no-auto-save` |
| `paste` | `--clipboard`、`--plain 文字`、`--html HTML` 或 `--files 圖片...`，可加 `--pos X Y` |
| 貼圖 | `toggle-images`、`show-images`、`hide-images`、`toggle-click-through`、`no-click-through` |
| 群組 | `show-group-manager`、`create-group [名稱]`、`switch-group [名稱]`、`switch-group-prev`、`empty-group` |
| 歷史與應用程式 | `show-capture-history`、`clear-snip-history`、`show-tray-menu`、`open-preferences`、`enable-hotkeys`、`disable-hotkeys`、`enable-hot-corners`、`disable-hot-corners`、`exit` |

指定範圍及輸出時會直接擷取，互動選取則在確認範圍後輸出。加上 `--hold` 可先調整範圍與標註，再按 `Enter` 完成輸出。`--custom` 對話框接受 `X Y 寬 高` 和選填的延遲秒數。

## 已知限制

- UI 子元素候選依賴目標程式提供的 Windows UI Automation 資訊。沒有對應資訊時，可用視窗選取或直接拖曳。
- 混合 DPI 及副螢幕在左側或上方的虛擬座標已有測試。不同 DPI 的實體多螢幕仍需桌面驗收。

第三方元件與授權請見 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
