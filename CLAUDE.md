# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Term1809 is a WPF terminal emulator app (single project) that wraps Windows Terminal's ConPTY, providing full terminal emulation (ANSI/VT, 24-bit color, GPU-accelerated rendering, mouse support). The terminal-integration code lives in `Term1809/Terminal/` under the `Term1809.Terminal` namespace. Targets **Windows 10 Enterprise LTSC 2019 (build 17763)** compatibility, which constrains several input-handling decisions below.

## Build Commands

```powershell
# Build
dotnet build Term1809.sln -c Debug

# Run
dotnet run --project Term1809 -c Debug
```

There are no unit tests. Validate by running the app. Solution file: `Term1809.sln`.

## Architecture

```
PtyTerminalControl (UserControl — public API, XAML bindings, theming, input hooks)
        │
        ↓  ITerminalConnection
ConPtyConnection (bidirectional I/O, process lifecycle, input/output interceptors)
        │
        ↓  Pipes
ConPtyHandle + ChildProcessLauncher (conpty.dll P/Invoke, child process creation)
        │
        ↓  Native
Windows ConPTY + Terminal rendering engine (GPU-accelerated)
```

- **PtyTerminalControl** (`Term1809/Terminal/PtyTerminalControl.cs`) — Main terminal control. Exposes dependency properties for XAML binding (`StartupCommandLine`, `Theme`, `IsReadOnly`, `LogOutput`, `Win32InputMode`, etc.). Manages the connection between the UI rendering control and the ConPtyConnection backend. Also hosts the native input hooks (see below).
- **ConPtyConnection** (`Term1809/Terminal/ConPtyConnection.cs`) — Core pseudo-console manager implementing `ITerminalConnection`. Handles pipe-based I/O, async output reading, VT code stripping, and optional output logging. Provides `OutputInterceptor` and `InputInterceptor` delegate hooks for modifying data in transit.
- **DelimitedConPtyConnection** (`Term1809/Terminal/DelimitedConPtyConnection.cs`) — Subclass of ConPtyConnection that buffers output until a delimiter string is found, useful for capturing discrete command outputs.
- **Terminal/Internals/** — Low-level Windows API wrappers: `ConPtyHandle` (conpty handle), `ChildProcessLauncher` (child process with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`), `AnonymousPipePair` (anonymous pipes), `ConPtyNativeMethods` (P/Invoke signatures).

### Native input handling (Terminal/PtyTerminalControl.cs)

Because the terminal renders in a native HWND, several inputs need OS-level interception rather than WPF event handling:

- **Tab / Shift+Tab passthrough** — A `WH_KEYBOARD` thread hook fires before WPF focus navigation, then `AttachConsole` + `WriteConsoleInput` writes `KEY_EVENT_RECORD`s directly to the child's console input buffer (bypassing ConPTY's VT input pipe). See `docs/tab-key-passthrough.md` for the full investigation and why simpler approaches fail on build 17763.
- **Ctrl+C / Ctrl+V** — The keyboard hook copies selected text on Ctrl+C (falling back to sending the interrupt), and intercepts Ctrl+V to paste the clipboard into the terminal.
- **CJK IME** — `WM_IME_STARTCOMPOSITION` / `WM_IME_COMPOSITION` are handled to reposition the IME composition window at the terminal cursor via `ImmSetCompositionWindow`.
- **File drag-and-drop** — Handled via native `WM_DROPFILES` (`0x0233`), not WPF drop events.

### App UI

The app uses [HandyControl](https://github.com/HandyOrg/HandyControl) and a tabbed multi-terminal UI (`MainWindow` + `TerminalTabViewModel`).

### Key Design Decisions

- **Detachable PTY**: ConPtyConnection can be disconnected from one control and reattached to another at runtime (`DetachConnection()` / `RestartConnection(replacement)`), enabling terminal migration across windows.
- **Span-based I/O**: Hot paths use `ReadOnlySpan<char>` and `Span<char>` for zero-copy buffer handling.
- **HWND airspace**: The terminal renders in a native HWND, so WPF controls cannot overlay it (same limitation as WebView2).
- **CsWin32**: P/Invoke code is auto-generated from `NativeMethods.txt` (project root) via the `Microsoft.Windows.CsWin32` source generator.
- **conpty.dll explicit copy**: the ConPTY NuGet package ships its native dll under the legacy `win10-x64` RID, which .NET 8+ asset resolution doesn't map from `win-x64` — the csproj copies it explicitly; don't remove that item or the terminal won't start.

## Target Framework

`net10.0-windows10.0.19041.0`, x64, self-contained (`Directory.Build.targets` — deployment targets may not have the .NET runtime installed). There is no NuGet packaging or CI.
