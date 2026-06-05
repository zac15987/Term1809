using Microsoft.Terminal.Wpf;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Win32;
using Term1809.Terminal.Internals;

namespace Term1809.Terminal {
	/// <summary>
	/// Manages the lifecycle of a ConPTY session: pipe creation, child process launch,
	/// bidirectional I/O, and forwarding terminal output to the UI renderer.
	/// Implements <see cref="ITerminalConnection"/> for the Windows Terminal rendering engine.
	/// </summary>
	public class ConPtyConnection : ITerminalConnection {

		// ------------------------------------------------------------------
		// Design-mode sentinel (evaluated once at class load time)
		// ------------------------------------------------------------------

		private static readonly bool IsDesignMode =
			System.ComponentModel.DesignerProperties.GetIsInDesignMode(
				new System.Windows.DependencyObject());

		// ------------------------------------------------------------------
		// Configuration (set at construction, immutable thereafter)
		// ------------------------------------------------------------------

		private readonly bool USE_BINARY_WRITER;

		/// <summary>
		/// Backing size (in chars) of the output read buffer. Also serves as the release
		/// threshold for delimiter buffering in <see cref="DelimitedConPtyConnection"/>.
		/// </summary>
		protected readonly int OutputBufferSize;

		// ------------------------------------------------------------------
		// Input-pipe state
		// ------------------------------------------------------------------

		private SafeFileHandle _inputPipeWriteHandle;
		private StreamWriter _inputWriter;
		private BinaryWriter _inputWriterBinary;

		// ------------------------------------------------------------------
		// ConPTY session state
		// ------------------------------------------------------------------

		/// <summary>The active pseudo-console handle. Null until <see cref="Start(string,int,int,bool,IChildProcessLauncher,string)"/> completes setup.</summary>
		private ConPtyHandle _console;

		/// <summary>
		/// The child process launched for this session.
		/// Accessible so callers can inspect or force-kill the process.
		/// </summary>
		public IChildProcess Process { get; protected set; }

		/// <summary>Gets whether the child process has been launched.</summary>
		public bool IsStarted { get; private set; }

		/// <summary>Guards against starting the output-read loop more than once.</summary>
		public bool ReadLoopStarted = false;

		// ------------------------------------------------------------------
		// Output stream
		// ------------------------------------------------------------------

		/// <summary>Raw VT-100 byte stream from the child process's stdout side of the ConPTY.</summary>
		public FileStream OutputStream { get; private set; }

		// ------------------------------------------------------------------
		// Optional logging
		// ------------------------------------------------------------------

		/// <summary>
		/// When non-null, all terminal output (after interceptors run) is appended here.
		/// Enabled by passing <c>logOutput = true</c> to <see cref="Start(string,int,int,bool,IChildProcessLauncher,string)"/>.
		/// </summary>
		public StringBuilder OutputLog { get; private set; }

		// ------------------------------------------------------------------
		// VT-code stripping utilities
		// ------------------------------------------------------------------

		// The VT/control-character matcher is assembled from individually documented
		// fragments rather than a single opaque literal, so each alternative is auditable:
		private const string _csiSequence    = @"\x1b\[\??[0-9;]*[A-Za-z]"; // CSI: ESC '[' params final-letter
		private const string _oscTitlePrefix = @"\x1b\]0;";                  // OSC window-title introducer
		private const string _invisibleMarks = @"[\uFEFF\u200B\a\b]";        // BOM, zero-width space, bell, backspace

		/// <summary>
		/// Matches VT/ANSI escape sequences plus a handful of invisible characters
		/// (BOM, zero-width space, OSC title prefix, bell, backspace) so they can be stripped.
		/// </summary>
		public static readonly Regex colorStrip =
			new($"{_csiSequence}|{_oscTitlePrefix}|{_invisibleMarks}", RegexOptions.Compiled);

