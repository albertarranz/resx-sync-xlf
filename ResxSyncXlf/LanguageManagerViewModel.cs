using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;

namespace ResxEditor
{
    internal sealed class LanguageManagerViewModel : INotifyPropertyChanged
    {
        private string _solutionDirectory;
        private List<ProjectItem> _solutionProjects = new List<ProjectItem>();

        private ObservableCollection<LanguageItem> _activeLanguages = new ObservableCollection<LanguageItem>();
        public ObservableCollection<LanguageItem> ActiveLanguages
        {
            get => _activeLanguages;
            set { _activeLanguages = value; OnPropertyChanged(); }
        }

        private List<LanguageItem> _allLanguages = new List<LanguageItem>();
        public List<LanguageItem> AllLanguages
        {
            get => _allLanguages;
            set { _allLanguages = value; OnPropertyChanged(); }
        }

        private List<LanguageItem> _availableLanguages = new List<LanguageItem>();
        public List<LanguageItem> AvailableLanguages
        {
            get => _availableLanguages;
            set { _availableLanguages = value; OnPropertyChanged(); }
        }

        private LanguageItem _selectedNewLanguage;
        public LanguageItem SelectedNewLanguage
        {
            get => _selectedNewLanguage;
            set { _selectedNewLanguage = value; OnPropertyChanged(); }
        }

        // Conversion feature properties
        private ObservableCollection<ProjectItem> _projectsForConversion = new();
        public ObservableCollection<ProjectItem> ProjectsForConversion
        {
            get => _projectsForConversion;
            set { _projectsForConversion = value; OnPropertyChanged(); }
        }

        private ProjectItem _selectedConversionProject;
        public ProjectItem SelectedConversionProject
        {
            get => _selectedConversionProject;
            set { _selectedConversionProject = value; OnPropertyChanged(); }
        }

        private ObservableCollection<CheckListBoxLanguageItem> _conversionLanguages = new();
        public ObservableCollection<CheckListBoxLanguageItem> ConversionLanguages
        {
            get => _conversionLanguages;
            set { _conversionLanguages = value; OnPropertyChanged(); }
        }

        private List<CheckListBoxLanguageItem> _conversionLanguagesView = new();
        public List<CheckListBoxLanguageItem> ConversionLanguagesView
        {
            get => _conversionLanguagesView;
            set { _conversionLanguagesView = value; OnPropertyChanged(); }
        }

        private string _conversionLanguageFilter = string.Empty;
        public string ConversionLanguageFilter
        {
            get => _conversionLanguageFilter;
            set
            {
                _conversionLanguageFilter = value;
                OnPropertyChanged();
                ApplyConversionLanguageFilter();
            }
        }

        private bool _showSelectedOnly;
        public bool ShowSelectedOnly
        {
            get => _showSelectedOnly;
            set
            {
                _showSelectedOnly = value;
                OnPropertyChanged();
                ApplyConversionLanguageFilter();
            }
        }

        private bool _isLoading = false;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        private string _conversionStatusMessage = string.Empty;
        public string ConversionStatusMessage
        {
            get => _conversionStatusMessage;
            set { _conversionStatusMessage = value; OnPropertyChanged(); }
        }

        private string _projectsMessage = string.Empty;
        public string ProjectsMessage
        {
            get => _projectsMessage;
            set { _projectsMessage = value; OnPropertyChanged(); }
        }

        public ICommand AddLanguageCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ConvertProjectCommand { get; }
        public ICommand SelectAllCommand { get; }
        public ICommand ClearAllCommand { get; }
        public ICommand FilterSelectedCommand { get; }

