using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ResxEditor
{
    /// <summary>
    /// Reverse sync: reads translated entries from XLF files and writes them into
    /// the matching localised .resx files (e.g. Strings.es.resx).
    /// Called before a project build so the compiler sees up-to-date translations.
    /// </summary>
    internal static class XlfToResxSynchronizer
    {
        private static readonly XNamespace Xliff = "urn:oasis:names:tc:xliff:document:1.2";

        /// <summary>
        /// Given the directory of a project, finds all MultilingualResources/*.xlf files,
        /// extracts translated entries, and writes them into localised .resx files.
        /// Returns a list of (resxPath, updatedCount) for logging.
        /// </summary>
        public static List<(string resxPath, int updated)> SynchronizeFromXlf(string projectDir)
        {
            var results = new List<(string, int)>();

            string mrDir = FindMultilingualResourcesDir(projectDir);
            if (mrDir == null)
                return results;

            string projectName = FindProjectName(projectDir);

            foreach (string xlfPath in Directory.GetFiles(mrDir, $"{projectName}.*.xlf"))
            {
                // Extract language tag from filename: ProjectName.es.xlf → "es"
                string xlfName = Path.GetFileNameWithoutExtension(xlfPath); // ProjectName.es
                string langTag = xlfName.Substring(projectName.Length).TrimStart('.');
                if (string.IsNullOrEmpty(langTag))
                    continue;

                foreach (var (resxPath, updated) in SyncXlfToResx(xlfPath, langTag, projectDir))
                    results.Add((resxPath, updated));
            }

            return results;
        }

        // ------------------------------------------------------------------ core

        private static List<(string resxPath, int updated)> SyncXlfToResx(
            string xlfPath, string langTag, string projectDir)
        {
            var results = new List<(string, int)>();
            try
            {
                XDocument xlfDoc = XDocument.Load(xlfPath);
                XElement? fileElement = xlfDoc.Root?.Element(Xliff + "file");
                if (fileElement == null)
                    return results;

                XElement? body = fileElement.Element(Xliff + "body");
                if (body == null)
                    return results;

                // MAT places trans-units inside a <group> whose id is the original resx name.
                // Fall back to a flat body (no group) if needed.
                var groups = body.Elements(Xliff + "group").ToList();
                if (groups.Count == 0)
                {
                    // Treat entire body as one group; derive resx name from <file original>.
                    string original = (string?)fileElement.Attribute("original") ?? string.Empty;
                    string baseResxName = OriginalToResxName(original);
                    string resxPath = ResolveLocalisedResxPath(projectDir, baseResxName, langTag);
                    int updated = ApplyTranslationsToResx(body.Elements(Xliff + "trans-unit"), resxPath);
                    results.Add((resxPath, updated));
                }
                else
                {
                    foreach (XElement group in groups)
                    {
                        string groupId = (string?)group.Attribute("id") ?? string.Empty;
                        string baseResxName = OriginalToResxName(groupId);
                        string resxPath = ResolveLocalisedResxPath(projectDir, baseResxName, langTag);
                        int updated = ApplyTranslationsToResx(group.Elements(Xliff + "trans-unit"), resxPath);
                        results.Add((resxPath, updated));
                    }
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] XlfToResx failed for '{xlfPath}': {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Applies translated trans-unit entries to a localised resx file.
        /// Creates the file if it doesn't exist. Returns the number of entries written/updated.
        /// </summary>
        private static int ApplyTranslationsToResx(
            IEnumerable<XElement> transUnits, string resxPath)
        {
            int updated = 0;
            try
            {
                XDocument resxDoc = File.Exists(resxPath)
                    ? XDocument.Load(resxPath)
                    : CreateEmptyResx();

                XElement root = resxDoc.Root!;

                // Build a lookup of existing data elements by name for fast access.
                var existing = root.Elements("data")
                    .Where(d => d.Attribute("name") != null)
                    .ToDictionary(d => (string)d.Attribute("name")!, StringComparer.Ordinal);

                foreach (XElement tu in transUnits)
                {
                    string? id = (string?)tu.Attribute("id");
                    if (string.IsNullOrEmpty(id))
                        continue;

                    XElement? targetEl = tu.Element(Xliff + "target");
                    if (targetEl == null)
                        continue;

                    // Skip entries that are still marked as untranslated.
                    string state = (string?)targetEl.Attribute("state") ?? string.Empty;
                    if (string.Equals(state, "new", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string translation = targetEl.Value;

                    if (existing.TryGetValue(id, out XElement? dataEl))
                    {
                        // Update only if the value changed.
                        XElement? valueEl = dataEl.Element("value");
                        if (valueEl == null)
                        {
                            dataEl.Add(new XElement("value", translation));
                            updated++;
                        }
                        else if (valueEl.Value != translation)
                        {
                            valueEl.Value = translation;
                            updated++;
                        }
                    }
                    else
                    {
                        // Add new entry.
                        XElement newData = new XElement("data",
                            new XAttribute("name", id),
                            new XAttribute(XNamespace.Xml + "space", "preserve"),
                            new XElement("value", translation));
                        root.Add(newData);
                        existing[id] = newData;
                        updated++;
                    }
                }

                if (updated > 0)
                    SaveResx(resxDoc, resxPath);
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Failed to write resx '{resxPath}': {ex.Message}");
            }
            return updated;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Converts an XLF 'original' attribute value to a base resx filename.
        /// MAT uses uppercase paths like "HT.SCA.LANGUAGE/STRINGS.RESX".
        /// Returns just the filename part, e.g. "Strings.resx".
        /// </summary>
        private static string OriginalToResxName(string original)
        {
            // Strip path separators and take the last segment.
            string name = original.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
                name = name.Substring(slash + 1);

            // Normalise case to Title-case for the resx filename.
            // MAT stores "STRINGS.RESX" — convert to "Strings.resx" so file lookup works.
            if (name == name.ToUpperInvariant())
                name = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());

            return name;
        }

        /// <summary>
        /// Searches for the base resx file in the project tree and returns the path for
        /// the localised variant (e.g. folder/Strings.es.resx).
        /// </summary>
        private static string ResolveLocalisedResxPath(string projectDir, string baseResxName, string langTag)
        {
            string baseNoExt = Path.GetFileNameWithoutExtension(baseResxName); // "Strings"
            string localisedName = $"{baseNoExt}.{langTag}.resx";              // "Strings.es.resx"

            // Try to find the base resx to place the localised file alongside it.
            string[] baseFiles = Directory.GetFiles(projectDir, baseResxName, SearchOption.AllDirectories);
            if (baseFiles.Length > 0)
                return Path.Combine(Path.GetDirectoryName(baseFiles[0])!, localisedName);

            // Fallback: put it in the project root.
            return Path.Combine(projectDir, localisedName);
        }

        private static XDocument CreateEmptyResx()
        {
            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("root",
                    new XElement("resheader",
                        new XAttribute("name", "resmimetype"),
                        new XElement("value", "text/microsoft-resx")),
                    new XElement("resheader",
                        new XAttribute("name", "version"),
                        new XElement("value", "2.0")),
                    new XElement("resheader",
                        new XAttribute("name", "reader"),
                        new XElement("value", "System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")),
                    new XElement("resheader",
                        new XAttribute("name", "writer"),
                        new XElement("value", "System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"))));
        }

        private static void SaveResx(XDocument doc, string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

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

        private static string FindProjectName(string startDir)
        {
            string? dir = startDir;
            while (dir != null)
            {
                string[] projs = Directory.GetFiles(dir, "*.csproj");
                if (projs.Length > 0)
                    return Path.GetFileNameWithoutExtension(projs[0]);
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFileName(startDir) ?? "Project";
        }

        private static string? FindMultilingualResourcesDir(string startDir)
        {
            string? dir = startDir;
            while (dir != null)
            {
                string candidate = Path.Combine(dir, "MultilingualResources");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}