		/// <summary>
		/// Collapses any whitespace run that crosses more than one line break down to a single
		/// blank line. Applied after VT stripping in <see cref="GetBufferText"/>.
		/// </summary>
		private static readonly Regex NewlineReduce = new(@"\n\s*\n\s*");

		/// <summary>Strips VT/ANSI escape codes from <paramref name="str"/>.</summary>
		public static string StripVtCodes(string str) => colorStrip.Replace(str, "");

		/// <summary>
		/// Returns the accumulated output as clean plain text: optionally strips VT codes,
		/// removes carriage returns, collapses redundant blank lines, and trims surrounding whitespace.
		/// </summary>
		public string GetBufferText(bool stripVTCodes = true) {
			var raw = stripVTCodes
				? StripVtCodes(OutputLog.ToString())
				: OutputLog.ToString();
			return NewlineReduce.Replace(raw.Replace("\r", ""), "\n\n").Trim();
		}

		// ------------------------------------------------------------------
		// Events
		// ------------------------------------------------------------------

		/// <summary>Fired once the session is fully initialized and ready to accept input.</summary>
		public event EventHandler Ready;

		/// <summary>
		/// Fired whenever the child process emits output that should be forwarded to the terminal UI.
		/// </summary>
		public event EventHandler<TerminalOutputEventArgs> OutputReceived;

		/// <summary>
		/// Explicit <see cref="ITerminalConnection"/> output event. The Windows Terminal rendering
		/// engine subscribes here to receive output; subscriptions are forwarded to/from the
		/// public <see cref="OutputReceived"/> event so the renderer and external consumers share
		/// the same source.
		/// </summary>
		event EventHandler<TerminalOutputEventArgs> ITerminalConnection.TerminalOutput {
			add => OutputReceived += value;
			remove => OutputReceived -= value;
		}

		// ------------------------------------------------------------------
		// Interceptor delegates
		// ------------------------------------------------------------------

		/// <summary>
		/// Delegate type for intercepting and optionally rewriting data spans in transit.
		/// Set the span length to zero to suppress forwarding entirely.
		/// </summary>
		public delegate void SpanInterceptor(ref Span<char> data);

		/// <summary>
		/// Called with each chunk of output before it is forwarded to the UI terminal.
		/// Assign a handler to filter or transform the stream.
		/// </summary>
		public SpanInterceptor OutputInterceptor;

		/// <summary>
		/// Called with each string of keyboard input before it is written to the child process.
		/// Assign a handler to filter or transform keystrokes.
		/// </summary>
		public SpanInterceptor InputInterceptor;

		// ------------------------------------------------------------------
		// Read-only mode
		// ------------------------------------------------------------------

		/// <summary>
		/// When true, input arriving via <see cref="ITerminalConnection.WriteInput"/> is discarded
		/// (interceptors still fire). Direct calls to <see cref="SendInput"/> are unaffected.
		/// </summary>
		protected bool _ReadOnly;

		// ------------------------------------------------------------------
		// Default launcher (inner class)
		// ------------------------------------------------------------------

		/// <summary>
		/// Default <see cref="IChildProcessLauncher"/> that delegates to the static
		/// <see cref="ChildProcessLauncher.Start"/> helper.
		/// </summary>
		protected class DefaultChildProcessLauncher : IChildProcessLauncher {
			public IChildProcess Start(string command, nuint attributes, ConPtyHandle console, string workingDirectory = null)
				=> ChildProcessLauncher.Start(command, attributes, console, workingDirectory);
		}

		// ------------------------------------------------------------------
		// Construction
		// ------------------------------------------------------------------

