# MySnip 0.1.0 測試結果

測試日期：2026-08-12  
建置環境：Windows x64、.NET SDK 10.0.111、Inno Setup 6.7.3

## 自動測試

- Release 結果：48 通過、0 失敗、0 略過。
- TRX：`artifacts/MySnip-tests.trx`。
- 覆蓋項目：負座標與 DPI 座標換算、100%/150%/200% 覆蓋視窗反向 DPI 映射、範圍 clamp/move、四角 resize、Undo/Redo、設定預設/往返/損毀/不支援 schema/IO fallback、功能鍵範圍與重複驗證、貼圖 transform clamp。

## 發佈與安裝 smoke test

- `build-release.ps1`：成功完成 restore、Release test、win-x64 self-contained publish、Inno Setup compile。
- 安裝程式：`dist/MySnip-Setup-x64.exe`，產品版本 0.1.0，約 51.03 MiB。
- SHA-256：`4A5EA47DAA6BE797E3175CE6CA207A13B2BAC440C447BA1926525D9BB35705A5`。
- `/VERYSILENT` 安裝：exit code 0；確認 `MySnip.exe` 存在且版本為 0.1.0。
- `/VERYSILENT` 解除安裝：exit code 0；確認測試安裝目錄已移除。
- 第二執行個體：已確認不持有 mutex 的程序正常以 exit code 0 結束、僅保留一個 MySnip 程序；Win32 `EnumWindows` 確認第一執行個體顯示可見的「MySnip 設定」視窗。
- 雙螢幕 100% overlay probe：目前虛擬桌面 `0,0,3840,1080`，Win32 `GetWindowRect` 回傳 `0,0,3840,1080`；WPF DPI scale 與 inverse layout scale 皆為 1.0000，Canvas 最終實體比例為 1.0000；probe exit code 0。

## 尚待實機驗收

- Windows 10 22H2 與乾淨 Windows 11 VM 的互相比對、覆蓋升級與 SmartScreen 流程。
- 雙螢幕 100%/150%/200% 混合 DPI、負座標、跨螢幕截圖的視覺精度。
- 所有工具逐一人工標註、剪貼簿/PNG/JPEG、貼圖旋轉/翻轉/穿透操作。
- 100 次截圖與同時 20 張貼圖的長時間 GDI Handle / memory 壓力觀察。
