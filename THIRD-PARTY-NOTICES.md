# 第三方元件與授權

MySnip 的產品程式碼為獨立實作，未複製或散布 Snipaste 的程式碼、圖示、文字或其他資產。

## 執行階段

- Microsoft .NET / WPF Runtime — MIT License；self-contained 發佈內容包含 Microsoft .NET 執行階段檔案。授權文字與公告請參閱 [.NET Runtime repository](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) 及隨發佈檔案所附的授權內容。
- Windows API / Windows Forms / WPF — 由目標 Windows 作業系統與 .NET 提供。

## 開發與打包工具

- xUnit.net 2.9.3 — Apache License 2.0；僅用於測試，不包含於使用者安裝內容。
- Microsoft.NET.Test.Sdk 18.0.1 — MIT License；僅用於測試。
- xunit.runner.visualstudio 3.1.5 — Apache License 2.0；僅用於測試。
- Inno Setup 6 — 自有授權；僅用於產生安裝程式。詳見 [Inno Setup License](https://jrsoftware.org/files/is/license.txt)。

套件版本以各專案檔與 `obj/project.assets.json` 的實際還原結果為準。