		/// <summary>
		/// Initializes a new <see cref="ConPtyConnection"/> with the given buffer and writer settings.
		/// </summary>
		/// <param name="READ_BUFFER_SIZE">Size (in chars) of the output read buffer. Defaults to 16 KB.</param>
		/// <param name="USE_BINARY_WRITER">
		/// When true, a <see cref="BinaryWriter"/> is used for input instead of a <see cref="StreamWriter"/>.
		/// Required for callers that need raw byte-level control over the input stream.
		/// </param>
		/// <param name="childProcessLauncher">Unused; accepted for API compatibility.</param>
		public ConPtyConnection(int READ_BUFFER_SIZE = 1024 * 16, bool USE_BINARY_WRITER = false, IChildProcessLauncher childProcessLauncher = null) {
			this.OutputBufferSize = READ_BUFFER_SIZE;
			this.USE_BINARY_WRITER = USE_BINARY_WRITER;
		}

		// ------------------------------------------------------------------
		// Session startup
		// ------------------------------------------------------------------

		/// <summary>
		/// Parameterless overload: in design mode emits a placeholder prompt;
		/// otherwise re-enters the read loop for an already-started session.
		/// </summary>
		public void Start() {
			if (IsDesignMode) {
				EmitOutput("design-mode preview — terminal output appears here\r\n");
				return;
			}
			Task.Run(RunReadLoop);
		}

		/// <summary>
		/// Launches the child process and wires up all I/O for a new ConPTY session.
		/// Blocks until the child exits, then emits a session-ended notice.
		/// </summary>
		/// <param name="command">Command line to execute (e.g. <c>cmd.exe</c>).</param>
		/// <param name="consoleWidth">Initial viewport width in columns. Defaults to 80.</param>
		/// <param name="consoleHeight">Initial viewport height in rows. Defaults to 30.</param>
		/// <param name="logOutput">When true, all output is also appended to <see cref="OutputLog"/>.</param>
		/// <param name="factory">
		/// Optional custom launcher. Defaults to <see cref="DefaultChildProcessLauncher"/>.
		/// </param>
		/// <param name="workingDirectory">
		/// Working directory for the child process. Null inherits the current directory.
		/// </param>
		public void Start(string command, int consoleWidth = 80, int consoleHeight = 30, bool logOutput = false, IChildProcessLauncher factory = null, string workingDirectory = null) {
			if (Process != null)
				throw new Exception("This connection has already been started.");

			factory = factory ?? new DefaultChildProcessLauncher();

			// Short-circuit in the WPF designer: fire Ready and return without touching native APIs.
			if (IsDesignMode) {
				IsStarted = true;
				Ready?.Invoke(this, EventArgs.Empty);
				return;
			}

			if (logOutput)
				OutputLog = new StringBuilder();

			// ConPTY start sequence (order is API-dictated):
			//   1. Create input + output anonymous pipe pairs.
			//   2. Create the ConPTY, connecting the input read-side and output write-side.
			//   3. Launch the child process with the ConPTY attribute attached.
			//   4. Expose the output read-side as a FileStream for the read loop.
			using (var inputPipe = new AnonymousPipePair())
			using (var outputPipe = new AnonymousPipePair())
			using (var console = ConPtyHandle.Create(inputPipe.ReadSide, outputPipe.WriteSide, consoleWidth, consoleHeight))
			using (var process = factory.Start(command, PInvoke.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console, workingDirectory)) {
				Process = process;
				_console = console;

				// Expose the raw output stream before signaling readiness so that any Ready
				// handler that queries OutputStream sees a valid reference.
				OutputStream = new FileStream(outputPipe.ReadSide, FileAccess.Read);

				IsStarted = true;
				Ready?.Invoke(this, EventArgs.Empty);

				// Stash the input write-side and construct the appropriate writer.
				_inputPipeWriteHandle = inputPipe.WriteSide;
				var inputStream = new FileStream(_inputPipeWriteHandle, FileAccess.Write);
				if (!USE_BINARY_WRITER)
					_inputWriter = new StreamWriter(inputStream) { AutoFlush = true };
				else
					_inputWriterBinary = new BinaryWriter(inputStream);

				// Start draining output from the child process (synchronous — returns when done).
				RunReadLoop();

				// Register a CTRL_CLOSE cleanup handler so resources are freed if the console
				// window is closed via the title-bar X button.
				OnClose(() => DisposeResources(process, console, outputPipe, inputPipe, _inputWriter));

				process.WaitForExit();
				EmitOutput("-- session ended --");

				_console.Dispose();
			}
		}

