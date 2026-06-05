// Derived from the Microsoft Windows Terminal ConPTY sample (MIT, (c) Microsoft Corporation). See THIRD-PARTY-NOTICES.md
using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;
using Windows.Win32.System.Console;

namespace Term1809.Terminal.Internals {
    /// <summary>
    /// P/Invoke declarations for the ConPTY (pseudo-console) API exposed by conpty.dll.
    /// All three entry points map to documented Windows Console API functions.
    /// </summary>
    /// <remarks>
    /// conpty.dll is copied to the output directory by the .csproj because the NuGet
    /// package ships under the legacy "win10-x64" RID, which .NET 8+ asset resolution
    /// does not map from "win-x64" automatically.
    /// </remarks>
    internal static class ConPtyNativeMethods {

        // -------------------------------------------------------------------------
        // CreatePseudoConsole
        // Allocates a new pseudo-console of the requested dimensions and returns a
        // handle through which the host can interact with it.
        // -------------------------------------------------------------------------
        [DllImport("conpty.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(
            COORD size,
            SafeFileHandle hInput,
            SafeFileHandle hOutput,
            uint dwFlags,
            out IntPtr phPC);

        // -------------------------------------------------------------------------
        // ClosePseudoConsole
        // Releases all resources owned by the pseudo-console identified by hPC.
        // -------------------------------------------------------------------------
        [DllImport("conpty.dll", SetLastError = true)]
        internal static extern int ClosePseudoConsole(IntPtr hPC);

        // -------------------------------------------------------------------------
        // ResizePseudoConsole
        // Notifies the pseudo-console that the host viewport has changed dimensions.
        // -------------------------------------------------------------------------
        [DllImport("conpty.dll", SetLastError = true)]
        internal static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    }
}
