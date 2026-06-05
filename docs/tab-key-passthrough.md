# Tab/Shift+Tab 按鍵穿透問題與解決方案

## 問題描述

在 WPF 應用程式中嵌入 PtyTerminalControl 時，Tab 和 Shift+Tab 無法傳遞到 terminal 內的應用程式（如 Claude Code）。按下 Shift+Tab 時，焦點會跳到 WPF 視窗中的其他控件（如按鈕），而不是送到 terminal。

## 根本原因（三層障礙）

### 1. WPF 焦點導航攔截 Tab

WPF 的 `IKeyboardInputSink.TranslateAccelerator` 在 `HwndSource.OnPreprocessMessage` 中攔截 Tab 鍵，用於焦點導航。這發生在以下所有機制之前：

- `PreviewKeyDown` routed event — 當原生 HWND 擁有焦點時不觸發（因為 Tab 在 WPF event 產生前就被消耗）
- `ComponentDispatcher.ThreadFilterMessage` — Tab 在 `ThreadPreprocessMessage` 階段被 HwndSource 消耗，而非 `ThreadFilterMessage`
- `HwndHost.MessageHook` — 訊息從未到達子視窗的 SubclassWndProc

### 2. ConPTY 拆分 VT Escape Sequence

即使成功將 `\x1b[Z`（CSI Z，標準 Shift+Tab VT 序列）寫入 ConPTY 的 input pipe，ConPTY 在 Windows 10 LTSC 2019 (build 17763) 上會將 escape sequence 拆成個別字元（ESC、`[`、`Z`），導致 Shift modifier 遺失。

驗證方式：寫入純文字 `HELLO` 可以成功到達應用程式輸入框，但 `\x1b[Z` 無法被正確識別為 Shift+Tab。

### 3. ENABLE_VIRTUAL_TERMINAL_INPUT 對 ConPTY pipe 無效

透過 `AttachConsole` + `SetConsoleMode` 設定 `ENABLE_VIRTUAL_TERMINAL_INPUT` (0x200) flag 雖然 API 回傳成功，但此 flag 只影響真實 console 視窗的鍵盤輸入轉換，不影響 ConPTY pipe 的資料處理方式。

## 嘗試過但失敗的方法

| 方法 | 失敗原因 |
|------|----------|
| `PreviewKeyDown` + `e.Handled = true` | 原生 HWND 擁有焦點時，WPF 不會為 Tab 產生 routed event |
| `ComponentDispatcher.ThreadFilterMessage` | Tab 在 `ThreadPreprocessMessage` 階段被消耗，`ThreadFilterMessage` 收不到 |
| `KeyboardNavigationMode.Contained` | 僅告訴 WPF 焦點導航範圍，不阻止 Tab 被攔截 |
| `WH_KEYBOARD` hook + `SendInput("\x1b[Z")` | ConPTY 將 escape sequence 拆成個別字元 |
| `WH_KEYBOARD` hook + `SendMessage(WM_KEYDOWN)` | terminal engine 產生的 VT 序列同樣被 ConPTY 拆分 |
| `WH_KEYBOARD` hook + Win32 input mode format | ConPTY 不認識 `\x1b[Vk;Sc;Uc;Kd;Cs;Rc_` 格式（未協商 Win32 input mode）|
| `AttachConsole` + `SetConsoleMode(ENABLE_VIRTUAL_TERMINAL_INPUT)` | 此 flag 不影響 ConPTY pipe 處理 |
| `Win32InputMode="True"` (XAML) | 導致所有鍵盤輸入失效，無法打字 |

## 最終解決方案：WH_KEYBOARD + WriteConsoleInput

### 原理

1. **WH_KEYBOARD thread hook** — 安裝在 UI 執行緒上，在 WPF 的 message loop 處理之前攔截鍵盤訊息
2. **AttachConsole** — 連接到子進程的 console
3. **WriteConsoleInput** — 直接寫入 `KEY_EVENT_RECORD` 到 console input buffer，完全繞過 ConPTY 的 VT input pipe 解析

### 流程

```
使用者按下 Shift+Tab
    ↓
WH_KEYBOARD hook 攔截 VK_TAB (在 WPF 處理前)
    ↓
AttachConsole(childPid) 連接子進程 console
    ↓
WriteConsoleInput 寫入 KEY_EVENT_RECORD:
  - wVirtualKeyCode = 9 (VK_TAB)
  - wVirtualScanCode = 15
  - dwControlKeyState = 0x10 (SHIFT_PRESSED)
    ↓
FreeConsole() 斷開 console
    ↓
return 1 吞掉原始訊息 (WPF 永遠看不到 Tab)
    ↓
應用程式收到完整的 Shift+Tab KEY_EVENT_RECORD
```

### 關鍵實作細節

- **取得 terminal HWND**：透過 `VisualTreeHelper` 找到 `HwndHost`，使用其 public `Handle` 屬性（非 reflection）
- **焦點判斷**：用 `GetFocus()` Win32 API 比對 terminal HWND，確保只在 terminal 擁有焦點時攔截
- **子進程 PID**：從 `ConPtyConnection.Process`（轉型為 `ChildProcessLauncher.LaunchedProcess`）取得 `Pid` 屬性
- **64 位元安全**：lParam 的 bit 31 檢查使用 `(ulong)(long)lParam` 避免 `IntPtr` 轉 `uint` 的 `OverflowException`
- **GC 防護**：`KeyboardHookProc` delegate 存在 `_hookProc` 欄位中，防止被垃圾回收

## 環境資訊

- Windows 10 Enterprise LTSC 2019 (build 17763)
- .NET 6.0 / .NET 8.0
- ConPTY: CI.Microsoft.Windows.Console.ConPTY 1.22.250314001
- Terminal: CI.Microsoft.Terminal.Wpf 1.22.250204002
