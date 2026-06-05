using System;
using System.ComponentModel;
using System.Windows.Input;

using Term1809.Terminal;

namespace Term1809 {

	/// <summary>
	/// One terminal tab: owns a live <see cref="PtyTerminalControl"/> that stays in the
	/// host grid for the lifetime of the tab (the native HWND must never be unloaded), plus
	/// the editable header state and the per-tab context-menu commands.
	/// </summary>
	public sealed class TerminalTabViewModel : INotifyPropertyChanged {

		public PtyTerminalControl Control { get; }
		public ConPtyConnection Term => Control.Connection;

		/// <summary>The command this tab was launched with, reused by "Duplicate Tab".</summary>
		public string StartupCommand { get; }

		/// <summary>Guards against disposing the same tab's PTY twice (X-button vs. exit-poll).</summary>
		public bool Disposed { get; set; }

		public TerminalTabViewModel(
			PtyTerminalControl control,
			string title,
			string startupCommand,
			Action<TerminalTabViewModel> onDuplicate,
			Action<TerminalTabViewModel> onExport) {

			Control = control;
			_title = title;
			StartupCommand = startupCommand;
			DuplicateCommand = new RelayCommand(() => onDuplicate?.Invoke(this));
			ExportTextCommand = new RelayCommand(() => onExport?.Invoke(this));
		}

		private string _title;
		public string Title {
			get => _title;
			set {
				if (_title == value) return;
				_title = value;
				OnPropertyChanged(nameof(Title));
			}
		}

		private bool _isEditing;
		/// <summary>True while the header is being renamed (swaps TextBlock for a TextBox).</summary>
		public bool IsEditing {
			get => _isEditing;
			set {
				if (_isEditing == value) return;
				_isEditing = value;
				OnPropertyChanged(nameof(IsEditing));
			}
		}

		public ICommand DuplicateCommand { get; }
		public ICommand ExportTextCommand { get; }

		public event PropertyChangedEventHandler PropertyChanged;
		private void OnPropertyChanged(string prop) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
	}

	/// <summary>Minimal ICommand for binding context-menu items to view-model actions.</summary>
	public sealed class RelayCommand : ICommand {
		private readonly Action _execute;
		private readonly Func<bool> _canExecute;

		public RelayCommand(Action execute, Func<bool> canExecute = null) {
			_execute = execute ?? throw new ArgumentNullException(nameof(execute));
			_canExecute = canExecute;
		}

		public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
		public void Execute(object parameter) => _execute();

		public event EventHandler CanExecuteChanged {
			add => CommandManager.RequerySuggested += value;
			remove => CommandManager.RequerySuggested -= value;
		}
	}
}
