using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ResxEditor
{
    /// <summary>
    /// Creates a new localised .resx stub and a matching .xlf stub for a given language tag.
    /// The XLF is seeded with all keys from the base resx, all marked state="new" (untranslated).
    /// </summary>
    internal static class LanguageCreator
    {
        private static readonly XNamespace Xliff = "urn:oasis:names:tc:xliff:document:1.2";

        /// <summary>
        /// Creates the new language files for <paramref name="langTag"/> in <paramref name="projectDir"/>.
        /// Returns a summary of what was created.
        /// </summary>
        public static LanguageCreateResult Create(string projectDir, string langTag)
        {
            var result = new LanguageCreateResult { LangTag = langTag };

            string projectName = FindProjectName(projectDir);
            string mrDir = Path.Combine(FindProjectRoot(projectDir), "MultilingualResources");
            Directory.CreateDirectory(mrDir);

            // Find every base .resx in the project (files whose name has no language dot, e.g. Strings.resx).
            string[] baseResxFiles = FindBaseResxFiles(projectDir);
            if (baseResxFiles.Length == 0)
            {
                result.Error = "No base .resx files found in project.";
                return result;
            }

            // Build the XLF path.
            string xlfPath = Path.Combine(mrDir, $"{projectName}.{langTag}.xlf");
            if (File.Exists(xlfPath))
            {
                result.Error = $"{Path.GetFileName(xlfPath)} already exists.";
                return result;
            }

            // Try to find an existing XLF to use as structural template.
            string? templateXlf = Directory.GetFiles(mrDir, $"{projectName}.*.xlf").FirstOrDefault();

            // Collect all base-resx entries grouped by resx file.
            var resxGroups = new List<(string resxPath, Dictionary<string, string> keys)>();
            foreach (string rx in baseResxFiles)
                resxGroups.Add((rx, ReadResxKeys(rx)));

            // Create XLF.
            XDocument xlfDoc = templateXlf != null
                ? BuildXlfFromTemplate(templateXlf, langTag, resxGroups)
                : BuildXlfFromScratch(projectName, langTag, resxGroups);

            SaveXml(xlfDoc, xlfPath);
            result.XlfPath = xlfPath;

            // Create empty localised .resx for each base resx.
            foreach (var (resxPath, _) in resxGroups)
            {
                string dir = Path.GetDirectoryName(resxPath)!;
                string baseNoExt = Path.GetFileNameWithoutExtension(resxPath);
                string locResxPath = Path.Combine(dir, $"{baseNoExt}.{langTag}.resx");
                if (!File.Exists(locResxPath))
                {
                    SaveXml(CreateEmptyResx(), locResxPath);
                    result.ResxPaths.Add(locResxPath);
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ XLF builders

        private static XDocument BuildXlfFromTemplate(
            string templatePath, string langTag,
            List<(string resxPath, Dictionary<string, string> keys)> resxGroups)
        {
            XDocument tmpl = XDocument.Load(templatePath);
            XElement? tmplFile = tmpl.Root?.Element(Xliff + "file");
            string sourceLang = tmplFile != null ? ((string?)tmplFile.Attribute("source-language") ?? "en") : "en";

            return BuildXlfDocument(sourceLang, langTag, resxGroups, tmpl);
        }

        private static XDocument BuildXlfFromScratch(
            string projectName, string langTag,
            List<(string resxPath, Dictionary<string, string> keys)> resxGroups)
        {
            return BuildXlfDocument("en", langTag, resxGroups, null);
        }

        private static XDocument BuildXlfDocument(
            string sourceLang, string targetLang,
            List<(string resxPath, Dictionary<string, string> keys)> resxGroups,
            XDocument? template)
        {
            // Build <header> from template if available.
            XElement? headerTemplate = template?.Root
                ?.Element(Xliff + "file")
                ?.Element(Xliff + "header");

            var groups = new List<XElement>();
            foreach (var (resxPath, keys) in resxGroups)
            {
                string resxFileName = Path.GetFileName(resxPath).ToUpperInvariant();
                var transUnits = keys.Select(kv =>
                    new XElement(Xliff + "trans-unit",
                        new XAttribute("id", kv.Key),
                        new XAttribute("translate", "yes"),
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        new XElement(Xliff + "source", kv.Value),
                        new XElement(Xliff + "target",
                            new XAttribute("state", "new"),
                            string.Empty)));

                groups.Add(new XElement(Xliff + "group",
                    new XAttribute("id", resxFileName),
                    new XAttribute("datatype", "resx"),
                    transUnits));
            }

            // Determine 'original' from first resx.
            string original = resxGroups.Count > 0
                ? Path.GetFileName(resxGroups[0].resxPath).ToUpperInvariant()
                : "STRINGS.RESX";

            var fileChildren = new List<object>();
            if (headerTemplate != null)
                fileChildren.Add(new XElement(headerTemplate)); // clone header
            fileChildren.Add(new XElement(Xliff + "body", groups));

            var fileElement = new XElement(Xliff + "file",
                new XAttribute("original", original),
                new XAttribute("source-language", sourceLang),
                new XAttribute("target-language", targetLang),
                new XAttribute("datatype", "xml"),
                fileChildren);

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(Xliff + "xliff",
                    new XAttribute("version", "1.2"),
                    new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                    fileElement));
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
                    if (string.IsNullOrEmpty(name) || data.Attribute("type") != null)
                        continue;
                    if (name.StartsWith(">", StringComparison.Ordinal))
                        continue;
                    dict[name] = (string?)data.Element("value") ?? string.Empty;
                }
            }
            catch { /* best effort */ }
            return dict;
        }

        private static string[] FindBaseResxFiles(string projectDir)
        {
            return Directory.GetFiles(projectDir, "*.resx", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string nameNoExt = Path.GetFileNameWithoutExtension(f);
                    return !nameNoExt.Contains('.');       // "Strings" has no dot; "Strings.es" does
                })
                .ToArray();
        }

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

        private static XDocument CreateEmptyResx()
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
                        new XElement("value", "System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"))));
        }

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

    internal sealed class LanguageCreateResult
    {
        public string LangTag { get; set; } = string.Empty;
        public string? XlfPath { get; set; }
        public List<string> ResxPaths { get; } = new List<string>();
        public string? Error { get; set; }
        public bool Success => Error == null;
    }
}
