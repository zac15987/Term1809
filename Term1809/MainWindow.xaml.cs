using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Term1809.Terminal;
using Microsoft.Terminal.Wpf;

namespace Term1809 {
	/// <summary>
	/// Tabbed terminal host with a Windows-Terminal-style custom title bar: the window is an
	/// <see cref="HandyControl.Controls.Window"/> (WindowWin10 style) and the tab strip lives in
	/// its non-client area. Each tab owns a live <see cref="PtyTerminalControl"/> that stays in
	/// <c>TerminalHost</c> for the lifetime of the tab; the HandyControl TabControl is used only as
	/// the header strip. Switching tabs toggles Visibility (never unloads), because the terminal
	/// renders in a native HWND whose keyboard hook is installed on Load and removed on Unload.
	/// </summary>
	public partial class MainWindow : HandyControl.Controls.Window {

		public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();

		private readonly DataBinds binds = new();
		private DispatcherTimer _exitPoll;
		private int _tabCounter;
		private string _titleBeforeEdit;

		public MainWindow() {
			InitializeComponent();
			DataContext = binds;
			Loaded += (_, _) => {
				if (Tabs.Count == 0) CreateTab();
				StartExitPoll();
			};
		}

		private TerminalTabViewModel Active => tabControl.SelectedItem as TerminalTabViewModel;

		// ---- Tab lifecycle -------------------------------------------------

		private TerminalTabViewModel CreateTab(string startupCommand = null, string title = null) {
			startupCommand ??= binds.StartupCommand;

			var ctrl = new PtyTerminalControl {
				StartupCommandLine = startupCommand,
				Theme = binds.Theme,
				FontFamilyWhenSettingTheme = new FontFamily("Cascadia Mono NF"),
				FontSizeWhenSettingTheme = 12,
				LogOutput = true, // required for "Export Text" / "Show Buffer"
				Win32InputMode = false,
				NavigationCapture = NavigationCapture.Tab | NavigationCapture.Arrows,
				Visibility = Visibility.Collapsed,
			};
			TerminalHost.Children.Add(ctrl); // added to the loaded tree -> Loaded fires -> PTY starts

			var vm = new TerminalTabViewModel(ctrl, title ?? $"pwsh {++_tabCounter}", startupCommand, DuplicateTab, ExportTab);
			Tabs.Add(vm);
			tabControl.SelectedItem = vm; // fires SelectionChanged -> visibility + focus
			RefreshTabStrip();
			return vm;
		}

