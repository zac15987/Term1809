using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Term1809.Terminal.Internals;
using Microsoft.Terminal.Wpf;


namespace Term1809.Terminal {
	/// <summary>
	/// Flags controlling which navigation keys the control keeps from WPF focus navigation
	/// (forwarding them to the hosted console instead of letting WPF consume them).
	/// </summary>
	[Flags]
	[System.ComponentModel.TypeConverter(typeof(System.ComponentModel.EnumConverter))]
	public enum NavigationCapture { None = 1 << 0, Tab = 1 << 1, Arrows = 1 << 2 };

	public class PtyTerminalControl : UserControl {
		private delegate IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern IntPtr SetWindowsHookEx(int idHook, KeyboardHookProc lpfn, IntPtr hMod, uint dwThreadId);
		[DllImport("user32.dll")]
		private static extern bool UnhookWindowsHookEx(IntPtr hhk);
		[DllImport("user32.dll")]
		private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
		[DllImport("user32.dll")]
		private static extern IntPtr GetFocus();
		[DllImport("kernel32.dll")]
		private static extern uint GetCurrentThreadId();
		[DllImport("kernel32.dll")]
		private static extern bool AttachConsole(uint dwProcessId);
		[DllImport("kernel32.dll")]
		private static extern bool FreeConsole();
		[DllImport("kernel32.dll")]
		private static extern IntPtr GetStdHandle(int nStdHandle);
		[DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", CharSet = CharSet.Unicode)]
		private static extern bool WriteConsoleInput(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);
		[DllImport("shell32.dll")]
		private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);
		[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
		private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, char[] lpszFile, uint cch);
		[DllImport("shell32.dll")]
		private static extern void DragFinish(IntPtr hDrop);
		[DllImport("imm32.dll")]
		private static extern IntPtr ImmGetContext(IntPtr hWnd);
		[DllImport("imm32.dll")]
		private static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref COMPOSITIONFORM lpCompForm);
		[DllImport("imm32.dll")]
		private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);
		[DllImport("user32.dll")]
		private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

		[StructLayout(LayoutKind.Sequential)]
		private struct COMPOSITIONFORM {
			public uint dwStyle;
			public POINT ptCurrentPos;
			public RECT rcArea;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct POINT {
			public int x;
			public int y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct RECT {
			public int left, top, right, bottom;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct CONSOLE_COORD {
			public short X;
			public short Y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SMALL_RECT {
			public short Left, Top, Right, Bottom;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct CONSOLE_SCREEN_BUFFER_INFO {
			public CONSOLE_COORD dwSize;
			public CONSOLE_COORD dwCursorPosition;
			public ushort wAttributes;
			public SMALL_RECT srWindow;
			public CONSOLE_COORD dwMaximumWindowSize;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct KEY_EVENT_RECORD {
			public int bKeyDown;
			public ushort wRepeatCount;
			public ushort wVirtualKeyCode;
			public ushort wVirtualScanCode;
			public char uChar;
			public uint dwControlKeyState;
		}

		[StructLayout(LayoutKind.Explicit)]
		private struct INPUT_RECORD {
			[FieldOffset(0)] public ushort EventType;
			[FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
		}

		private IntPtr _keyboardHook;
		private KeyboardHookProc _hookProc; // prevent GC collection
		private IntPtr _terminalHwnd;
		private uint _childPid;

		/// <summary>
		/// Packs the RGB channels of <paramref name="color"/> into a Win32 COLORREF
		/// (0x00BBGGRR layout). COLORREF has no alpha channel, so the alpha component is dropped.
		/// </summary>
		public static uint ColorToColorRef(Color color) => BitConverter.ToUInt32(new byte[] { color.R, color.G, color.B, 0 }, 0);

		private static void InputCaptureChanged(DependencyObject target, DependencyPropertyChangedEventArgs e) {
			var cntrl = target as PtyTerminalControl;
			cntrl.SetKBCaptureOptions();
		}
		private void SetKBCaptureOptions() {
			KeyboardNavigation.SetTabNavigation(this, this.NavigationCapture.HasFlag(NavigationCapture.Tab) ? KeyboardNavigationMode.Contained : KeyboardNavigationMode.Continue);
			KeyboardNavigation.SetDirectionalNavigation(this, this.NavigationCapture.HasFlag(NavigationCapture.Arrows) ? KeyboardNavigationMode.Contained : KeyboardNavigationMode.Continue);
		}
		// WPF intercepts Tab via IKeyboardInputSink.TranslateAccelerator before it reaches
		// the native terminal HWND. A WH_KEYBOARD thread hook fires before any WPF processing.
		// ConPTY's pipe splits VT escape sequences into individual key events, so we use
		// WriteConsoleInput to write KEY_EVENT_RECORDs directly to the console input buffer,
		// preserving modifier keys (e.g. Shift+Tab).
		private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
			if (nCode >= 0 && _terminalHwnd != IntPtr.Zero && GetFocus() == _terminalHwnd) {
				int vk = (int)wParam;
				bool keyUp = ((ulong)(long)lParam & 0x80000000) != 0;

				// Tab key: inject as KEY_EVENT_RECORD via WriteConsoleInput
				if (vk == 0x09 /*VK_TAB*/ && _childPid != 0 && this.NavigationCapture.HasFlag(NavigationCapture.Tab)) {
					bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
					if (AttachConsole(_childPid)) {
						try {
							var handle = GetStdHandle(-10 /*STD_INPUT_HANDLE*/);
							var rec = new INPUT_RECORD {
								EventType = 1, // KEY_EVENT
								KeyEvent = new KEY_EVENT_RECORD {
									bKeyDown = keyUp ? 0 : 1,
									wRepeatCount = 1,
									wVirtualKeyCode = 9, // VK_TAB
									wVirtualScanCode = 15,
									uChar = keyUp ? '\0' : '\t',
									dwControlKeyState = shift ? 0x10u /*SHIFT_PRESSED*/ : 0u
								}
							};
							WriteConsoleInput(handle, new[] { rec }, 1, out _);
						} finally {
							FreeConsole();
						}
					}
					return (IntPtr)1;
				}

				// Ctrl+V: paste clipboard text into the terminal
				if (vk == 0x56 /*VK_V*/ && (Keyboard.Modifiers & ModifierKeys.Control) != 0) {
					if (!keyUp && Clipboard.ContainsText()) {
						var text = Clipboard.GetText();
						if (!string.IsNullOrEmpty(text))
							Connection?.SendInput(text);
					}
					return (IntPtr)1;
				}

				// Ctrl+C: copy selected text if selection is active, otherwise pass through as terminal interrupt
				if (vk == 0x43 /*VK_C*/ && (Keyboard.Modifiers & ModifierKeys.Control) != 0) {
					if (!keyUp) {
						var selected = RenderControl?.GetSelectedText();
						if (!string.IsNullOrEmpty(selected)) {
							Clipboard.SetText(selected);
							return (IntPtr)1;
						}
					} else {
						// suppress key-up if we handled key-down as copy (selection may have been cleared already)
						// let it through — the terminal needs the natural Ctrl+C key-up
					}
				}
			}
			return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
		}

		private void InstallKeyboardHook() {
			if (_keyboardHook != IntPtr.Zero) return;
			_hookProc = KeyboardHookCallback;
			_keyboardHook = SetWindowsHookEx(2 /*WH_KEYBOARD*/, _hookProc, IntPtr.Zero, GetCurrentThreadId());
		}

		private void UninstallKeyboardHook() {
			if (_keyboardHook != IntPtr.Zero) {
				UnhookWindowsHookEx(_keyboardHook);
				_keyboardHook = IntPtr.Zero;
			}
		}

		private void CaptureTerminalHwnd() {
			var hwndHost = FindVisualChild<HwndHost>(RenderControl);
			if (hwndHost != null) {
				_terminalHwnd = hwndHost.Handle;
				// Enable file drag-and-drop on the native terminal HWND (WPF drag events
				// don't fire over HwndHost due to HWND airspace).
				DragAcceptFiles(_terminalHwnd, true);
				hwndHost.MessageHook += OnTerminalMessage;
			}
		}

		private void PositionImeCompositionWindow(IntPtr hwnd) {
			if (_childPid == 0) return;

			var hIMC = ImmGetContext(hwnd);
			if (hIMC == IntPtr.Zero) return;

			try {
				int cursorCol = 0, cursorRow = 0, viewportTop = 0;
				if (AttachConsole(_childPid)) {
					try {
						var hOutput = GetStdHandle(-11 /*STD_OUTPUT_HANDLE*/);
						if (hOutput != IntPtr.Zero && GetConsoleScreenBufferInfo(hOutput, out var csbi)) {
							cursorCol = csbi.dwCursorPosition.X;
							cursorRow = csbi.dwCursorPosition.Y;
							viewportTop = csbi.srWindow.Top;
						}
					} finally {
						FreeConsole();
					}
				}

				int columns = RenderControl?.Columns ?? 80;
				int rows = RenderControl?.Rows ?? 24;
				GetClientRect(hwnd, out var clientRect);
				int clientWidth = clientRect.right - clientRect.left;
				int clientHeight = clientRect.bottom - clientRect.top;

				int charWidth = columns > 0 ? clientWidth / columns : 8;
				int charHeight = rows > 0 ? clientHeight / rows : 16;

				int pixelX = cursorCol * charWidth;
				int pixelY = (cursorRow - viewportTop) * charHeight;

				const uint CFS_POINT = 0x0002;
				var cf = new COMPOSITIONFORM {
					dwStyle = CFS_POINT,
					ptCurrentPos = new POINT { x = pixelX, y = pixelY },
				};
				ImmSetCompositionWindow(hIMC, ref cf);
			} finally {
				ImmReleaseContext(hwnd, hIMC);
			}
		}

		private IntPtr OnTerminalMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
			const int WM_IME_STARTCOMPOSITION = 0x010D;
			const int WM_IME_COMPOSITION = 0x010F;
			const int WM_DROPFILES = 0x0233;
			if (msg == WM_IME_STARTCOMPOSITION || msg == WM_IME_COMPOSITION) {
				PositionImeCompositionWindow(hwnd);
			} else if (msg == WM_DROPFILES && Connection != null) {
				uint count = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
				var files = new string[count];
				for (uint i = 0; i < count; i++) {
					uint len = DragQueryFile(wParam, i, null, 0);
					var buf = new char[len + 1];
					DragQueryFile(wParam, i, buf, (uint)buf.Length);
					files[i] = new string(buf, 0, (int)len);
				}
				DragFinish(wParam);
				var paths = string.Join(" ", files.Select(f => f.Contains(' ') ? $"\"{f}\"" : f));
				Connection.SendInput(paths);
				handled = true;
			}
			return IntPtr.Zero;
		}

		private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
				var child = VisualTreeHelper.GetChild(parent, i);
				if (child is T t) return t;
				var found = FindVisualChild<T>(child);
				if (found != null) return found;
			}
			return null;
		}

		/// <summary>
		/// Controls which keyboard navigation inputs are captured and forwarded to the terminal
		/// rather than allowing WPF to consume them for focus traversal.
		/// </summary>
		public NavigationCapture NavigationCapture {
			get => (NavigationCapture)GetValue(NavigationCaptureProperty);
			set => SetValue(NavigationCaptureProperty, value);
		}

		[Description("Applies a color theme to the terminal. Setter only."), Category("Common")]
		public TerminalTheme? Theme { set => SetTheme(_Theme = value); private get => _Theme; }
		private TerminalTheme? _Theme;
		private void SetTheme(TerminalTheme? v) { if (v != null) RenderControl?.SetTheme(v.Value, FontFamilyWhenSettingTheme.Source, (short)FontSizeWhenSettingTheme); }

		[Description("When set, keyboard input from the UI is blocked. Code can still push input via Connection.SendInput. Setter only."), Category("Common")]
		public bool? IsReadOnly { set => SetReadOnly(_IsReadOnly = value); private get => _IsReadOnly; }
		private bool? _IsReadOnly;
		private void SetReadOnly(bool? v) { if (v != null) Connection?.SetReadOnly(v.Value, false); }

		[Description("Toggles whether the text caret is drawn in the terminal. Setter only."), Category("Common")]
		public bool? IsCursorVisible { set => SetCursor(_IsCursorVisible = value); private get => _IsCursorVisible; }
		private bool? _IsCursorVisible;
		private void SetCursor(bool? v) { if (v != null) Connection?.SetCursorVisibility(v.Value); }

		/// <summary>
		/// The UI terminal rendering control. Read-only; set by <see cref="InitializeComponent"/>.
		/// </summary>
		[Description("The underlying rendering surface that paints terminal output.")]
		public TerminalControl RenderControl {
			get => (TerminalControl)GetValue(RenderControlPropertyKey.DependencyProperty);
			private set => SetValue(RenderControlPropertyKey, value);
		}

		private static void OnTermChanged(DependencyObject target, DependencyPropertyChangedEventArgs e) {
			if (target is not PtyTerminalControl control)
				return;
			if (e.NewValue is not ConPtyConnection connection)
				return;

			// If the render surface is already loaded, run its load handler straight away.
			if (control.RenderControl.IsLoaded)
				control.Terminal_Loaded(control.RenderControl, null);

			// Hook readiness: fire now if the session is already up, otherwise wait for Ready.
			if (connection.IsStarted)
				control.Term_TermReady(connection, null);
			else
				connection.Ready += control.Term_TermReady;
		}

		/// <summary>
		/// The backend <see cref="ConPtyConnection"/>. Assign to connect the control to an existing session,
		/// or leave as the default to let the control create and start its own.
		/// </summary>
		[Description("The backing session. Swap it to point the control at a different running process.")]
		public ConPtyConnection Connection {
			get => (ConPtyConnection)GetValue(ConnectionProperty);
			set => SetValue(ConnectionProperty, value);
		}

		/// <summary>
		/// Detaches the current <see cref="Connection"/> from this control and returns it.
		/// The caller is responsible for disposing it if no longer needed.
		/// </summary>
		public ConPtyConnection DetachConnection() {
			if (RenderControl != null)
				RenderControl.Connection = null;
			if (Connection != null)
				Connection.Ready -= Term_TermReady;
			var ret = Connection;
			Connection = null;
			return ret;
		}

		public string StartupCommandLine {
			get => (string)GetValue(StartupCommandLineProperty);
			set => SetValue(StartupCommandLineProperty, value);
		}

		/// <summary>
		/// The working directory the terminal process starts in. Defaults to the user profile directory (like launching PowerShell directly). Set to null to inherit the host app's working directory.
		/// </summary>
		public string StartupWorkingDirectory {
			get => (string)GetValue(StartupWorkingDirectoryProperty);
			set => SetValue(StartupWorkingDirectoryProperty, value);
		}

		public bool LogOutput {
			get => (bool)GetValue(LogOutputProperty);
			set => SetValue(LogOutputProperty, value);
		}

		/// <summary>
		/// When enabled, keyboard input is delivered to ConPTY via the win32-input-mode extended
		/// key-event protocol, which preserves modifiers and control sequences more faithfully
		/// than the default encoding. Protocol spec:
		/// https://github.com/microsoft/terminal/blob/main/doc/specs/%234999%20-%20Improved%20keyboard%20handling%20in%20Conpty.md
		/// </summary>
		public bool Win32InputMode {
			get => (bool)GetValue(Win32InputModeProperty);
			set => SetValue(Win32InputModeProperty, value);
		}

		public FontFamily FontFamilyWhenSettingTheme {
			get => (FontFamily)GetValue(FontFamilyWhenSettingThemeProperty);
			set => SetValue(FontFamilyWhenSettingThemeProperty, value);
		}

		public int FontSizeWhenSettingTheme {
			get => (int)GetValue(FontSizeWhenSettingThemeProperty);
			set => SetValue(FontSizeWhenSettingThemeProperty, value);
		}

		public PtyTerminalControl() {
			InitializeComponent();
			SetKBCaptureOptions();
		}

		private void InitializeComponent() {
			RenderControl = new();
			Connection = new();
			RenderControl.AutoResize = true;
			RenderControl.Loaded += Terminal_Loaded;
			var grid = new Grid();
			grid.Children.Add(RenderControl);
			this.Content = grid;
			Focusable = true;
			RenderControl.Focusable = true;
			this.GotFocus += (_, _) => RenderControl.Focus();
			Loaded += (_, _) => { CaptureTerminalHwnd(); InstallKeyboardHook(); };
			Unloaded += (_, _) => UninstallKeyboardHook();
		}

		void MainThreadRun(Action action) => Dispatcher.Invoke(action);

		private void Term_TermReady(object sender, EventArgs e) {
			MainThreadRun(() => {
				RenderControl.Connection = Connection;
				Connection.SetWin32InputMode(Win32InputMode);
				Connection.Resize(RenderControl.Columns, RenderControl.Rows);
				if (Connection?.Process is ChildProcessLauncher.LaunchedProcess wp)
					_childPid = (uint)wp.Pid;
			});
		}

		/// <summary>
		/// Replaces the current connection with <paramref name="replacement"/> (or a new
		/// <see cref="ConPtyConnection"/> if null), optionally disposing the old connection.
		/// </summary>
		/// <param name="replacement">Optional existing connection to switch to.</param>
		/// <param name="disposeExisting">True if the detached connection should be terminated.</param>
		public async Task RestartConnection(ConPtyConnection replacement = null, bool disposeExisting = true) {
			// DetachConnection unhooks the current session and hands it back to us.
			var detached = DetachConnection();
			if (disposeExisting && detached != null)
				ShutDownConnection(detached);

			Connection = replacement ?? new ConPtyConnection();
		}

		// Best-effort teardown of a detached session; either step may already be a no-op.
		private static void ShutDownConnection(ConPtyConnection connection) {
			try { connection.CloseInput(); } catch { }
			try { connection.KillProcess(); } catch { }
		}

		private void StartTerm(int column_width, int row_height) {
			if (Connection?.IsStarted != false)
				return;

			MainThreadRun(() => {
				var cmd = StartupCommandLine;
				var workingDir = StartupWorkingDirectory;
				var term = Connection;
				var logOutput = LogOutput;
				Task.Run(() => term.Start(cmd, column_width, row_height, logOutput, workingDirectory: workingDir));
			});
		}

		private async void Terminal_Loaded(object sender, RoutedEventArgs e) {
			await TermInit();
		}

		private async Task TermInit() {
			StartTerm(RenderControl.Columns, RenderControl.Rows);
			ApplyInitialState();

			// Cursor visibility can get reset while the session finishes coming up, so
			// re-apply it once after a short settle delay.
			await Task.Delay(1000);
			SetCursor(IsCursorVisible);
		}

		// Pushes the control's startup property values down into the live connection.
		private void ApplyInitialState() {
			SetTheme(Theme);
			SetCursor(IsCursorVisible);
			SetReadOnly(IsReadOnly);
		}

		#region Dependency Properties
		public static readonly DependencyProperty NavigationCaptureProperty = DependencyProperty.Register(nameof(NavigationCapture), typeof(NavigationCapture), typeof(PtyTerminalControl), new PropertyMetadata(NavigationCapture.Tab | NavigationCapture.Arrows, InputCaptureChanged));

		public static readonly DependencyProperty ThemeProperty = WriteOnlyDpFactory<PtyTerminalControl>.GenerateWriteOnlyProperty(c => c.Theme);
		protected static readonly DependencyPropertyKey RenderControlPropertyKey = DependencyProperty.RegisterReadOnly(nameof(RenderControl), typeof(TerminalControl), typeof(PtyTerminalControl), new PropertyMetadata());
		public static readonly DependencyProperty RenderControlProperty = RenderControlPropertyKey.DependencyProperty;
		public static readonly DependencyProperty ConnectionProperty = DependencyProperty.Register(nameof(Connection), typeof(ConPtyConnection), typeof(PtyTerminalControl), new(null, OnTermChanged));
		public static readonly DependencyProperty StartupCommandLineProperty = DependencyProperty.Register(nameof(StartupCommandLine), typeof(string), typeof(PtyTerminalControl), new PropertyMetadata("powershell.exe"));
		public static readonly DependencyProperty StartupWorkingDirectoryProperty = DependencyProperty.Register(nameof(StartupWorkingDirectory), typeof(string), typeof(PtyTerminalControl), new PropertyMetadata(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

		public static readonly DependencyProperty LogOutputProperty = DependencyProperty.Register(nameof(LogOutput), typeof(bool), typeof(PtyTerminalControl), new PropertyMetadata(false));
		public static readonly DependencyProperty Win32InputModeProperty = DependencyProperty.Register(nameof(Win32InputMode), typeof(bool), typeof(PtyTerminalControl), new PropertyMetadata(true));
		public static readonly DependencyProperty IsReadOnlyProperty = WriteOnlyDpFactory<PtyTerminalControl>.GenerateWriteOnlyProperty(c => c.IsReadOnly);
		public static readonly DependencyProperty IsCursorVisibleProperty = WriteOnlyDpFactory<PtyTerminalControl>.GenerateWriteOnlyProperty(c => c.IsCursorVisible);

		public static readonly DependencyProperty FontFamilyWhenSettingThemeProperty = DependencyProperty.Register(nameof(FontFamilyWhenSettingTheme), typeof(FontFamily), typeof(PtyTerminalControl), new PropertyMetadata(new FontFamily("Cascadia Code")));

		public static readonly DependencyProperty FontSizeWhenSettingThemeProperty = DependencyProperty.Register(nameof(FontSizeWhenSettingTheme), typeof(int), typeof(PtyTerminalControl), new PropertyMetadata(12));

		#endregion
	}
}
