using System;
using System.Text;

namespace Term1809.Terminal {
	/// <summary>
	/// A <see cref="ConPtyConnection"/> that withholds output until a configured delimiter is
	/// detected, then releases everything up to (but not including) the delimiter in one shot.
	/// Useful for capturing discrete command outputs where a known sentinel marks completion.
	///
	/// Strategy: instead of indexing into a single fixed buffer, this class accumulates the
	/// not-yet-released text in a growing <see cref="StringBuilder"/> ("carry-over"). Each read
	/// is appended; the carry-over is scanned for the last delimiter, the portion before it is
	/// released, and the remainder stays buffered for next time. Safety valves release the whole
	/// carry-over when it grows past the configured capacity or when an idle timeout elapses.
	/// </summary>
	public class DelimitedConPtyConnection : ConPtyConnection {

		// Text seen since the last delimiter was consumed (or since startup). Released either when
		// a delimiter shows up, when it grows past OutputBufferSize, or on idle timeout.
		private readonly StringBuilder _carry = new StringBuilder();

		// Active delimiter as a plain string (null/empty disables delimiter buffering).
		private string _delimiter;

		// Idle-flush window; default(TimeSpan) disables it.
		private TimeSpan _idleFlush;

		// When the most recent delimiter was observed (only tracked while _idleFlush is enabled).
		private DateTime _lastDelimiterAt;

		/// <summary>
		/// Creates a new <see cref="DelimitedConPtyConnection"/>.
		/// </summary>
		/// <param name="READ_BUFFER_SIZE">Output read-buffer size in chars. Defaults to 16 KB.</param>
		/// <param name="USE_BINARY_WRITER">Pass <c>true</c> to use a <see cref="System.IO.BinaryWriter"/> for input.</param>
		/// <param name="delimiter">
		/// Initial delimiter sequence. Pass <see cref="ReadOnlySpan{T}.Empty"/> (the default)
		/// to start with no delimiter active.
		/// </param>
		/// <param name="MaxWaitTimeoutForDelimiter">
		/// If non-zero, the carry-over is flushed when this duration elapses after the last
		/// delimiter was seen and no new delimiter has arrived.
		/// </param>
		public DelimitedConPtyConnection(
			int READ_BUFFER_SIZE = 1024 * 16,
			bool USE_BINARY_WRITER = false,
			ReadOnlySpan<char> delimiter = default,
			TimeSpan MaxWaitTimeoutForDelimiter = default)
			: base(READ_BUFFER_SIZE, USE_BINARY_WRITER) {
			if (!delimiter.IsEmpty)
				SetOutputDelimiter(delimiter, MaxWaitTimeoutForDelimiter);
		}

		/// <summary>
		/// Activates (or replaces) the output delimiter. Output is held until the delimiter is
		/// detected in the stream; the delimiter itself is never forwarded to the UI.
		/// Pass an empty span to disable delimiter-based buffering.
		/// </summary>
		/// <param name="delimiter">Delimiter character sequence.</param>
		/// <param name="MaxWaitTimeoutForDelimiter">
		/// Optional timeout: if a delimiter has previously been seen and no new delimiter
		/// arrives within this window, the entire carry-over is flushed on the next output event.
		/// </param>
		public void SetOutputDelimiter(ReadOnlySpan<char> delimiter, TimeSpan MaxWaitTimeoutForDelimiter = default) {
			_delimiter = delimiter.IsEmpty ? null : delimiter.ToString();
			_idleFlush = MaxWaitTimeoutForDelimiter;
		}

		/// <summary>
		/// Accumulates incoming text and releases it only at delimiter boundaries (with overflow
		/// and idle-timeout safety valves). Returns the text to surface this iteration, or
		/// <c>null</c> to withhold everything until more data arrives.
		/// </summary>
		protected override string FilterChunk(ReadOnlySpan<char> chunk) {
			// With no delimiter configured, behave exactly like the base connection.
			if (string.IsNullOrEmpty(_delimiter))
				return chunk.ToString();

			_carry.Append(chunk);

			// Look for the LAST delimiter across everything held so far. Materializing the
			// carry-over to a string keeps the search simple and avoids manual index bookkeeping.
			string held = _carry.ToString();
			int boundary = held.LastIndexOf(_delimiter, StringComparison.Ordinal);

			if (boundary >= 0) {
				string release = held.Substring(0, boundary);
				int resumeFrom = boundary + _delimiter.Length;

				_carry.Clear();
				if (resumeFrom < held.Length)
					_carry.Append(held, resumeFrom, held.Length - resumeFrom);

				if (_idleFlush != default)
					_lastDelimiterAt = DateTime.Now;

				return release;
			}

			// Overflow valve: a single un-delimited run has outgrown the buffer capacity, so
			// release it rather than buffering without bound.
			if (_carry.Length >= OutputBufferSize)
				return DrainCarry();

			// Idle valve: a delimiter was seen earlier, but none has arrived within the window.
			if (_idleFlush != default && _lastDelimiterAt != default
				&& (DateTime.Now - _lastDelimiterAt) > _idleFlush) {
				_lastDelimiterAt = DateTime.Now;
				return DrainCarry();
			}

			// Nothing to release yet — keep buffering.
			return null;
		}

		/// <summary>Empties the carry-over and returns everything it held.</summary>
		private string DrainCarry() {
			string all = _carry.ToString();
			_carry.Clear();
			return all;
		}
	}
}
