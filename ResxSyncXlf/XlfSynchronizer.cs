using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ResxEditor
{
    /// <summary>
    /// Pure logic: reads a .resx file, finds .xlf siblings, and adds missing trans-unit entries.
    /// </summary>
    internal static class XlfSynchronizer
    {
        private static readonly XNamespace Xliff = "urn:oasis:names:tc:xliff:document:1.2";

        /// <summary>
        /// Synchronize all .xlf files that sit next to <paramref name="resxPath"/>.
        /// Returns a list of (xlfPath, addedCount, removedCount, updatedCount) for logging.
        /// </summary>
        public static List<(string xlfPath, int added, int removed, int updated)> Synchronize(string resxPath)
        {
            var results = new List<(string, int, int, int)>();

            if (!File.Exists(resxPath))
                return results;

            // Read all data keys from the resx (skip metadata/assembly entries).
            // Note: resxKeys may legitimately be empty (all entries deleted) — we still need to
            // sync the .xlf files in that case so deleted entries get removed there too.
            var resxKeys = ReadResxKeys(resxPath);

            // Find .xlf files in the MultilingualResources folder next to or above the .resx.
            // MAT naming: {ProjectName}.{language}.xlf  (e.g. HT.SCA.Language.es.xlf)
            string dir = Path.GetDirectoryName(resxPath)!;
            string projectName = FindProjectName(dir);

            // MultilingualResources may be a sibling of the .resx or a sibling of any ancestor up to the project root.
            string[] xlfFiles = FindXlfFiles(dir, projectName);

            foreach (string xlfPath in xlfFiles)
            {
                var (added, removed, updated) = SyncXlf(resxPath, resxKeys, xlfPath);
                results.Add((xlfPath, added, removed, updated));
            }

            return results;
        }

        // ------------------------------------------------------------------ helpers

        private static Dictionary<string, string> ReadResxKeys(string resxPath)
        {
            try
            {
                XDocument doc = XDocument.Load(resxPath);
                return ReadResxKeys(doc);
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Failed to read resx '{resxPath}': {ex.Message}");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        /// <summary>Reads resx data keys/values from a byte snapshot (e.g. captured before a save).</summary>
        public static Dictionary<string, string> ReadResxKeysFromBytes(byte[] resxBytes)
        {
            try
            {
                using var ms = new MemoryStream(resxBytes);
                XDocument doc = XDocument.Load(ms);
                return ReadResxKeys(doc);
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Failed to read resx snapshot: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private static Dictionary<string, string> ReadResxKeys(XDocument doc)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XElement data in doc.Root!.Elements("data"))
            {
                string? name = (string?)data.Attribute("name");
                if (string.IsNullOrEmpty(name))
                    continue;

                // Skip designer/metadata entries (type attribute present → not a string).
                if (data.Attribute("type") != null)
                    continue;

                // Also skip .resx metadata keys like >>xxx.
                if (name.StartsWith(">", StringComparison.Ordinal))
                    continue;

                string value = (string?)data.Element("value") ?? string.Empty;
                dict[name] = value;
            }
            return dict;
        }

        /// <summary>
        /// Detects rename pairs between an old and new snapshot of resx keys, matching a removed key
        /// to an added key when they share the exact same value (a strong signal of a plain rename).
        /// Ambiguous matches (more than one candidate) are left alone so they fall back to add/remove.
        /// </summary>
        public static Dictionary<string, string> DetectRenames(Dictionary<string, string> oldKeys, Dictionary<string, string> newKeys)
        {
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            if (oldKeys.Count == 0 || newKeys.Count == 0)
                return renames;

            var removedKeys = oldKeys.Keys.Where(k => !newKeys.ContainsKey(k)).ToList();
            var addedKeys = new HashSet<string>(newKeys.Keys.Where(k => !oldKeys.ContainsKey(k)), StringComparer.Ordinal);

            foreach (string removedKey in removedKeys)
            {
                string oldValue = oldKeys[removedKey];
                var matches = addedKeys.Where(k => newKeys[k] == oldValue).ToList();
                if (matches.Count == 1)
                {
                    renames[removedKey] = matches[0];
                    addedKeys.Remove(matches[0]);
                }
            }
            return renames;
        }

        /// <summary>
        /// Renames matching data entries in all localised .resx siblings of <paramref name="basePath"/>
        /// (e.g. "Strings.es.resx", "Strings.fr-FR.resx") so a rename in the base resx propagates
        /// instead of leaving the old key orphaned.
        /// Returns a list of (resxPath, renamedCount) for logging.
        /// </summary>
        public static List<(string resxPath, int renamed)> RenameInLocalizedResx(string basePath, Dictionary<string, string> renames)
        {
            var results = new List<(string, int)>();
            if (renames.Count == 0)
                return results;

            string dir = Path.GetDirectoryName(basePath)!;
            string baseNameNoExt = Path.GetFileNameWithoutExtension(basePath); // e.g. "Strings"

            string[] localizedFiles = Directory.GetFiles(dir, $"{baseNameNoExt}.*.resx");
            foreach (string resxFile in localizedFiles)
            {
                int renamed = RenameResxEntries(resxFile, renames);
                results.Add((resxFile, renamed));
            }
            return results;
        }

        private static int RenameResxEntries(string resxPath, Dictionary<string, string> renames)
        {
            int renamed = 0;
            try
            {
                XDocument doc = XDocument.Load(resxPath);
                foreach (XElement data in doc.Root!.Elements("data"))
                {
                    string? name = (string?)data.Attribute("name");
                    if (name != null && renames.TryGetValue(name, out string newName))
                    {
                        data.SetAttributeValue("name", newName);
                        renamed++;
                    }
                }

                if (renamed > 0)
                {
                    doc.Save(resxPath);
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Failed to rename entries in resx '{resxPath}': {ex.Message}");
            }
            return renamed;
        }

        /// <summary>
        /// Removes data entries matching <paramref name="removedKeys"/> from all localised .resx siblings
        /// of <paramref name="basePath"/> (e.g. "Strings.es.resx", "Strings.fr-FR.resx") so a deletion in
        /// the base resx propagates instead of leaving orphaned entries.
        /// Returns a list of (resxPath, removedCount) for logging.
        /// </summary>
        public static List<(string resxPath, int removed)> RemoveInLocalizedResx(string basePath, IEnumerable<string> removedKeys)
        {
            var results = new List<(string, int)>();
            var keySet = new HashSet<string>(removedKeys, StringComparer.Ordinal);
            if (keySet.Count == 0)
                return results;

            string dir = Path.GetDirectoryName(basePath)!;
            string baseNameNoExt = Path.GetFileNameWithoutExtension(basePath); // e.g. "Strings"

            string[] localizedFiles = Directory.GetFiles(dir, $"{baseNameNoExt}.*.resx");
            foreach (string resxFile in localizedFiles)
            {
                int removed = RemoveResxEntries(resxFile, keySet);
                results.Add((resxFile, removed));
            }
            return results;
        }

        private static int RemoveResxEntries(string resxPath, HashSet<string> keysToRemove)
        {
            int removed = 0;
            try
            {
                XDocument doc = XDocument.Load(resxPath);
                foreach (XElement data in doc.Root!.Elements("data").ToList())
                {
                    string? name = (string?)data.Attribute("name");
                    if (name != null && keysToRemove.Contains(name))
                    {
                        data.Remove();
                        removed++;
                    }
                }

                if (removed > 0)
                {
                    doc.Save(resxPath);
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Failed to remove entries in resx '{resxPath}': {ex.Message}");
            }
            return removed;
        }

        private static (int added, int removed, int updated) SyncXlf(string resxPath, Dictionary<string, string> resxKeys, string xlfPath)
        {
            int added = 0;
            int removed = 0;
            int updated = 0;
            try
            {
                XDocument doc = XDocument.Load(xlfPath);
                XElement? fileElement = doc.Root!.Element(Xliff + "file");
                if (fileElement == null)
                    return (0, 0, 0);

                XElement? body = fileElement.Element(Xliff + "body");
                if (body == null)
                    return (0, 0, 0);

                // Find the group (may or may not exist).
                XElement? group = body.Element(Xliff + "group");
                if (group == null)
                {
                    // Create group mirroring the original attribute
                    string original = (string?)fileElement.Attribute("original") ?? Path.GetFileName(resxPath).ToUpperInvariant();
                    group = new XElement(Xliff + "group",
                        new XAttribute("id", original),
                        new XAttribute("datatype", "resx"));
                    body.Add(group);
                }

                // Remove trans-units whose id no longer exists in the resx.
                foreach (XElement tu in group.Elements(Xliff + "trans-unit").ToList())
                {
                    string id = (string?)tu.Attribute("id") ?? string.Empty;
                    if (!resxKeys.ContainsKey(id))
                    {
                        tu.Remove();
                        removed++;
                    }
                }

                // Collect existing trans-unit ids (after removal).
                var existingUnits = group.Elements(Xliff + "trans-unit")
                    .ToDictionary(tu => (string?)tu.Attribute("id") ?? string.Empty, tu => tu, StringComparer.Ordinal);

                // Add missing ones, update source text when the neutral value changed.
                foreach (var kv in resxKeys)
                {
                    if (existingUnits.TryGetValue(kv.Key, out XElement existingTu))
                    {
                        XElement? source = existingTu.Element(Xliff + "source");
                        if (source != null && source.Value != kv.Value)
                        {
                            source.Value = kv.Value;
                            updated++;
                        }
                        continue;
                    }

                    XElement transUnit = BuildTransUnit(kv.Key, kv.Value);
                    group.Add(transUnit);
                    existingUnits[kv.Key] = transUnit;
                    added++;
                }

                if (added > 0 || removed > 0 || updated > 0)
                {
                    // Preserve original XML declaration and encoding.
                    SaveXml(doc, xlfPath);
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Failed to sync xlf '{xlfPath}': {ex.Message}");
            }
            return (added, removed, updated);
        }

        /// <summary>Builds a trans-unit element matching the MAT format.</summary>
        private static XElement BuildTransUnit(string id, string sourceText)
        {
            // Format:
            // <trans-unit id="KEY" translate="yes" xml:space="preserve">
            //   <source>SOURCE</source>
            //   <target state="new">SOURCE</target>
            // </trans-unit>
            return new XElement(Xliff + "trans-unit",
                new XAttribute("id", id),
                new XAttribute("translate", "yes"),
                new XAttribute(XNamespace.Xml + "space", "preserve"),
                new XElement(Xliff + "source", sourceText),
                new XElement(Xliff + "target",
                    new XAttribute("state", "new"),
                    sourceText));
        }

        private static void SaveXml(XDocument doc, string path)
        {
            // Keep the original UTF-8 declaration.
            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                OmitXmlDeclaration = false,
            };

            using var writer = System.Xml.XmlWriter.Create(path, settings);
            doc.Save(writer);
        }

        /// <summary>
        /// Walks up from <paramref name="startDir"/> to find the nearest .csproj and returns its name without extension.
        /// Falls back to the start directory name if none is found.
        /// </summary>
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

        /// <summary>
        /// Searches for a <c>MultilingualResources</c> folder starting at <paramref name="startDir"/> and walking up,
        /// then returns all files matching <c>{projectName}.*.xlf</c> inside it.
        /// </summary>
        private static string[] FindXlfFiles(string startDir, string projectName)
        {
            string? dir = startDir;
            while (dir != null)
            {
                string candidate = Path.Combine(dir, "MultilingualResources");
                if (Directory.Exists(candidate))
                {
                    string[] files = Directory.GetFiles(candidate, $"{projectName}.*.xlf");
                    if (files.Length > 0)
                        return files;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return Array.Empty<string>();
        }
    }
}
