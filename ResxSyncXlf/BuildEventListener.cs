using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ResxEditor
{
    /// <summary>
    /// Subscribes to IVsSolutionBuildManager2 events.
    /// Before each project build starts, synchronizes XLF translations into localised .resx files.
    /// </summary>
    internal sealed class BuildEventListener : IVsUpdateSolutionEvents2, IDisposable
    {
        private readonly IVsSolutionBuildManager2 _buildManager;
        private uint _cookie;
        private bool _disposed;

        public BuildEventListener(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _buildManager = (IVsSolutionBuildManager2)serviceProvider.GetService(typeof(SVsSolutionBuildManager));
            if (_buildManager != null)
            {
                _buildManager.AdviseUpdateSolutionEvents(this, out _cookie);
                OutputLogger.Log("[ResxSync] BuildEventListener registered.");
            }
            else
            {
                OutputLogger.Log("[ResxSync] ERROR: could not obtain IVsSolutionBuildManager2.");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ThreadHelper.ThrowIfNotOnUIThread();
            if (_buildManager != null && _cookie != 0)
            {
                _buildManager.UnadviseUpdateSolutionEvents(_cookie);
                _cookie = 0;
            }
        }

        // ------------------------------------------------------------------ IVsUpdateSolutionEvents2

        /// <summary>
        /// Called just before each project configuration starts building.
        /// We use this to push XLF translations into localised resx files.
        /// </summary>
        public int UpdateProjectCfg_Begin(
            IVsHierarchy pHierProj,
            IVsCfg pCfgProj,
            IVsCfg pCfgSln,
            uint dwAction,
            ref int pfCancel)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string? projectDir = GetProjectDir(pHierProj);
                if (string.IsNullOrEmpty(projectDir))
                    return VSConstants.S_OK;

                OutputLogger.Log($"[ResxSync] Build starting for: {System.IO.Path.GetFileName(projectDir)}");
                var results = XlfToResxSynchronizer.SynchronizeFromXlf(projectDir);

                foreach (var (resxPath, updated) in results)
                {
                    if (updated > 0)
                        OutputLogger.Log($"[ResxSync]   -> Wrote {updated} translations to {System.IO.Path.GetFileName(resxPath)}");
                    else
                        OutputLogger.Log($"[ResxSync]   -> No translation changes for {System.IO.Path.GetFileName(resxPath)}");
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] UpdateProjectCfg_Begin error: {ex.Message}");
            }
            return VSConstants.S_OK;
        }

        private static string? GetProjectDir(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            hierarchy.GetProperty(
                (uint)VSConstants.VSITEMID.Root,
                (int)__VSHPROPID.VSHPROPID_ProjectDir,
                out object? value);
            return value as string;
        }

        // ------------------------------------------------------------------ unused interface members

        public int UpdateSolution_Begin(ref int pfCancelUpdate) => VSConstants.S_OK;
        public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand) => VSConstants.S_OK;
        public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;
        public int UpdateSolution_Cancel() => VSConstants.S_OK;
        public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;
        public int UpdateProjectCfg_Done(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, int fSuccess, int fCancel) => VSConstants.S_OK;
    }
}
