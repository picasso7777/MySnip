# MySnip - Python Edition

純 Python 的截圖與貼圖工具，等效於 C#/WPF 版的核心功能。

## 需求

- Python 3.10+
- Windows 10/11

## 安裝

```powershell
cd python
pip install -r requirements.txt
```

## 執行

```powershell
python mysnip.py
```

## 快捷鍵

| 按鍵 | 功能 |
|------|------|
| F1   | 截圖（拖曳選取區域） |
| F3   | 將剪貼簿圖片貼到螢幕 |
| Esc  | 取消截圖 / 關閉貼圖 |

## 截圖操作

1. 按 F1 後螢幕出現半透明覆蓋層
2. 拖曳滑鼠選取區域
3. 按 Enter → 貼圖到螢幕
4. 右鍵 → 選擇「複製到剪貼簿」或「存檔」

## 貼圖操作

- 拖曳移動
- 滾輪縮放
- Ctrl+滾輪調整透明度
- 右鍵選單：複製 / 另存 / 關閉
- Esc 關閉

## 系統匣

右下角系統匣圖示可快速截圖、貼上、開啟截圖資料夾、結束程式。

## 設定

設定檔位於 `%LOCALAPPDATA%\MySnip\settings.json`，可修改：

```json
{
  "capture_hotkey": "f1",
  "paste_hotkey": "f3",
  "output_directory": "C:\\Users\\你的名稱\\Pictures\\MySnip"
}
```

## 為什麼用 Python？

Python 版本不產生 .exe，防毒軟體不會誤判攔截。直接用 `python mysnip.py` 執行即可。
