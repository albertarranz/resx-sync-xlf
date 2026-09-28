using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ResxEditor
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuids.ResxSyncPackageString)]
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string,
        PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(LanguageManagerWindow),
        Style = Microsoft.VisualStudio.Shell.VsDockStyle.Tabbed,
        Window = Microsoft.VisualStudio.Shell.Interop.ToolWindowGuids.SolutionExplorer)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class ResxSyncPackage : AsyncPackage, IVsSolutionEvents3
    {
        private ResxFileWatcher? _watcher;
        private BuildEventListener? _buildListener;
        private IVsSolution? _solution;
        private uint _solutionEventsCookie;
        private LanguageManagerWindow? _languageManagerWindow;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            OutputLogger.Initialize(this);
            OutputLogger.Log("[ResxSync] Package initialized.");

            _watcher = new ResxFileWatcher(this);
            _buildListener = new BuildEventListener(this);

            // Register the Tools > Language Manager command entirely in code (no .vsct needed).
            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
            {
                var cmdId = new CommandID(PackageGuids.LanguageManagerCmdSet, PackageGuids.LanguageManagerCommandId);
                var cmd = new MenuCommand(OpenLanguageManagerWindow, cmdId);
                mcs.AddCommand(cmd);
            }

            // Subscribe to solution events to refresh Language Manager when solution changes
            if (await GetServiceAsync(typeof(SVsSolution)) is IVsSolution solution)
            {
                _solution = solution;
                solution.AdviseSolutionEvents(this, out _solutionEventsCookie);
                OutputLogger.Log("[ResxSync] Subscribed to solution events.");
            }
        }

        private void OpenLanguageManagerWindow(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ToolWindowPane window = FindToolWindow(typeof(LanguageManagerWindow), 0, true);
            if (window?.Frame == null)
            {
                OutputLogger.Log("[ResxSync] Failed to create Language Manager window.");
                return;
            }
            var frame = (Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame)window.Frame;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
        }

        /// <summary>Called when a solution is fully opened.</summary>
        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OutputLogger.Log("[ResxSync] OnAfterOpenSolution triggered.");
            RefreshLanguageManager();
            return 0;
        }

        /// <summary>Called when a solution is closed.</summary>
        public int OnBeforeCloseSolution(object pUnkReserved)
        {
            return 0;
        }

        /// <summary>Called after a solution is closed.</summary>
        public int OnAfterCloseSolution(object pUnkReserved)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OutputLogger.Log("[ResxSync] Solution closed.");
            // Clear the cached window so it gets recreated with new solution
            _languageManagerWindow = null;
            return 0;
        }

        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)
        {
            return 0;
        }

        public int OnBeforeLoadProject(ref Guid guidProjectID, string pszProjectFilename)
        {
            return 0;
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            return 0;
        }

        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel)
        {
            return 0;
        }

        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
        {
            return 0;
        }

        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
        {
            return 0;
        }

        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
        {
            return 0;
        }

        public int OnAfterUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
        {
            return 0;
        }

        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel)
        {
            return 0;
        }

        public int OnQueryCloseProject(IVsHierarchy pHierarchy, ref int pfCancel)
        {
            return 0;
        }

        public int OnBeforeClosingChildren(IVsHierarchy pHierarchy)
        {
            return 0;
        }

        public int OnAfterOpeningChildren(IVsHierarchy pHierarchy)
        {
            return 0;
        }

        public int OnAfterClosingChildren(IVsHierarchy pHierarchy)
        {
            return 0;
        }

        public int OnAfterMergeSolution(object pUnkReserved)
        {
            return 0;
        }

        public int OnBeforeOpeningChildren(IVsHierarchy pHierarchy)
        {
            return 0;
        }

        private void RefreshLanguageManager()
        {
            try
            {
                // Get or find the Language Manager window
                _languageManagerWindow = FindToolWindow(typeof(LanguageManagerWindow), 0, false) as LanguageManagerWindow;
                if (_languageManagerWindow != null && _languageManagerWindow is LanguageManagerWindow window)
                {
                    // Trigger refresh
                    if (window.Content is LanguageManagerControl control)
                    {
                        control.OnVisible();
                        OutputLogger.Log("[ResxSync] Language Manager refreshed for new solution.");
                    }
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Error refreshing Language Manager: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                if (_solution != null && _solutionEventsCookie != 0)
                {
                    _solution.UnadviseSolutionEvents(_solutionEventsCookie);
                }
                _watcher?.Dispose();
                _buildListener?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

