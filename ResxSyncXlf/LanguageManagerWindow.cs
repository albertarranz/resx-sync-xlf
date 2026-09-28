using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ResxEditor
{
    /// <summary>
    /// Tool window that hosts the Language Manager WPF control.
    /// Opened via Tools > Language Manager or auto-opened when solution is fully loaded.
    /// </summary>
    [Guid(PackageGuids.LanguageManagerWindowString)]
    public sealed class LanguageManagerWindow : ToolWindowPane
    {
        private readonly LanguageManagerControl _control;
        private bool _initialized = false;

        public LanguageManagerWindow() : base(null)
        {
            try
            {
                Caption = "Language Manager";
                _control = new LanguageManagerControl();
                Content = _control;
                OutputLogger.Log("[ResxSync] LanguageManagerWindow initialized successfully");
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Error in LanguageManagerWindow constructor: {ex.Message}");
                OutputLogger.Log($"[ResxSync] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>Called by VS when the window is created and becomes visible for the first time.</summary>
        public override void OnToolWindowCreated()
        {
            try
            {
                base.OnToolWindowCreated();
                if (!_initialized)
                {
                    _initialized = true;
                    _control.OnVisible();
                    OutputLogger.Log("[ResxSync] LanguageManagerWindow.OnToolWindowCreated completed successfully");
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Error in OnToolWindowCreated: {ex.Message}");
                OutputLogger.Log($"[ResxSync] Stack trace: {ex.StackTrace}");
                throw;
            }
        }
    }
}
