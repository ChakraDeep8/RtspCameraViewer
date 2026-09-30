using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace RtspCameraViewer
{
    public partial class App : Application
    {
        /// <summary>
        /// Held for the life of the process. Local\ scopes it to this Windows session, which is
        /// what "already running for this user" means.
        /// </summary>
        private Mutex? _singleInstance;

        /// <summary>
        /// Refuses to start a second copy, and brings the running one to the front instead.
        ///
        /// This is a data-safety measure, not tidiness. The camera list is read into memory at
        /// startup and written back whole whenever something changes, so two instances each hold
        /// their own copy and the last one to save wins. A window left open from before a change
        /// — classes renamed, a camera added — will happily write its stale copy over the top and
        /// silently undo the change. That is exactly how renamed classes appeared to "not show":
        /// an older window was still displaying the auto-derived device codes it had loaded
        /// before the rename, one save away from restoring them for real.
        ///
        /// A second copy is also easy to start by accident here — desktop shortcut, Start menu,
        /// the "start when I sign in" task, the installer's finish page — and two of them would
        /// each open a full grid of RTSP streams, which is enough on its own to tear the picture.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            _singleInstance = new Mutex(initiallyOwned: true, name: @"Local\RtspCameraViewer.SingleInstance", createdNew: out bool isFirst);

            if (!isFirst)
            {
                FocusRunningInstance();
                // Before base.OnStartup, so StartupUri never creates a second main window.
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        /// <summary>
        /// Brings the copy that is already running to the foreground, restoring it if minimized,
        /// so launching again reads as "here it is" rather than as nothing happening.
        /// </summary>
        private static void FocusRunningInstance()
        {
            try
            {
                int mine = Environment.ProcessId;
                foreach (var process in Process.GetProcessesByName("RtspCameraViewer"))
                {
                    if (process.Id == mine) continue;
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;

                    if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                    SetForegroundWindow(handle);
                    return;
                }
            }
            catch
            {
                // Focusing is a courtesy; failing to do it must not stop this copy exiting.
            }
        }

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