        public LanguageManagerViewModel()
        {
            try
            {
                AddLanguageCommand = new RelayCommand(AddLanguage, CanAddLanguage);
                RefreshCommand = new RelayCommand(_ => LoadProjects());
                ConvertProjectCommand = new RelayCommand(_ => ExecuteConversion(), CanExecuteConversion);
                SelectAllCommand = new RelayCommand(_ => { foreach (var l in ConversionLanguages) l.IsSelected = true; });
                ClearAllCommand = new RelayCommand(_ => { foreach (var l in ConversionLanguages) l.IsSelected = false; });
                FilterSelectedCommand = new RelayCommand(_ => ShowSelectedOnly = !ShowSelectedOnly);
                LoadAllLanguages();
                LoadProjects();
                
                OutputLogger.Log("[ResxSync] LanguageManagerViewModel initialized successfully");
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Error in LanguageManagerViewModel constructor: {ex.Message}");
                OutputLogger.Log($"[ResxSync] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public void LoadProjects()
        {
            IsLoading = true;
            StatusMessage = "Scanning solution...";
            ProjectsMessage = "";
            _solutionProjects.Clear();
            _solutionDirectory = null;

            var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;

            if (dte?.Solution == null || !dte.Solution.IsOpen)
            {
                StatusMessage = "No solution open.";
                ActiveLanguages.Clear();
                IsLoading = false;
                return;
            }

            _solutionDirectory = Path.GetDirectoryName(dte.Solution.FullName);

            foreach (EnvDTE.Project p in dte.Solution.Projects)
                CollectProjects(p);

            ProjectsMessage = $"Total language projects detected: {_solutionProjects.Count}";

            // Capture UI dispatcher here, on the UI thread
            var uiDispatcher = Dispatcher.CurrentDispatcher;

            Task.Run(() =>
            {
                try
                {
                    var languages = ScanLanguages();
                    uiDispatcher.Invoke(() =>
                    {
                        UpdateUILanguages(languages);
                        RefreshProjectsForConversion();
                        UpdateConversionLanguages(languages);
                        StatusMessage = languages.Count > 0
                            ? $"Found {languages.Count} language(s)."
                            : "No languages detected.";
                        IsLoading = false;
                    });
                }
                catch (Exception ex)
                {
                    uiDispatcher.Invoke(() =>
                    {
                        StatusMessage = $"Error: {ex.Message}";
                        IsLoading = false;
                    });
                }
            });
        }

        private void CollectProjects(EnvDTE.Project project)
        {
            try
            {
                string projectPath = project.FullName ?? string.Empty;
                string dir;
                if (File.Exists(projectPath))
                    dir = Path.GetDirectoryName(projectPath);
                else if (Directory.Exists(projectPath))
                    dir = projectPath;
                else
                    dir = null;

                if (dir != null)
                {

                    bool hasStringsResx = false;
                    try
                    {
                        var stringsFiles = Directory.GetFiles(dir, "Strings*.resx", SearchOption.AllDirectories);
                        if (stringsFiles.Length > 0)
                            hasStringsResx = true;
                    }
                    catch { }

                    bool hasXlfFiles = false;
                    try
                    {
                        string mrDir = FindMultilingualResourcesDir(dir);
                        if (mrDir != null)
                        {
                            var xlfFiles = Directory.GetFiles(mrDir, "*.xlf");
                            if (xlfFiles.Length > 0)
                                hasXlfFiles = true;
                        }
                    }
                    catch { }

                    if (hasStringsResx || hasXlfFiles)
                        _solutionProjects.Add(new ProjectItem(project.Name, dir));
                }

                if (project.ProjectItems != null)
                {
                    foreach (EnvDTE.ProjectItem item in project.ProjectItems)
                    {
                        try
                        {
                            if (item.SubProject != null)
                                CollectProjects(item.SubProject);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private Dictionary<string, LanguageItem> ScanLanguages()
        {
            var languages = new Dictionary<string, LanguageItem>();

            foreach (var proj in _solutionProjects)
            {
                try
                {
                    foreach (string resxPath in Directory.GetFiles(proj.Directory, "*.resx", SearchOption.AllDirectories))
                    {
                        string filename = Path.GetFileNameWithoutExtension(resxPath);
                        int lastDot = filename.LastIndexOf('.');
                        if (lastDot > 0)
                        {
                            string tag = filename.Substring(lastDot + 1);
                            if (IsValidLanguageTag(tag) && !languages.ContainsKey(tag))
                            {
                                var match = AllLanguages.FirstOrDefault(l =>
                                    string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
                                languages[tag] = match ?? new LanguageItem(tag, tag);
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    string mrDir = FindMultilingualResourcesDir(proj.Directory);
                    if (mrDir != null)
                    {
                        foreach (string xlfPath in Directory.GetFiles(mrDir, "*.xlf"))
                        {
                            string filename = Path.GetFileNameWithoutExtension(xlfPath);
                            int lastDot = filename.LastIndexOf('.');
                            if (lastDot > 0)
                            {
                                string tag = filename.Substring(lastDot + 1);
                                if (IsValidLanguageTag(tag) && !languages.ContainsKey(tag))
                                {
                                    var match = AllLanguages.FirstOrDefault(l =>
                                        string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
                                    languages[tag] = match ?? new LanguageItem(tag, tag);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return languages;
        }

        private void UpdateUILanguages(Dictionary<string, LanguageItem> languages)
        {
            ActiveLanguages.Clear();
            foreach (var lang in languages.Values)
                ActiveLanguages.Add(lang);
            UpdateAvailableLanguages();
        }

        private void UpdateAvailableLanguages()
        {
            var activeTags = new HashSet<string>(
                ActiveLanguages.Select(l => l.Tag), StringComparer.OrdinalIgnoreCase);
            AvailableLanguages = AllLanguages
                .Where(l => !activeTags.Contains(l.Tag))
                .ToList();
        }

        private void UpdateConversionLanguages(Dictionary<string, LanguageItem> languages)
        {
            ConversionLanguages.Clear();
            var source = languages.Count > 0 ? languages.Values : (IEnumerable<LanguageItem>)AllLanguages;
            foreach (var lang in source)
                ConversionLanguages.Add(new CheckListBoxLanguageItem(lang.Tag, lang.Display) { IsSelected = false });
            ApplyConversionLanguageFilter();
        }

        private void ApplyConversionLanguageFilter()
        {
            var filtered = ConversionLanguages.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(_conversionLanguageFilter))
                filtered = filtered.Where(l => l.Display.IndexOf(_conversionLanguageFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            if (_showSelectedOnly)
                filtered = filtered.Where(l => l.IsSelected);

            ConversionLanguagesView = filtered.ToList();
        }

        private void LoadAllLanguages()
        {
            AllLanguages.Clear();
            var cultures = CultureInfo.GetCultures(CultureTypes.AllCultures);
            foreach (var culture in cultures.OrderBy(c => c.DisplayName))
            {
                string tag = culture.IetfLanguageTag;
                string display = $"{culture.DisplayName} ({tag})";
                AllLanguages.Add(new LanguageItem(tag, display));
            }
        }

        private void RefreshProjectsForConversion()
        {
            ProjectsForConversion.Clear();
            var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte?.Solution == null || !dte.Solution.IsOpen)
                return;

            var list = new List<ProjectItem>();
            foreach (EnvDTE.Project p in dte.Solution.Projects)
                CollectAllProjects(p, list);

            foreach (var proj in list)
            {
                if (HasLanguageFiles(proj.Directory))
                    continue;
                ProjectsForConversion.Add(proj);
            }
        }

        private void CollectAllProjects(EnvDTE.Project project, List<ProjectItem> list)
        {
            try
            {
                string dir = null;
                string path = project.FullName ?? string.Empty;
                if (File.Exists(path))
                    dir = Path.GetDirectoryName(path);
                else if (Directory.Exists(path))
                    dir = path;

                if (dir != null)
                    list.Add(new ProjectItem(project.Name, dir));

                if (project.ProjectItems != null)
                {
                    foreach (EnvDTE.ProjectItem item in project.ProjectItems)
                    {
                        try
                        {
                            if (item.SubProject != null)
                                CollectAllProjects(item.SubProject, list);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static bool HasLanguageFiles(string projectDir)
        {
            try
            {
                foreach (string resxPath in Directory.GetFiles(projectDir, "*.resx", SearchOption.AllDirectories))
                {
                    string filename = Path.GetFileNameWithoutExtension(resxPath);
                    int lastDot = filename.LastIndexOf('.');
                    if (lastDot > 0)
                    {
                        string tag = filename.Substring(lastDot + 1);
                        if (IsValidLanguageTag(tag))
                            return true;
                    }
                }
            }
            catch { }

            try
            {
                string mrDir = FindMultilingualResourcesDir(projectDir);
                if (mrDir != null && Directory.GetFiles(mrDir, "*.xlf").Length > 0)
                    return true;
            }
            catch { }

            return false;
        }

        private void InitializeConversionLanguages()
        {
            ConversionLanguages.Clear();
            foreach (var lang in AllLanguages)
            {
                ConversionLanguages.Add(new CheckListBoxLanguageItem(lang.Tag, lang.Display));
            }
        }

        private bool CanAddLanguage(object _)
        {
            return SelectedNewLanguage != null && _solutionProjects.Count > 0;
        }

        private bool CanExecuteConversion(object _)
        {
            return SelectedConversionProject != null && ConversionLanguages.Any(l => l.IsSelected);
        }

        private void ExecuteConversion()
        {
            if (SelectedConversionProject == null)
            {
                ConversionStatusMessage = "Please select a project.";
                return;
            }

            var selectedLanguages = ConversionLanguages
                .Where(l => l.IsSelected)
                .Select(l => l.Tag)
                .ToList();

            if (selectedLanguages.Count == 0)
            {
                ConversionStatusMessage = "Please select at least one language.";
                return;
            }

            IsLoading = true;
            ConversionStatusMessage = "Converting project...";

            try
            {
                var result = ProjectLanguageConverter.Convert(
                    SelectedConversionProject.Directory,
                    SelectedConversionProject.Name,
                    selectedLanguages);

                if (!result.Success)
                {
                    ConversionStatusMessage = $"Conversion failed: {result.Error}";
                }
                else
                {
                    ConversionStatusMessage = $"Successfully converted {SelectedConversionProject.Name} to {selectedLanguages.Count} language(s)!";
                    SelectedConversionProject = null;
                    foreach (var lang in ConversionLanguages)
                        lang.IsSelected = false;
                    OutputLogger.Log("[ResxSync] Project conversion completed successfully");
                    var uiDispatcher = Dispatcher.CurrentDispatcher;
                    Task.Run(() =>
                    {
                        var langs = ScanLanguages();
                        uiDispatcher.Invoke(() => 
                        { 
                            UpdateUILanguages(langs); 
                            RefreshProjectsForConversion();
                            UpdateConversionLanguages(langs);
                            IsLoading = false; 
                        });
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                ConversionStatusMessage = $"Error: {ex.Message}";
                OutputLogger.Log($"[ResxSync] Conversion exception: {ex.Message}");
            }
            IsLoading = false;
        }

        private void AddLanguage(object _)
        {
            if (SelectedNewLanguage == null)
                return;

            IsLoading = true;
            StatusMessage = "Adding language...";

            try
            {
                var allLines = new List<string>();
                foreach (var proj in _solutionProjects)
                {
                    var result = LanguageCreator.Create(proj.Directory, SelectedNewLanguage.Tag);
                    if (!result.Success)
                    {
                        StatusMessage = $"Failed to create language: {result.Error}";
                        IsLoading = false;
                        return;
                    }

                    if (!string.IsNullOrEmpty(result.XlfPath))
                        allLines.Add($"{proj.Name}: {Path.GetFileName(result.XlfPath)}");
                    foreach (var rp in result.ResxPaths)
                        allLines.Add($"{proj.Name}: {Path.GetFileName(rp)}");
                }

                StatusMessage = $"Language '{SelectedNewLanguage.Tag}' added to {_solutionProjects.Count} project(s).";
                OutputLogger.Log($"[ResxSync] Language '{SelectedNewLanguage.Tag}' added: " + string.Join(", ", allLines));
                SelectedNewLanguage = null;
                var uiDispatcher = Dispatcher.CurrentDispatcher;
                Task.Run(() =>
                {
                    var langs = ScanLanguages();
                    uiDispatcher.Invoke(() => 
                    { 
                        UpdateUILanguages(langs); 
                        RefreshProjectsForConversion();
                        UpdateConversionLanguages(langs);
                        IsLoading = false; 
                    });
                });
                return;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed: {ex.Message}";
                OutputLogger.Log($"[ResxSync] AddLanguage error: {ex.Message}");
            }
            IsLoading = false;
        }

        private static bool IsValidLanguageTag(string tag)
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(tag);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FindMultilingualResourcesDir(string startDir)
        {
            string dir = startDir;
            while (dir != null)
            {
                string candidate = Path.Combine(dir, "MultilingualResources");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal sealed class ProjectItem
    {
        public string Name { get; }
        public string Directory { get; }
        public ProjectItem(string name, string dir) { Name = name; Directory = dir; }
        public override string ToString() => Name;
    }

    internal sealed class LanguageItem
    {
        public string Tag { get; }
        public string Display { get; }
        public LanguageItem(string tag, string display) { Tag = tag; Display = display; }
        public override string ToString() => Display;
    }

    internal sealed class CheckListBoxLanguageItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string Tag { get; }
        public string Display { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public CheckListBoxLanguageItem(string tag, string display)
        {
            Tag = tag;
            Display = display;
            _isSelected = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;
        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object p) => _canExecute?.Invoke(p) ?? true;
        public void Execute(object p) => _execute(p);
        public event EventHandler CanExecuteChanged
        {
            add => System.Windows.Input.CommandManager.RequerySuggested += value;
            remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
        }
    }
}
