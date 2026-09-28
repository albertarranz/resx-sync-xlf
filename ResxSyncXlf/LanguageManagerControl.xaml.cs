using System;
using System.Windows.Controls;

namespace ResxEditor
{
    public partial class LanguageManagerControl : UserControl
    {
        public LanguageManagerControl()
        {
            try
            {
                InitializeComponent();
                DataContext = new LanguageManagerViewModel();
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Error initializing LanguageManagerControl: {ex.Message}");
                OutputLogger.Log($"[ResxSync] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>Refreshes the project list when the tool window becomes visible.</summary>
        public void OnVisible()
        {
            try
            {
                if (DataContext is LanguageManagerViewModel vm)
                    vm.LoadProjects();
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Error in OnVisible: {ex.Message}");
                OutputLogger.Log($"[ResxSync] Stack trace: {ex.StackTrace}");
            }
        }
    }
}
