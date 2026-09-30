using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ResxEditor
{
    /// <summary>
    /// Listens for document saves via IVsRunningDocTableEvents3.
    /// When a .resx file is saved, delegates to XlfSynchronizer.
    /// </summary>
    internal sealed class ResxFileWatcher : IVsRunningDocTableEvents3, IDisposable
    {
        private readonly IVsRunningDocumentTable _rdt;
        private uint _cookie;
        private bool _disposed;

        // Stores the original disk bytes for localised .resx files captured in OnBeforeSave,
        // so we can restore them in OnAfterSave (the save already happened by then).
        private readonly Dictionary<uint, byte[]> _pendingRestores = new Dictionary<uint, byte[]>();

        // Stores the original disk bytes for the base .resx file captured in OnBeforeSave,
        // so we can diff old vs new keys in OnAfterSave to detect renames.
        private readonly Dictionary<uint, byte[]> _pendingBaseSnapshots = new Dictionary<uint, byte[]>();

        public ResxFileWatcher(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _rdt = (IVsRunningDocumentTable)serviceProvider.GetService(typeof(SVsRunningDocumentTable));
            if (_rdt != null)
            {
                _rdt.AdviseRunningDocTableEvents(this, out _cookie);
                OutputLogger.Log("[ResxSync] ResxFileWatcher registered.");
            }
            else
            {
                OutputLogger.Log("[ResxSync] ERROR: could not obtain IVsRunningDocumentTable.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            ThreadHelper.ThrowIfNotOnUIThread();
            if (_rdt != null && _cookie != 0)
            {
                _rdt.UnadviseRunningDocTableEvents(_cookie);
                _cookie = 0;
            }
        }

        // ------------------------------------------------------------------ IVsRunningDocTableEvents3

        public int OnAfterSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string? path = GetDocumentPath(docCookie);
                if (path == null || !path.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
                    return VSConstants.S_OK;

                // Only the base (no language suffix) resx should trigger XLF sync.
                // A localised file looks like "Strings.es.resx" or "Strings.fr-FR.resx".
                // The base file looks like "Strings.resx" — its name without extension has no dot.
                string nameNoExt = System.IO.Path.GetFileNameWithoutExtension(path); // e.g. "Strings" or "Strings.es"
                bool isLocalisedResx = nameNoExt.Contains(".");

                if (isLocalisedResx)
                {
                    if (_pendingRestores.TryGetValue(docCookie, out byte[] originalBytes))
                    {
                        _pendingRestores.Remove(docCookie);
                        System.IO.File.WriteAllBytes(path, originalBytes);
                        OutputLogger.Log($"[ResxSync] Restored original content: {System.IO.Path.GetFileName(path)}");
                        // Reload the in-memory document so VS is in sync with the restored file.
                        ReloadDocument(docCookie);
                    }
                    else
                    {
                        OutputLogger.Log($"[ResxSync] Ignoring localised resx (no snapshot): {System.IO.Path.GetFileName(path)}");
                    }
                    return VSConstants.S_OK;
                }

                OutputLogger.Log($"[ResxSync] Resx saved: {path}");

                // Detect renames by diffing the pre-save snapshot against the just-saved content,
                // then propagate the rename to localised .resx siblings so they aren't orphaned.
                Dictionary<string, string>? pendingRenames = null;
                if (_pendingBaseSnapshots.TryGetValue(docCookie, out byte[] baseSnapshot))
                {
                    _pendingBaseSnapshots.Remove(docCookie);
                    try
                    {
                        var oldKeys = XlfSynchronizer.ReadResxKeysFromBytes(baseSnapshot);
                        var newKeys = XlfSynchronizer.ReadResxKeysFromBytes(System.IO.File.ReadAllBytes(path));
                        var renames = XlfSynchronizer.DetectRenames(oldKeys, newKeys);
                        pendingRenames = renames;
                        if (renames.Count > 0)
                        {
                            foreach (var rename in renames)
                                OutputLogger.Log($"[ResxSync]   -> Detected rename: '{rename.Key}' -> '{rename.Value}'");

                            var renameResults = XlfSynchronizer.RenameInLocalizedResx(path, renames);
                            foreach (var (resxPath, renamed) in renameResults)
                            {
                                if (renamed > 0)
                                    OutputLogger.Log($"[ResxSync]   -> Renamed {renamed} entries in {System.IO.Path.GetFileName(resxPath)}");
                            }
                        }

                        // Keys that were removed and not part of a rename are plain deletions —
                        // propagate their removal to the localised .resx siblings too.
                        var deletedKeys = oldKeys.Keys
                            .Where(k => !newKeys.ContainsKey(k) && !renames.ContainsKey(k))
                            .ToList();
                        if (deletedKeys.Count > 0)
                        {
                            foreach (var deletedKey in deletedKeys)
                                OutputLogger.Log($"[ResxSync]   -> Detected deletion: '{deletedKey}'");

                            var removeResults = XlfSynchronizer.RemoveInLocalizedResx(path, deletedKeys);
                            foreach (var (resxPath, removedCount) in removeResults)
                            {
                                if (removedCount > 0)
                                    OutputLogger.Log($"[ResxSync]   -> Removed {removedCount} entries from {System.IO.Path.GetFileName(resxPath)}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OutputLogger.Log($"[ResxSync] Rename detection error: {ex.Message}");
                    }
                }

                var results = XlfSynchronizer.Synchronize(path, pendingRenames);

                foreach (var (xlfPath, added, removed, updated) in results)
                {
                    if (added > 0 || removed > 0 || updated > 0)
                        OutputLogger.Log($"[ResxSync]   -> {System.IO.Path.GetFileName(xlfPath)}: added {added}, removed {removed}, updated {updated}");
                    else
                        OutputLogger.Log($"[ResxSync]   -> No changes for {System.IO.Path.GetFileName(xlfPath)}");
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] OnAfterSave error: {ex.Message}");
            }
            return VSConstants.S_OK;
        }

        private void ReloadDocument(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _rdt.GetDocumentInfo(docCookie, out _, out _, out _, out _, out _, out _, out IntPtr ppunkDocData);
                if (ppunkDocData != IntPtr.Zero)
                {
                    var docData = Marshal.GetObjectForIUnknown(ppunkDocData);
                    Marshal.Release(ppunkDocData);
                    if (docData is IVsPersistDocData persistDocData)
                        persistDocData.ReloadDocData(0);
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] ReloadDocument error: {ex.Message}");
            }
        }

        private string? GetDocumentPath(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _rdt.GetDocumentInfo(
                docCookie,
                out _,   // grfRDTFlags
                out _,   // dwReadLocks
                out _,   // dwEditLocks
                out string pbstrMkDocument,
                out _,   // phier
                out _,   // pitemid
                out _);  // ppunkDocData
            return pbstrMkDocument;
        }

        // ------------------------------------------------------------------ unused interface members

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;
        public int OnBeforeSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string? path = GetDocumentPath(docCookie);
                if (path == null || !path.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
                    return VSConstants.S_OK;

                string nameNoExt = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!nameNoExt.Contains("."))
                {
                    // Base resx: snapshot current disk content so OnAfterSave can diff old vs new
                    // keys and detect renames.
                    if (System.IO.File.Exists(path))
                    {
                        _pendingBaseSnapshots[docCookie] = System.IO.File.ReadAllBytes(path);
                    }
                    return VSConstants.S_OK; // let it save normally
                }

                // Localised resx: snapshot current disk content before VS overwrites it.
                if (System.IO.File.Exists(path))
                {
                    _pendingRestores[docCookie] = System.IO.File.ReadAllBytes(path);
                    OutputLogger.Log($"[ResxSync] Captured snapshot for restore: {System.IO.Path.GetFileName(path)}");
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] OnBeforeSave error: {ex.Message}");
            }
            return VSConstants.S_OK;
        }
    }
}
