// Derived from the Microsoft Windows Terminal ConPTY sample (MIT, (c) Microsoft Corporation). See THIRD-PARTY-NOTICES.md
using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;

namespace Term1809.Terminal.Internals {
    /// <summary>
    /// Wraps a Win32 anonymous pipe pair (read end + write end) as a single
    /// disposable unit suitable for wiring up a pseudo-console I/O channel.
    /// </summary>
    /// <remarks>
    /// Each ConPTY session requires two pipe pairs: one for the host-to-child
    /// direction (stdin) and one for the child-to-host direction (stdout).
    /// Create one <see cref="AnonymousPipePair"/> per direction.
    /// </remarks>
    public sealed class AnonymousPipePair : IDisposable {

        /// <summary>The read end of the anonymous pipe.</summary>
        public readonly SafeFileHandle ReadSide;

        /// <summary>The write end of the anonymous pipe.</summary>
        public readonly SafeFileHandle WriteSide;

        /// <summary>
        /// Creates the anonymous pipe pair. Throws <see cref="Win32Exception"/>
        /// if the underlying <c>CreatePipe</c> call fails.
        /// </summary>
        public AnonymousPipePair() {
            bool created = PInvoke.CreatePipe(out ReadSide, out WriteSide, lpPipeAttributes: null, nSize: 0);
            if (!created) {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed — could not allocate anonymous pipe pair.");
            }
        }

        private bool _disposed;

        /// <inheritdoc/>
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;

            ReadSide?.Dispose();
            WriteSide?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