		// ------------------------------------------------------------------
		// Input
		// ------------------------------------------------------------------

		/// <summary>
		/// Writes a span of characters to the child process's stdin pipe.
		/// </summary>
		/// <param name="input">Characters to deliver to the child process.</param>
		public void SendInput(ReadOnlySpan<char> input) {
			if (IsDesignMode)
				return;
			if (_console.IsDisposed)
				return;
			if (_inputWriter == null && _inputWriterBinary == null)
				throw new InvalidOperationException(
					"No input writer is available. Start the connection before sending input.");

			if (!USE_BINARY_WRITER)
				_inputWriter.Write(input);
			else
				SendInputBytes(Encoding.UTF8.GetBytes(input.ToString()));
		}

		/// <summary>
		/// Writes raw bytes to the child process's stdin pipe.
		/// When <see cref="USE_BINARY_WRITER"/> is false, the bytes are decoded as UTF-8 and
		/// forwarded via the text writer path instead.
		/// </summary>
		public void SendInputBytes(ReadOnlySpan<byte> input) {
			if (!USE_BINARY_WRITER) {
				SendInput(Encoding.UTF8.GetString(input));
				return;
			}
			_inputWriterBinary.Write(input);
			_inputWriterBinary.Flush();
		}

		/// <summary>
		/// Closes the stdin pipe to the child process, causing it to receive EOF on its next read.
		/// </summary>
		public void CloseInput() {
			_inputWriter?.Close();
			_inputWriter?.Dispose();
			_inputWriterBinary?.Close();
			_inputWriterBinary?.Dispose();
			_inputWriter = null;
			_inputWriterBinary = null;
		}

		/// <summary>
		/// Terminates the child process if it is still running.
		/// </summary>
		public void KillProcess() {
			if (Process?.HasExited != false) return;
			Process.Kill();
		}

		// ------------------------------------------------------------------
		// ITerminalConnection explicit implementations
		// ------------------------------------------------------------------

		void ITerminalConnection.WriteInput(string data) {
			Span<char> span = data.ToCharArray();
			InputInterceptor?.Invoke(ref span);
			if (span.Length > 0 && !_ReadOnly)
				SendInput(span);
		}

		void ITerminalConnection.Resize(uint row_height, uint column_width) {
			_console?.Resize((int)column_width, (int)row_height);
		}

		void ITerminalConnection.Close() {
			_console?.Dispose();
		}

		// ------------------------------------------------------------------
		// Output emission
		// ------------------------------------------------------------------

		/// <summary>
		/// Delivers <paramref name="str"/> to the UI terminal renderer by raising <see cref="OutputReceived"/>.
		/// ANSI/VT sequences included in <paramref name="str"/> are rendered by the terminal engine.
		/// </summary>
		public void EmitOutput(ReadOnlySpan<char> str) {
			OutputReceived?.Invoke(this, new TerminalOutputEventArgs(str.ToString()));
		}

		// ------------------------------------------------------------------
		// Terminal control sequences
		// ------------------------------------------------------------------

		/// <summary>Public overload: resizes the viewport to the given dimensions.</summary>
		public void Resize(int column_width, int row_height) {
			_console?.Resize(column_width, row_height);
		}

		/// <summary>
		/// Shows or hides the terminal cursor via the DECTCEM private-mode sequence.
		/// </summary>
		public void SetCursorVisibility(bool visible) =>
			EmitOutput("\x1b[?25" + (visible ? 'h' : 'l'));

		/// <summary>
		/// Clears the terminal screen.
		/// </summary>
		/// <param name="fullReset">
		/// When true, sends a full terminal reset (RIS + OSC 104) that restores all parameters
		/// to defaults. When false, only clears the visible screen and scrollback buffer.
		/// </param>
		public void ClearScreen(bool fullReset = false) =>
			EmitOutput(fullReset ? "\x001bc\x1b]104\x1b\\" : "\x1b[H\x1b[2J[3J");

