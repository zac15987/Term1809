// Derived from the Microsoft Windows Terminal ConPTY sample (MIT, (c) Microsoft Corporation). See THIRD-PARTY-NOTICES.md
using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.System.Console;

namespace Term1809.Terminal.Internals {
    /// <summary>
    /// Wraps a ConPTY handle (<c>HPCON</c>) and exposes resize and disposal
    /// semantics. Use <see cref="Create"/> to allocate a new pseudo-console.
    /// </summary>
    public class ConPtyHandle : IDisposable {

        // ------------------------------------------------------------------
        // Nested SafeHandle
        // ------------------------------------------------------------------

        /// <summary>
        /// A <see cref="SafeHandle"/> that owns an <c>HPCON</c> and closes it
        /// through <see cref="ConPtyNativeMethods.ClosePseudoConsole"/> when the
        /// last reference is released.
        /// </summary>
        internal class ConPtyOwnedHandle : ClosePseudoConsoleSafeHandle {
            public ConPtyOwnedHandle(IntPtr rawHandle, bool ownsHandle = true)
                : base(rawHandle, ownsHandle) {
            }

            protected override bool ReleaseHandle() {
                ConPtyNativeMethods.ClosePseudoConsole(handle);
                return true;
            }
        }

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        private bool _disposed;

        /// <summary>Gets whether this instance has been disposed.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>The underlying owned ConPTY safe handle.</summary>
        internal ConPtyOwnedHandle Handle { get; }

        /// <summary>
        /// Returns the raw <see cref="IntPtr"/> for callers that need to pass
        /// the handle to unmanaged code directly.
        /// </summary>
        public IntPtr GetDangerousHandle => Handle.DangerousGetHandle();

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        private ConPtyHandle(ConPtyOwnedHandle ownedHandle) {
            Handle = ownedHandle;
        }

        /// <summary>
        /// Creates and initializes a new pseudo-console of the requested dimensions.
        /// If either <paramref name="width"/> or <paramref name="height"/> is zero
        /// the dimensions default to 80 x 30.
        /// </summary>
        /// <param name="inputReadSide">Read end of the input pipe.</param>
        /// <param name="outputWriteSide">Write end of the output pipe.</param>
        /// <param name="width">Viewport width in columns.</param>
        /// <param name="height">Viewport height in rows.</param>
        /// <returns>A <see cref="ConPtyHandle"/> owning the new pseudo-console.</returns>
        /// <exception cref="Win32Exception">Thrown when ConPTY creation fails.</exception>
        public static ConPtyHandle Create(
            SafeFileHandle inputReadSide,
            SafeFileHandle outputWriteSide,
            int width,
            int height) {

            const int defaultWidth = 80;
            const int defaultHeight = 30;

            if (width == 0 || height == 0) {
                width = defaultWidth;
                height = defaultHeight;
            }

            var size = new COORD { X = (short)width, Y = (short)height };
            int createResult = ConPtyNativeMethods.CreatePseudoConsole(
                size, inputReadSide, outputWriteSide, 0, out IntPtr hPC);

            if (createResult != 0) {
                throw new Win32Exception(createResult);
            }

            return new ConPtyHandle(new ConPtyOwnedHandle(hPC));
        }

        // ------------------------------------------------------------------
        // Operations
        // ------------------------------------------------------------------

        /// <summary>
        /// Notifies the pseudo-console that the host viewport has been resized.
        /// </summary>
        public void Resize(int width, int height) {
            var newSize = new COORD { X = (short)width, Y = (short)height };
            ConPtyNativeMethods.ResizePseudoConsole(Handle.DangerousGetHandle(), newSize);
        }

        // ------------------------------------------------------------------
        // IDisposable
        // ------------------------------------------------------------------

        public void Dispose() {
            if (!_disposed) {
                Handle.Dispose();
                _disposed = true;
            }
        }
    }
}
