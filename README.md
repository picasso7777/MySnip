# MySnip 0.1.0

MySnip 是一套獨立實作、完全離線的 Windows 截圖與桌面貼圖工具，適用於 Windows 10/11 x64。它不使用 Snipaste 的程式碼、圖示、文字或其他專有資產。

## 圖示

MySnip 使用自有的「截圖框＋貼圖圖釘」圖示，原始向量檔位於 `assets\MySnip.svg`，多尺寸 Windows 圖示位於 `assets\MySnip.ico`。它會套用到執行檔、系統匣與安裝捷徑；建置時由 `tools\Generate-MySnipIcon.ps1` 重新產生 ICO。

## 安裝與第一次使用

1. 執行 `dist\MySnip-Setup-x64.exe`。安裝位置預設為 `%LocalAppData%\Programs\MySnip`，不需系統管理員權限。
2. MySnip 啟動後常駐系統匣；雙擊匣圖示也能開始截圖。
3. 按 `F1` 開始截圖，按 `F3` 將剪貼簿中的圖片貼到桌面。
4. 若快捷鍵被其他程式占用，從系統匣的「設定…」改用其他 F1–F12 功能鍵。

未簽章的安裝程式可能顯示 Windows SmartScreen 警告。請只使用自行建置或可信來源提供的檔案。

## 截圖與標註

- `F1`：隱藏既有貼圖後擷取虛擬桌面；滑鼠移動時優先預覽游標下最小的 UI 矩形。超過目前螢幕一半面積的大型候選不會自動框選，空白處也不顯示選框。點擊接受區塊，拖曳則建立自訂範圍；按 `W` 可明確選取整個視窗。
- 選取前會顯示游標附近的像素放大鏡、全域實體座標與 RGB 色值；按住 `Shift` 暫時改顯示 HEX 色值。
- `W`：選取游標下的最上層視窗；`F`：選取游標所在螢幕。
- 選取框可拖曳、以四邊或四角控制點縮放；方向鍵微調 1 px，`Shift + 方向鍵` 微調 10 px。
- 選取後顯示兩層式圖示工具列：第一列切換工具與完成動作，第二列依工具提供形狀、箭頭、粗細、濾鏡強度、文字字型、色盤與透明度設定。
- 標註工具：空心／實心矩形、空心／實心橢圓、直線、箭頭、畫筆、螢光筆、文字、馬賽克、模糊、橡皮擦及選取移動。
- 工具列可改色彩、粗細、透明度；設定頁可保存下次截圖的預設樣式。
- `Ctrl+Z` / `Ctrl+Y`：復原 / 重做。
- `Enter` 或 `Ctrl+C`：複製並完成；`Ctrl+S`：另存 PNG/JPEG；`Ctrl+T`：直接貼到桌面；`Esc`：取消。

## 桌面貼圖

- `F3` 支援剪貼簿圖片，或剪貼簿檔案清單中的第一個本機圖片檔。
- 拖曳移動；滾輪縮放；`Ctrl + 滾輪` 調整透明度。
- 右鍵選單可旋轉 90°、水平/垂直翻轉、複製目前效果、另存、重設、開啟滑鼠穿透或關閉。
- 若貼圖開啟滑鼠穿透後無法點選，從系統匣選「取消所有滑鼠穿透」。
- 貼圖只保存在記憶體；離開 MySnip 後不會保存或復原圖片。

## 隱私與設定

- 不含帳號、遙測、雲端同步、更新檢查或任何網路功能。
- 設定寫入 `%LocalAppData%\MySnip\settings.json`。
- 損毀的設定檔會被更名為 `settings.json.corrupt-時間戳`，程式則恢復預設值。
- 開機啟動預設關閉；開啟後只寫入目前使用者的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- 解除安裝會清除開機啟動項與程式檔，但保留上述使用者設定。

## 從原始碼建置

需求：

- Windows 10/11 x64
- .NET SDK 10.0.111 或相容的 .NET 10 SDK
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

## 已知限制

- v0.1.0 不含截圖歷史、OCR、QR Code、滾動截圖、螢幕錄影、GIF 動畫控制或貼圖跨重啟復原。
- UI 子元素候選依賴目標程式提供的 Windows UI Automation 資訊；未提供時會退回最上層視窗，桌面空白處則可直接拖曳。
- MySnip 以一般使用者權限執行；不承諾在高權限/系統桌面上攔截快捷鍵或擷取內容。
- 截圖、選取與標註座標全程使用實體像素。截圖覆蓋 HWND 透過 Win32 直接套用虛擬桌面的實體矩形，Canvas 再以目前 HWND DPI 的反向 LayoutTransform 抵銷 WPF DIP 縮放，因此 Canvas 1 單位等於 1 個實體像素；`WM_DPICHANGED` 時會重新套用映射。100% 雙螢幕已實機驗證，150%/200% 混合 DPI 的比例映射已有自動測試，仍建議發佈前在對應實體硬體上走查視覺精度。

第三方元件與授權請見 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
"# MySnip" 
