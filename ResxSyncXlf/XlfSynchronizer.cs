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
        /// Returns a list of (xlfPath, addedCount) for logging.
        /// </summary>
        public static List<(string xlfPath, int added)> Synchronize(string resxPath)
        {
            var results = new List<(string, int)>();

            if (!File.Exists(resxPath))
                return results;

            // Read all data keys from the resx (skip metadata/assembly entries).
            var resxKeys = ReadResxKeys(resxPath);
            if (resxKeys.Count == 0)
                return results;

            // Find .xlf files in the MultilingualResources folder next to or above the .resx.
            // MAT naming: {ProjectName}.{language}.xlf  (e.g. HT.SCA.Language.es.xlf)
            string dir = Path.GetDirectoryName(resxPath)!;
            string projectName = FindProjectName(dir);

            // MultilingualResources may be a sibling of the .resx or a sibling of any ancestor up to the project root.
            string[] xlfFiles = FindXlfFiles(dir, projectName);

            foreach (string xlfPath in xlfFiles)
            {
                int added = SyncXlf(resxPath, resxKeys, xlfPath);
                results.Add((xlfPath, added));
            }

            return results;
        }

        // ------------------------------------------------------------------ helpers

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

                    // Skip designer/metadata entries (type attribute present → not a string).
                    if (data.Attribute("type") != null)
                        continue;

                    // Also skip .resx metadata keys like >>xxx.
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

        private static int SyncXlf(string resxPath, Dictionary<string, string> resxKeys, string xlfPath)
        {
            int added = 0;
            try
            {
                XDocument doc = XDocument.Load(xlfPath);
                XElement? fileElement = doc.Root!.Element(Xliff + "file");
                if (fileElement == null)
                    return 0;

                XElement? body = fileElement.Element(Xliff + "body");
                if (body == null)
                    return 0;

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

                // Collect existing trans-unit ids.
                var existingIds = new HashSet<string>(
                    group.Elements(Xliff + "trans-unit")
                         .Select(tu => (string?)tu.Attribute("id") ?? string.Empty),
                    StringComparer.Ordinal);

                // Append missing ones.
                foreach (var kv in resxKeys)
                {
                    if (existingIds.Contains(kv.Key))
                        continue;

                    XElement transUnit = BuildTransUnit(kv.Key, kv.Value);
                    group.Add(transUnit);
                    existingIds.Add(kv.Key);
                    added++;
                }

                if (added > 0)
                {
                    // Preserve original XML declaration and encoding.
                    SaveXml(doc, xlfPath);
                }
            }
            catch (Exception ex)
            {
                OutputLogger.Log($"[ResxSync] Failed to sync xlf '{xlfPath}': {ex.Message}");
            }
            return added;
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
