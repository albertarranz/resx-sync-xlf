using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ResxEditor
{
    /// <summary>
    /// Converts a project without localization to support translation workflows.
    /// Creates localized .resx files (with sample entry) and .xlf files grouped by base resx.
    /// Supports merging into existing .xlf files.
    /// </summary>
    internal static class ProjectLanguageConverter
    {
        private static readonly XNamespace Xliff = "urn:oasis:names:tc:xliff:document:1.2";

        /// <summary>
        /// Converts a project to support translation by creating localized .resx and .xlf files
        /// for the selected language tags. Merges into existing .xlf files if they exist.
        /// </summary>
        public static ProjectConversionResult Convert(
            string projectDir,
            string projectName,
            List<string> selectedLanguageTags)
        {
            var result = new ProjectConversionResult { ProjectName = projectName };

            try
            {
                if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir))
                {
                    result.Error = "Project directory does not exist.";
                    OutputLogger.Log($"[ResxSync] Conversion failed: {result.Error}");
                    return result;
                }

                if (selectedLanguageTags == null || selectedLanguageTags.Count == 0)
                {
                    result.Error = "No languages selected.";
                    OutputLogger.Log($"[ResxSync] Conversion failed: {result.Error}");
                    return result;
                }

                OutputLogger.Log($"[ResxSync] Starting conversion for project: {projectName}");

                // Discover all base .resx files
                string[] baseResxFiles = FindBaseResxFiles(projectDir);
                if (baseResxFiles.Length == 0)
                {
                    // No base resx found — create Strings.resx with a sample entry
                    string baseResxPath = Path.Combine(projectDir, "Strings.resx");
                    XDocument sampleDoc = CreateResxWithSampleEntry();
                    SaveXml(sampleDoc, baseResxPath);
                    result.CreatedResxFiles.Add(baseResxPath);
                    baseResxFiles = new[] { baseResxPath };
                    OutputLogger.Log($"[ResxSync]   Created base {Path.GetFileName(baseResxPath)}");
                }

                OutputLogger.Log($"[ResxSync] Found {baseResxFiles.Length} base .resx files:");
                foreach (string file in baseResxFiles)
                    OutputLogger.Log($"[ResxSync]   - {Path.GetFileName(file)}");

                // Ensure MultilingualResources folder exists
                string mrDir = Path.Combine(FindProjectRoot(projectDir), "MultilingualResources");
                Directory.CreateDirectory(mrDir);
                OutputLogger.Log($"[ResxSync] Ensured MultilingualResources folder exists: {mrDir}");

                // Read all keys from base resx files, grouped by file
                var resxGroups = new List<(string resxPath, string resxFileName, Dictionary<string, string> keys)>();
                foreach (string resxPath in baseResxFiles)
                {
                    var keys = ReadResxKeys(resxPath);
                    string resxFileName = Path.GetFileNameWithoutExtension(resxPath).ToUpperInvariant();
                    resxGroups.Add((resxPath, resxFileName, keys));
                }

                // Process each selected language
                foreach (string langTag in selectedLanguageTags)
                {
                    OutputLogger.Log($"[ResxSync] Processing language: {langTag}");

                    // Create localized .resx files
                    foreach (var (resxPath, _, _) in resxGroups)
                    {
                        try
                        {
                            string dir = Path.GetDirectoryName(resxPath)!;
                            string baseNoExt = Path.GetFileNameWithoutExtension(resxPath);
                            string locResxPath = Path.Combine(dir, $"{baseNoExt}.{langTag}.resx");

                            if (!File.Exists(locResxPath))
                            {
                                XDocument resxDoc = CreateResxWithSampleEntry();
                                SaveXml(resxDoc, locResxPath);
                                result.CreatedResxFiles.Add(locResxPath);
                                OutputLogger.Log($"[ResxSync]   Created {Path.GetFileName(locResxPath)}");
                            }
                            else
                            {
                                OutputLogger.Log($"[ResxSync]   Skipped {Path.GetFileName(locResxPath)} (already exists)");
                            }
                        }
                        catch (Exception ex)
                        {
                            OutputLogger.Log($"[ResxSync]   Error creating localized resx: {ex.Message}");
                        }
                    }

                    // Create or merge .xlf file
                    try
                    {
                        string xlfPath = Path.Combine(mrDir, $"{projectName}.{langTag}.xlf");

                        if (File.Exists(xlfPath))
                        {
                            // Merge into existing XLF
                            int mergedCount = MergeXlf(xlfPath, resxGroups);
                            if (mergedCount > 0)
                            {
                                result.MergedXlfFiles.Add(xlfPath);
                                OutputLogger.Log($"[ResxSync]   Merged {mergedCount} new entries into {Path.GetFileName(xlfPath)}");
                            }
                            else
                            {
                                OutputLogger.Log($"[ResxSync]   {Path.GetFileName(xlfPath)} already up-to-date");
                            }
                        }
                        else
                        {
                            // Create new XLF
                            int createdCount = CreateXlf(xlfPath, projectName, langTag, resxGroups);
                            result.CreatedXlfFiles.Add(xlfPath);
                            result.TotalEntriesAdded += createdCount;
                            OutputLogger.Log($"[ResxSync]   Created {Path.GetFileName(xlfPath)} with {createdCount} entries");
                        }
                    }
                    catch (Exception ex)
                    {
                        OutputLogger.Log($"[ResxSync]   Error creating/merging xlf: {ex.Message}");
                    }
                }

                OutputLogger.Log($"[ResxSync] Conversion complete!");
                OutputLogger.Log($"[ResxSync] Summary: {result.CreatedResxFiles.Count} resx files created, " +
                    $"{result.CreatedXlfFiles.Count} xlf files created, {result.MergedXlfFiles.Count} xlf files merged");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                OutputLogger.Log($"[ResxSync] Conversion failed with exception: {ex.Message}");
            }

            return result;
        }

        // ====================================================================
        // XLF Creation
        // ====================================================================

        /// <summary>
        /// Creates a new XLF file with groups for each base resx file.
        /// All entries are marked as state="new" (untranslated).
        /// Returns the count of entries added.
        /// </summary>
        private static int CreateXlf(
            string xlfPath,
            string projectName,
            string targetLanguage,
            List<(string resxPath, string resxFileName, Dictionary<string, string> keys)> resxGroups)
        {
            var groups = new List<XElement>();
            int totalEntries = 0;

            foreach (var (_, resxFileName, keys) in resxGroups)
            {
                var transUnits = keys.Select(kv =>
                    new XElement(Xliff + "trans-unit",
                        new XAttribute("id", kv.Key),
                        new XAttribute("translate", "yes"),
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        new XElement(Xliff + "source", kv.Value),
                        new XElement(Xliff + "target",
                            new XAttribute("state", "new"),
                            string.Empty))).ToList();

                groups.Add(new XElement(Xliff + "group",
                    new XAttribute("id", resxFileName),
                    new XAttribute("datatype", "resx"),
                    transUnits));

                totalEntries += transUnits.Count;
            }

            // Determine 'original' from first resx
            string original = resxGroups.Count > 0 ? resxGroups[0].resxFileName : "STRINGS.RESX";

            var fileElement = new XElement(Xliff + "file",
                new XAttribute("original", original),
                new XAttribute("source-language", "en"),
                new XAttribute("target-language", targetLanguage),
                new XAttribute("datatype", "xml"),
                new XElement(Xliff + "body", groups));

            var xlfDoc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(Xliff + "xliff",
                    new XAttribute("version", "1.2"),
                    new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                    fileElement));

            SaveXml(xlfDoc, xlfPath);
            return totalEntries;
        }

        // ====================================================================
        // XLF Merge
        // ====================================================================

        /// <summary>
        /// Merges new entries from resx files into an existing XLF file.
        /// Preserves existing translations, only adds new keys.
        /// Returns the count of entries added.
        /// </summary>
        private static int MergeXlf(
            string xlfPath,
            List<(string resxPath, string resxFileName, Dictionary<string, string> keys)> resxGroups)
        {
            int mergedCount = 0;

            try
            {
                XDocument doc = XDocument.Load(xlfPath);
                XElement? fileElement = doc.Root?.Element(Xliff + "file");
                if (fileElement == null)
                    return 0;

                XElement? body = fileElement.Element(Xliff + "body");
                if (body == null)
                    return 0;

                // Process each resx group
                foreach (var (_, resxFileName, keys) in resxGroups)
                {
                    // Find or create group for this resx
                    XElement? group = body.Elements(Xliff + "group")
                        .FirstOrDefault(g => (string?)g.Attribute("id") == resxFileName);

                    if (group == null)
                    {
                        // Create new group for this resx
                        group = new XElement(Xliff + "group",
                            new XAttribute("id", resxFileName),
                            new XAttribute("datatype", "resx"));
                        body.Add(group);
                    }

                    // Collect existing trans-unit ids in this group
                    var existingIds = new HashSet<string>(
                        group.Elements(Xliff + "trans-unit")
                             .Select(tu => (string?)tu.Attribute("id") ?? string.Empty),
                        StringComparer.Ordinal);

                    // Add missing ones
                    foreach (var kv in keys)
                    {
                        if (existingIds.Contains(kv.Key))
                            continue;

                        XElement transUnit = new XElement(Xliff + "trans-unit",
                            new XAttribute("id", kv.Key),
                            new XAttribute("translate", "yes"),
                            new XAttribute(XNamespace.Xml + "space", "preserve"),
                            new XElement(Xliff + "source", kv.Value),
                            new XElement(Xliff + "target",
                                new XAttribute("state", "new"),
                                string.Empty));

                        group.Add(transUnit);
                        existingIds.Add(kv.Key);
                        mergedCount++;
                    }
                }

                if (mergedCount > 0)
                {
                    SaveXml(doc, xlfPath);
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Failed to merge xlf '{xlfPath}': {ex.Message}");
            }

            return mergedCount;
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        /// <summary>
        /// Reads all string entries from a .resx file.
        /// Skips entries with type attribute (non-string) and metadata entries (starting with ">").
        /// </summary>
        private static Dictionary<string, string> ReadResxKeys(string resxPath)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                XDocument doc = XDocument.Load(resxPath);
                foreach (XElement data in doc.Root!.Elements("data"))
                {
                    string? name = (string?)data.Attribute("name");
                    if (string.IsNullOrEmpty(name))
                        continue;

                    // Skip designer/metadata entries (type attribute present)
                    if (data.Attribute("type") != null)
                        continue;

                    // Skip .resx metadata keys like >>xxx
                    if (name.StartsWith(">", StringComparison.Ordinal))
                        continue;

                    string value = (string?)data.Element("value") ?? string.Empty;
                    dict[name] = value;
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Failed to read resx '{resxPath}': {ex.Message}");
            }
            return dict;
        }

        /// <summary>
        /// Finds all base .resx files in the project (files with no language tag).
        /// Example: Strings.resx (included), Strings.es.resx (excluded)
        /// </summary>
        private static string[] FindBaseResxFiles(string projectDir)
        {
            return Directory.GetFiles(projectDir, "*.resx", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string nameNoExt = Path.GetFileNameWithoutExtension(f);
                    // Base files have no dots in the name (e.g., "Strings", not "Strings.es")
                    return !nameNoExt.Contains('.');
                })
                .ToArray();
        }

        /// <summary>
        /// Walks up from startDir to find the nearest .csproj and returns its name without extension.
        /// Falls back to the start directory name if none is found.
        /// </summary>
        private static string FindProjectRoot(string startDir)
        {
            string? dir = startDir;
            while (dir != null)
            {
                if (Directory.GetFiles(dir, "*.csproj").Length > 0)
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            return startDir;
        }

        /// <summary>
        /// Creates a minimal .resx file with one sample entry.
        /// </summary>
        private static XDocument CreateResxWithSampleEntry()
        {
            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("root",
                    new XElement("resheader", new XAttribute("name", "resmimetype"),
                        new XElement("value", "text/microsoft-resx")),
                    new XElement("resheader", new XAttribute("name", "version"),
                        new XElement("value", "2.0")),
                    new XElement("resheader", new XAttribute("name", "reader"),
                        new XElement("value", "System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")),
                    new XElement("resheader", new XAttribute("name", "writer"),
                        new XElement("value", "System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")),
                    new XElement("data", new XAttribute("name", "_Sample"), new XAttribute(XNamespace.Xml + "space", "preserve"),
                        new XElement("value", "Sample entry"))));
        }

        /// <summary>
        /// Saves an XDocument to disk with UTF-8 encoding and proper formatting.
        /// </summary>
        private static void SaveXml(XDocument doc, string path)
        {
            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                OmitXmlDeclaration = false,
            };
            using var writer = System.Xml.XmlWriter.Create(path, settings);
            doc.Save(writer);
        }
    }

    /// <summary>
    /// Result of a project conversion operation.
    /// </summary>
    internal sealed class ProjectConversionResult
    {
        public string ProjectName { get; set; } = string.Empty;
        public List<string> CreatedResxFiles { get; set; } = new();
        public List<string> CreatedXlfFiles { get; set; } = new();
        public List<string> MergedXlfFiles { get; set; } = new();
        public int TotalEntriesAdded { get; set; }
        public string? Error { get; set; }
        public bool Success => Error == null;
    }
}
