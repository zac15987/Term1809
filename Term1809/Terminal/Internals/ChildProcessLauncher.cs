// Derived from the Microsoft Windows Terminal ConPTY sample (MIT, (c) Microsoft Corporation). See THIRD-PARTY-NOTICES.md
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Term1809.Terminal.Internals {

    /// <summary>
    /// Represents a child process that was launched under a pseudo-console.
    /// Exposes lifecycle control and cleanup semantics.
    /// </summary>
    public interface IChildProcess : IDisposable {
        /// <summary>Blocks the caller until the child process terminates.</summary>
        void WaitForExit();

        /// <summary>Gets whether the child process has already exited.</summary>
        bool HasExited { get; }

        /// <summary>Terminates the child process, optionally including its entire process tree.</summary>
        void Kill(bool EntireProcessTree = false);
    }

    /// <summary>
    /// Factory contract for starting child processes under a pseudo-console session.
    /// </summary>
    public interface IChildProcessLauncher {
        /// <summary>Starts a new child process attached to the given pseudo-console.</summary>
        IChildProcess Start(string command, nuint attributes, ConPtyHandle console, string workingDirectory = null);
    }

    /// <summary>
    /// Launches child processes wired to a ConPTY session via Windows process-thread attributes.
    /// </summary>
    public static class ChildProcessLauncher {

        /// <summary>
        /// Wraps a <see cref="ChildProcess"/> and exposes a managed <see cref="System.Diagnostics.Process"/>
        /// view of the running child along with lifecycle helpers.
        /// </summary>
        public class LaunchedProcess : IDisposable, IChildProcess {

            private ChildProcess _nativeProcess;
            private System.Diagnostics.Process _managedView;
            private bool _disposed;

            internal LaunchedProcess(ChildProcess nativeProcess) {
                _nativeProcess = nativeProcess;
            }

            // ------------------------------------------------------------------
            // Identity
            // ------------------------------------------------------------------

            /// <summary>The operating-system process identifier for the child.</summary>
            public int Pid => (int)_nativeProcess.ProcessInfo.dwProcessId;

            /// <summary>
            /// A <see cref="System.Diagnostics.Process"/> handle bound to <see cref="Pid"/>.
            /// Resolved on first access and cached.
            /// </summary>
            public System.Diagnostics.Process Process => _managedView ??= System.Diagnostics.Process.GetProcessById(Pid);

            // ------------------------------------------------------------------
            // Lifecycle
            // ------------------------------------------------------------------

            /// <summary>Gets whether the child process has already exited.</summary>
            public bool HasExited => Process.HasExited;

            /// <summary>Blocks the caller until the child process terminates.</summary>
            public void WaitForExit() => Process.WaitForExit();

            /// <summary>Terminates the child process, optionally including its entire process tree.</summary>
            public void Kill(bool EntireProcessTree = false) => Process.Kill(EntireProcessTree);

            // ------------------------------------------------------------------
            // IDisposable
            // ------------------------------------------------------------------

            /// <summary>Releases native resources owned by the underlying <see cref="ChildProcess"/>.</summary>
            protected virtual void Dispose(bool disposing) {
                if (!_disposed) {
                    if (disposing) {
                        _nativeProcess.Dispose();
                    }
                    _disposed = true;
                }
            }

            /// <inheritdoc/>
            public void Dispose() {
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }

        // ------------------------------------------------------------------
        // Public entry point
        // ------------------------------------------------------------------

        /// <summary>
        /// Launches a new child process attached to <paramref name="console"/>.
        /// The returned <see cref="LaunchedProcess"/> owns all native resources and
        /// must be disposed by the caller.
        /// </summary>
        /// <param name="command">The command line to execute.</param>
        /// <param name="attributes">Additional proc-thread attribute flags.</param>
        /// <param name="console">The pseudo-console to attach the child process to.</param>
        /// <param name="workingDirectory">
        /// Optional working directory for the child process.
        /// Passes <see langword="null"/> to inherit the current directory.
        /// </param>
        /// <returns>A <see cref="LaunchedProcess"/> wrapping the newly created child.</returns>
        public static LaunchedProcess Start(
            string command,
            nuint attributes,
            ConPtyHandle console,
            string workingDirectory = null) {

            var startupInfo = ConfigureProcessThread(console.Handle, attributes);
            var processInfo = RunProcess(ref startupInfo, command, workingDirectory);
            return new LaunchedProcess(new ChildProcess(startupInfo, processInfo));
        }

        // ------------------------------------------------------------------
        // Private implementation
        // ------------------------------------------------------------------

        /// <summary>
        /// Allocates and populates a <see cref="STARTUPINFOEXW"/> whose proc-thread
        /// attribute list wires <paramref name="hPC"/> as the ConPTY for the child.
        /// </summary>
        /// <remarks>
        /// Follows the two-call pattern documented at
        /// https://docs.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session#preparing-for-creation-of-the-child-process:
        /// first call with a null list to measure required buffer size, second call to
        /// populate the allocated buffer, then UpdateProcThreadAttribute to attach the handle.
        /// </remarks>
        unsafe private static STARTUPINFOEXW ConfigureProcessThread(
            ConPtyHandle.ConPtyOwnedHandle hPC,
            nuint attributes) {

            // Step 1 — measure the required buffer size for the attribute list.
            nuint requiredBytes = 0;
            bool sizeProbe = PInvoke.InitializeProcThreadAttributeList(
                lpAttributeList: default,
                dwAttributeCount: 1,
                dwFlags: 0,
                lpSize: &requiredBytes
            );

            // The probe call is expected to fail with ERROR_INSUFFICIENT_BUFFER.
            // A successful return or a zero size both indicate an unexpected state.
            if (sizeProbe || requiredBytes == 0) {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not calculate the required size for the proc-thread attribute list.");
            }

            // Step 2 — allocate and initialize the attribute list.
            var startupEx = new STARTUPINFOEXW();
            startupEx.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEXW>();
            startupEx.lpAttributeList = new LPPROC_THREAD_ATTRIBUTE_LIST(
                (void*)Marshal.AllocHGlobal((int)requiredBytes));

            bool listInitialized = PInvoke.InitializeProcThreadAttributeList(
                lpAttributeList: startupEx.lpAttributeList,
                dwAttributeCount: 1,
                dwFlags: 0,
                lpSize: &requiredBytes
            );
            if (!listInitialized) {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not initialize the proc-thread attribute list.");
            }

            // Step 3 — bind the pseudo-console handle as a thread attribute.
            bool attributeSet = PInvoke.UpdateProcThreadAttribute(
                lpAttributeList: startupEx.lpAttributeList,
                dwFlags: 0,
                attributes,
                (void*)hPC.DangerousGetHandle(),
                (nuint)IntPtr.Size,
                null,
                (nuint*)null
            );
            if (!attributeSet) {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not bind the pseudo-console handle to the proc-thread attribute list.");
            }

            return startupEx;
        }

        /// <summary>
        /// Creates the child process using <c>CreateProcess</c> with
        /// <see cref="PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT"/> so the
        /// ConPTY attribute list in <paramref name="startupEx"/> is honored.
        /// </summary>
        unsafe private static PROCESS_INFORMATION RunProcess(
            ref STARTUPINFOEXW startupEx,
            string commandLine,
            string workingDirectory = null) {

            uint attributeSize = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>();
            var processSecurity = new SECURITY_ATTRIBUTES { nLength = attributeSize };
            var threadSecurity = new SECURITY_ATTRIBUTES { nLength = attributeSize };

            // CreateProcess requires a mutable char buffer for the command line.
            Span<char> mutableCommandLine = (commandLine + '\0').ToCharArray();
            var capturedInfo = startupEx;

            bool created = PInvoke.CreateProcess(
                null,
                ref mutableCommandLine,
                processSecurity,
                threadSecurity,
                false,
                PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT,
                null,
                workingDirectory,
                capturedInfo.StartupInfo,
                out var processInfo
            );

            if (!created) {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create the child process.");
            }

            return processInfo;
        }
    }
}