		/// <summary>
		/// Enables or disables Win32 extended keyboard input mode (win32-input-mode).
		/// When enabled, key events are reported using the extended protocol described at
		/// https://github.com/microsoft/terminal/blob/main/doc/specs/%234999%20-%20Improved%20keyboard%20handling%20in%20Conpty.md
		/// </summary>
		public void SetWin32InputMode(bool enable) {
			var suffix = enable ? "h" : "l";
			EmitOutput($"\x1b[?9001{suffix}");
		}

		// ------------------------------------------------------------------
		// Read-only mode
		// ------------------------------------------------------------------

		/// <summary>
		/// Enables or disables read-only mode. While read-only, input from the UI is silently
		/// discarded. Optionally updates cursor visibility to reflect the new state.
		/// </summary>
		public void SetReadOnly(bool readOnly = true, bool updateCursor = true) {
			_ReadOnly = readOnly;
			if (updateCursor)
				SetCursorVisibility(!readOnly);
		}

		// ------------------------------------------------------------------
		// Output read loop
		// ------------------------------------------------------------------

		/// <summary>
		/// Pumps the child process's output: repeatedly fills a fixed char buffer from
		/// <see cref="OutputStream"/>, hands each fill to <see cref="FilterChunk"/> to decide
		/// what (if anything) to surface this iteration, and forwards the result to the UI.
		/// Runs until the stream reports end-of-data.
		/// </summary>
		protected virtual void RunReadLoop() {
			if (ReadLoopStarted)
				return;
			ReadLoopStarted = true;

			char[] buffer = new char[OutputBufferSize];

			using (var reader = new StreamReader(OutputStream)) {
				int filled;
				while ((filled = reader.Read(buffer, 0, buffer.Length)) > 0) {
					string toSurface = FilterChunk(buffer.AsSpan(0, filled));
					if (toSurface != null)
						ForwardToTerminal(toSurface);
				}
			}
		}

		/// <summary>
		/// Decides what portion of a freshly read chunk should reach the UI this iteration.
		/// The base connection forwards every chunk unchanged; subclasses may withhold,
		/// buffer, or rewrite the data. Returning <c>null</c> surfaces nothing this iteration.
		/// </summary>
		/// <param name="chunk">The characters produced by the latest read.</param>
		protected virtual string FilterChunk(ReadOnlySpan<char> chunk) => chunk.ToString();

		/// <summary>
		/// Runs <paramref name="text"/> through the optional <see cref="OutputInterceptor"/>,
		/// then emits whatever survives to the terminal renderer and the output log.
		/// Empty payloads are dropped before the interceptor runs.
		/// </summary>
		private void ForwardToTerminal(string text) {
			if (text.Length == 0)
				return;

			Span<char> span = text.ToCharArray();
			OutputInterceptor?.Invoke(ref span);
			if (span.IsEmpty)
				return;

			string outgoing = span.ToString();
			EmitOutput(outgoing);
			OutputLog?.Append(outgoing);
		}

		// ------------------------------------------------------------------
		// Cleanup helpers
		// ------------------------------------------------------------------

		/// <summary>
		/// Registers a CTRL_CLOSE handler so the given cleanup action runs if the
		/// console window is dismissed via the title-bar close button.
		/// </summary>
		private static void OnClose(Action cleanup) {
			PInvoke.SetConsoleCtrlHandler(eventType => {
				if (eventType == PInvoke.CTRL_CLOSE_EVENT) {
					cleanup();
				}
				return false;
			}, true);
		}

		/// <summary>Disposes each resource in order, ignoring individual failures.</summary>
		private void DisposeResources(params IDisposable[] resources) {
			foreach (var resource in resources)
				resource.Dispose();
		}
	}
}