		/// <summary>
		/// Heals HandyControl's TabPanel measure cache. TabPanel.Loaded re-measures itself using
		/// its own DesiredSize as the constraint; our first tab is created in Window.Loaded, i.e.
		/// AFTER the panel already measured empty, so that self-measure clamps the panel to width 0
		/// and the (no longer measure-dirty) parents never re-measure it with a real constraint.
		/// Invalidating the panel and its ancestors up to the TabControl forces a clean pass.
		/// </summary>
		private void RefreshTabStrip() {
			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
				DependencyObject d = FindVisualChild<HandyControl.Controls.TabPanel>(tabControl);
				while (d is UIElement el) {
					el.InvalidateMeasure();
					if (ReferenceEquals(d, tabControl)) break;
					d = VisualTreeHelper.GetParent(d);
				}
			}));
		}

		private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject {
			if (root == null) return null;
			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
				var child = VisualTreeHelper.GetChild(root, i);
				if (child is T hit) return hit;
				if (FindVisualChild<T>(child) is T deeper) return deeper;
			}
			return null;
		}

		/// <summary>Programmatic close (shell exit). Removes the VM ourselves; X-button uses TabItem_Closed.</summary>
		private void CloseTab(TerminalTabViewModel vm) {
			if (vm == null || !Tabs.Contains(vm)) return; // idempotent
			DisposeTabTerminal(vm);
			Tabs.Remove(vm); // triggers SelectionChanged (neighbor select / empty -> close)
		}

		/// <summary>Kill the PTY and unload the control. Safe to call twice.</summary>
		private void DisposeTabTerminal(TerminalTabViewModel vm) {
			if (vm == null || vm.Disposed) return;
			vm.Disposed = true;
			try { vm.Term?.CloseInput(); } catch { }
			try { vm.Term?.KillProcess(); } catch { }
			if (TerminalHost.Children.Contains(vm.Control))
				TerminalHost.Children.Remove(vm.Control); // Unloaded -> keyboard hook removed
		}

		// X button: HandyControl removes the item from Tabs itself right after this event.
		// The Closed routed event is declared with the EventHandler delegate (object, EventArgs).
		private void TabItem_Closed(object sender, EventArgs e) {
			if ((sender as FrameworkElement)?.DataContext is TerminalTabViewModel vm)
				DisposeTabTerminal(vm);
		}

		private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) {
			if (!ReferenceEquals(e.OriginalSource, tabControl)) return; // ignore bubbled inner selectors

			foreach (var vm in Tabs)
				vm.Control.Visibility = ReferenceEquals(vm, tabControl.SelectedItem)
					? Visibility.Visible : Visibility.Collapsed;

			if (Tabs.Count == 0) { Close(); return; } // last tab closed -> close window

			if (tabControl.SelectedItem is TerminalTabViewModel sel)
				FocusTerminal(sel.Control);
		}

		// ---- Rename --------------------------------------------------------

		private void TabItem_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
			if ((sender as FrameworkElement)?.DataContext is TerminalTabViewModel vm) {
				_titleBeforeEdit = vm.Title;
				vm.IsEditing = true;
			}
		}

		private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) {
			if (sender is TextBox tb && tb.IsVisible) {
				tb.Focus();
				tb.SelectAll();
			}
		}

		private void RenameBox_KeyDown(object sender, KeyEventArgs e) {
			if (sender is not TextBox tb || tb.DataContext is not TerminalTabViewModel vm) return;
			if (e.Key == Key.Enter) {
				vm.IsEditing = false;
				e.Handled = true;
				FocusTerminal(Active?.Control);
			} else if (e.Key == Key.Escape) {
				vm.Title = _titleBeforeEdit;
				vm.IsEditing = false;
				e.Handled = true;
				FocusTerminal(Active?.Control);
			}
		}

		private void RenameBox_LostFocus(object sender, RoutedEventArgs e) {
			if (sender is TextBox tb && tb.DataContext is TerminalTabViewModel vm)
				vm.IsEditing = false;
		}

		// ---- Context-menu commands ----------------------------------------

		private void DuplicateTab(TerminalTabViewModel vm) =>
			CreateTab(vm.StartupCommand, vm.Title + " (copy)");

		private void ExportTab(TerminalTabViewModel vm) {
			var text = vm.Term?.GetBufferText() ?? string.Empty;
			var dlg = new Microsoft.Win32.SaveFileDialog {
				Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
				FileName = MakeSafeFileName(vm.Title) + ".txt",
			};
			if (dlg.ShowDialog(this) == true)
				File.WriteAllText(dlg.FileName, text);
		}

		private static string MakeSafeFileName(string name) {
			foreach (var c in Path.GetInvalidFileNameChars())
				name = name.Replace(c, '_');
			return string.IsNullOrWhiteSpace(name) ? "terminal" : name;
		}

		// ---- Toolbar (acts on the active tab) -----------------------------

		private void NewTabClicked(object sender, RoutedEventArgs e) => CreateTab();

		/// <summary>Title-bar "v" button: opens its ContextMenu as a dropdown (Windows-Terminal style).</summary>
		private void MenuButtonClicked(object sender, RoutedEventArgs e) {
			if (sender is not Button btn || btn.ContextMenu == null) return;
			btn.ContextMenu.PlacementTarget = btn;
			btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
			btn.ContextMenu.IsOpen = true;
		}

		private void ClearTermClicked(object sender, RoutedEventArgs e) {
			Active?.Term?.ClearScreen();
			FocusTerminal(Active?.Control);
		}

		private void RestartClicked(object sender, RoutedEventArgs e) {
			Active?.Control?.RestartConnection();
			FocusTerminal(Active?.Control);
		}

		private void ShowBufferClicked(object sender, RoutedEventArgs e) {
			var term = Active?.Term;
			if (term == null) return;
			if (Keyboard.IsKeyDown(Key.LeftShift))
				term.OutputLog?.Clear();
			else
				MessageBox.Show(term.GetBufferText());
			FocusTerminal(Active?.Control);
		}

		private void ShowProcessOutputClicked(object sender, RoutedEventArgs e) =>
			new ProcessOutput().Show();

		// ---- Helpers -------------------------------------------------------

		private async void FocusTerminal(PtyTerminalControl ctrl) {
			if (ctrl == null) return;
			await Task.Delay(50); // give the freshly-shown HWND time to settle (matches old RefocusKB)
			ctrl.Focus();
			Keyboard.Focus(ctrl);
		}

		private void StartExitPoll() {
			_exitPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
			_exitPoll.Tick += (_, _) => {
				for (int i = Tabs.Count - 1; i >= 0; i--) {
					var vm = Tabs[i];
					if (vm.IsEditing) continue; // don't yank a tab mid-rename
					if (vm.Term?.Process?.HasExited == true)
						CloseTab(vm);
				}
			};
			_exitPoll.Start();
		}

		protected override void OnClosing(CancelEventArgs e) {
			_exitPoll?.Stop();
			foreach (var vm in Tabs) {
				try { vm.Term?.CloseInput(); } catch { }
				try { vm.Term?.KillProcess(); } catch { }
			}
			base.OnClosing(e);
		}

		// ---- Theme / startup config (WPF-only) ----------------------------

		public class DataBinds : INotifyPropertyChanged {
			public void TriggerPropChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

			public string StartupCommand => "pwsh.exe";

			private static readonly Color BackroundColor = Color.FromArgb(255, 12, 12, 12);
			private static readonly Color ForegroundColor = Color.FromArgb(255, 204, 204, 204);

			public event PropertyChangedEventHandler PropertyChanged;

			public SolidColorBrush BackroundColorBrush => new(BackroundColor);

			public TerminalTheme Theme { get; set; } = new() {
				DefaultBackground = PtyTerminalControl.ColorToColorRef(BackroundColor),
				DefaultForeground = PtyTerminalControl.ColorToColorRef(ForegroundColor),
				DefaultSelectionBackground = 0xcccccc,
				CursorStyle = CursorStyle.BlinkingBar,
				ColorTable = new uint[] { 0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1, 0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC, 0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9, 0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2 },
			};
		}
	}
}
