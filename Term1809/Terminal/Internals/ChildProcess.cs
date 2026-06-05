// Derived from the Microsoft Windows Terminal ConPTY sample (MIT, (c) Microsoft Corporation). See THIRD-PARTY-NOTICES.md
using System;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Term1809.Terminal.Internals {
    /// <summary>
    /// Owns the native handles for a child process spawned under a pseudo-console.
    /// The startup descriptor and process/thread handles are released when this
    /// instance is disposed or finalized.
    /// </summary>
    internal sealed class ChildProcess : IDisposable {

        /// <summary>
        /// Extended startup information used to launch the child process,
        /// including the proc-thread attribute list that wires up the ConPTY.
        /// </summary>
        public STARTUPINFOEXW StartupInfo { get; }

        /// <summary>
        /// Kernel handles and identifiers returned by CreateProcess for the child.
        /// </summary>
        public PROCESS_INFORMATION ProcessInfo { get; }

        public ChildProcess(STARTUPINFOEXW startupInfo, PROCESS_INFORMATION processInfo) {
            StartupInfo = startupInfo;
            ProcessInfo = processInfo;
        }

        // ------------------------------------------------------------------
        // Resource cleanup
        // ------------------------------------------------------------------

        private bool _released;

        /// <summary>
        /// Releases all native resources owned by this instance.
        /// When <paramref name="managedAlso"/> is <see langword="true"/> managed
        /// objects are also cleaned up (currently none); when <see langword="false"/>
        /// only unmanaged handles are freed (finalizer path).
        /// </summary>
        private void ReleaseResources(bool managedAlso) {
            if (_released) {
                return;
            }

            // Free the proc-thread attribute list before closing process handles.
            if (StartupInfo.lpAttributeList != default) {
                PInvoke.DeleteProcThreadAttributeList(StartupInfo.lpAttributeList);
            }

            if (ProcessInfo.hProcess != IntPtr.Zero) {
                PInvoke.CloseHandle(ProcessInfo.hProcess);
            }

            if (ProcessInfo.hThread != IntPtr.Zero) {
                PInvoke.CloseHandle(ProcessInfo.hThread);
            }

            _released = true;
        }

        ~ChildProcess() {
            ReleaseResources(managedAlso: false);
        }

        public void Dispose() {
            ReleaseResources(managedAlso: true);
            GC.SuppressFinalize(this);
        }
    }
}
