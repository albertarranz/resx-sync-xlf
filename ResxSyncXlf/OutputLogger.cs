using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ResxEditor
{
    /// <summary>
    /// Thin wrapper that writes to the Visual Studio Output window pane "Resx Sync".
    /// Safe to call from any thread.
    /// </summary>
    internal static class OutputLogger
    {
        private static IVsOutputWindowPane? _pane;
        private static readonly object _lock = new object();

        public static void Initialize(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var outputWindow = serviceProvider.GetService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                if (outputWindow == null)
                    return;

                Guid paneGuid = new Guid("A3B4C5D6-E7F8-9012-BCDE-F01234567890");
                outputWindow.CreatePane(ref paneGuid, "Resx Sync", fInitVisible: 1, fClearWithSolution: 0);
                outputWindow.GetPane(ref paneGuid, out _pane);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ResxSync] OutputLogger init failed: {ex.Message}");
            }
        }

        public static void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            System.Diagnostics.Debug.Write(line);

            lock (_lock)
            {
                if (_pane == null)
                    return;
            }

            // The pane must be written on the UI thread.
            if (ThreadHelper.CheckAccess())
            {
                WriteToPane(line);
            }
            else
            {
#pragma warning disable VSTHRD001
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    WriteToPane(line);
                });
#pragma warning restore VSTHRD001
            }
        }

        private static void WriteToPane(string line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lock (_lock)
            {
                _pane?.OutputStringThreadSafe(line);
            }
        }
    }
}
