using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles
{
    public class EpubFixService : IExecute<EpubFixCommand>
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public EpubFixService(IAuthorService authorService,
                               IBookService bookService,
                               IMediaFileService mediaFileService,
                               IDiskProvider diskProvider,
                               Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public void Execute(EpubFixCommand message)
        {
            var targets = new List<BookFile>();

            if (message.BookId.HasValue)
            {
                var book = _bookService.GetBook(message.BookId.Value);
                if (book != null)
                {
                    targets.AddRange(book.BookFiles.Value);
                }
            }
            else if (message.AuthorId.HasValue)
            {
                var author = _authorService.GetAuthor(message.AuthorId.Value);
                if (author != null)
                {
                    foreach (var b in author.Books.Value)
                    {
                        targets.AddRange(b.BookFiles.Value);
                    }
                }
            }
            else
            {
                // Entire library
                foreach (var author in _authorService.GetAllAuthors())
                {
                    foreach (var b in author.Books.Value)
                    {
                        targets.AddRange(b.BookFiles.Value);
                    }
                }
            }

            if (targets.Count == 0)
            {
                _logger.ProgressInfo("EPUB Fix: No book files found for the selected scope.");
                return;
            }

            _logger.ProgressInfo("EPUB Fix: Processing {0} file(s)", targets.Count);

            var fixedCount = 0;
            foreach (var bf in targets)
            {
                var path = bf.Path;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!_diskProvider.FileExists(path))
                {
                    continue;
                }

                try
                {
                    var fixes = FixEpub(path);
                    if (fixes.Count > 0)
                    {
                        fixedCount++;
                        _logger.Debug("EPUB fixes applied to {0}: {1}", path, string.Join(", ", fixes));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "EPUB Fix: Failed to process {0}", path);
                }
            }

            _logger.ProgressInfo("EPUB Fix: Completed. Files fixed: {0}", fixedCount);
        }

        private static List<string> FixEpub(string epubPath)
        {
            var fixedProblems = new List<string>();

            var tempFile = Path.Combine(Path.GetDirectoryName(epubPath) ?? ".", Path.GetFileNameWithoutExtension(epubPath) + ".tmp.epub");

            var textFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var binFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            using (var src = ZipFile.OpenRead(epubPath))
            {
                foreach (var entry in src.Entries)
                {
                    var name = entry.FullName;
                    var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
                    if (name.Equals("mimetype", StringComparison.OrdinalIgnoreCase) ||
                        new[] { "html", "xhtml", "htm", "xml", "svg", "css", "opf", "ncx" }.Contains(ext))
                    {
                        using (var stream = entry.Open())
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        {
                            textFiles[name] = reader.ReadToEnd();
                        }
                    }
                    else
                    {
                        using (var stream = entry.Open())
                        using (var ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            binFiles[name] = ms.ToArray();
                        }
                    }
                }
            }

            // Skip if already cleaned
            if (IsAlreadyCleaned(textFiles))
            {
                return fixedProblems;
            }

            FixEncoding(textFiles, fixedProblems);
            FixBodyIdLink(textFiles, fixedProblems);
            FixBookLanguage(textFiles, fixedProblems);
            FixStrayImg(textFiles, fixedProblems);
            MarkCleaned(textFiles, fixedProblems);

            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }

            using (var dest = ZipFile.Open(tempFile, ZipArchiveMode.Create))
            {
                if (textFiles.TryGetValue("mimetype", out var mimetype))
                {
                    var e = dest.CreateEntry("mimetype", CompressionLevel.NoCompression);
                    using (var s = e.Open())
                    using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                    {
                        w.Write(mimetype);
                    }
                }

                foreach (var kvp in textFiles)
                {
                    if (kvp.Key.Equals("mimetype", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var e = dest.CreateEntry(kvp.Key, CompressionLevel.Optimal);
                    using (var s = e.Open())
                    using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                    {
                        w.Write(kvp.Value);
                    }
                }

                foreach (var kvp in binFiles)
                {
                    var e = dest.CreateEntry(kvp.Key, CompressionLevel.Optimal);
                    using (var s = e.Open())
                    {
                        s.Write(kvp.Value, 0, kvp.Value.Length);
                    }
                }
            }

            File.Delete(epubPath);
            File.Move(tempFile, epubPath);

            return fixedProblems;
        }

        private static bool IsAlreadyCleaned(Dictionary<string, string> files)
        {
            if (!files.TryGetValue("META-INF/container.xml", out var containerXml))
            {
                return false;
            }

            try
            {
                var xdoc = new XmlDocument();
                xdoc.LoadXml(containerXml);
                var rootfiles = xdoc.GetElementsByTagName("rootfile");
                string opfPath = null;
                foreach (XmlElement rf in rootfiles)
                {
                    var mt = rf.GetAttribute("media-type");
                    if (string.Equals(mt, "application/oebps-package+xml", StringComparison.OrdinalIgnoreCase))
                    {
                        opfPath = rf.GetAttribute("full-path");
                        break;
                    }
                }

                if (string.IsNullOrEmpty(opfPath) || !files.ContainsKey(opfPath))
                {
                    return false;
                }

                var opfDoc = new XmlDocument();
                opfDoc.LoadXml(files[opfPath]);
                var metaNode = opfDoc.GetElementsByTagName("metadata").Cast<XmlElement>().FirstOrDefault();
                if (metaNode == null)
                {
                    return false;
                }

                foreach (XmlElement el in metaNode.GetElementsByTagName("meta"))
                {
                    var name = el.GetAttribute("name");
                    var content = el.GetAttribute("content");
                    if (string.Equals(name, "readarr:cleaned", StringComparison.OrdinalIgnoreCase) && string.Equals(content, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static void MarkCleaned(Dictionary<string, string> files, List<string> fixedProblems)
        {
            if (!files.TryGetValue("META-INF/container.xml", out var containerXml))
            {
                return;
            }

            try
            {
                var xdoc = new XmlDocument();
                xdoc.LoadXml(containerXml);
                var rootfiles = xdoc.GetElementsByTagName("rootfile");
                string opfPath = null;
                foreach (XmlElement rf in rootfiles)
                {
                    var mt = rf.GetAttribute("media-type");
                    if (string.Equals(mt, "application/oebps-package+xml", StringComparison.OrdinalIgnoreCase))
                    {
                        opfPath = rf.GetAttribute("full-path");
                        break;
                    }
                }

                if (string.IsNullOrEmpty(opfPath) || !files.ContainsKey(opfPath))
                {
                    return;
                }

                var opfDoc = new XmlDocument();
                opfDoc.PreserveWhitespace = true;
                opfDoc.LoadXml(files[opfPath]);

                var metaNode = opfDoc.GetElementsByTagName("metadata").Cast<XmlElement>().FirstOrDefault();
                if (metaNode == null)
                {
                    metaNode = opfDoc.CreateElement("metadata");
                    opfDoc.DocumentElement?.AppendChild(metaNode);
                }

                var meta = opfDoc.CreateElement("meta");
                meta.SetAttribute("name", "readarr:cleaned");
                meta.SetAttribute("content", "true");
                metaNode.AppendChild(meta);

                files[opfPath] = opfDoc.OuterXml;
                fixedProblems.Add("Marked EPUB as cleaned.");
            }
            catch
            {
            }
        }

        private static void FixEncoding(Dictionary<string, string> files, List<string> fixedProblems)
        {
            var encodingDecl = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";
            var xmlDeclRegex = new Regex("^<\\?xml\\s+version=[\"'][\\d.]+[\"']\\s+encoding=[\"'][a-zA-Z\\d-.]+[\"'].*?\\?>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (var key in files.Keys.ToList())
            {
                var ext = Path.GetExtension(key).TrimStart('.').ToLowerInvariant();
                if (ext == "html" || ext == "xhtml")
                {
                    var html = files[key].TrimStart();
                    if (!xmlDeclRegex.IsMatch(html))
                    {
                        html = encodingDecl + "\n" + html;
                        fixedProblems.Add($"Fixed encoding for file {key}");
                    }

                    files[key] = html;
                }
            }
        }

        private static void FixBodyIdLink(Dictionary<string, string> files, List<string> fixedProblems)
        {
            var bodyIdRegex = new Regex("<body[^>]*?id=\"([^\"]+)\"[^>]*>", RegexOptions.IgnoreCase);
            var bodyIdList = new List<(string src, string target)>();

            foreach (var kvp in files)
            {
                var ext = Path.GetExtension(kvp.Key).TrimStart('.').ToLowerInvariant();
                if (ext == "html" || ext == "xhtml")
                {
                    var match = bodyIdRegex.Match(kvp.Value);
                    if (match.Success && match.Groups.Count > 1)
                    {
                        var bodyId = match.Groups[1].Value;
                        if (!string.IsNullOrEmpty(bodyId))
                        {
                            var baseName = Path.GetFileName(kvp.Key);
                            var src = baseName + "#" + bodyId;
                            var target = baseName;
                            bodyIdList.Add((src, target));
                        }
                    }
                }
            }

            foreach (var key in files.Keys.ToList())
            {
                var content = files[key];
                foreach (var (src, target) in bodyIdList)
                {
                    if (content.IndexOf(src, StringComparison.Ordinal) >= 0)
                    {
                        content = content.Replace(src, target);
                        fixedProblems.Add($"Replaced link target {src} with {target} in file {key}.");
                    }
                }

                files[key] = content;
            }
        }

        private static void FixBookLanguage(Dictionary<string, string> files, List<string> fixedProblems)
        {
            const string language = "en";

            if (!files.TryGetValue("META-INF/container.xml", out var containerXml))
            {
                return;
            }

            try
            {
                var xdoc = new XmlDocument();
                xdoc.LoadXml(containerXml);
                var rootfiles = xdoc.GetElementsByTagName("rootfile");
                string opfPath = null;
                foreach (XmlElement rf in rootfiles)
                {
                    var mt = rf.GetAttribute("media-type");
                    if (string.Equals(mt, "application/oebps-package+xml", StringComparison.OrdinalIgnoreCase))
                    {
                        opfPath = rf.GetAttribute("full-path");
                        break;
                    }
                }

                if (string.IsNullOrEmpty(opfPath) || !files.ContainsKey(opfPath))
                {
                    return;
                }

                var opfDoc = new XmlDocument();
                opfDoc.PreserveWhitespace = true;
                opfDoc.LoadXml(files[opfPath]);

                var metaNode = opfDoc.GetElementsByTagName("metadata").Cast<XmlElement>().FirstOrDefault();
                if (metaNode == null)
                {
                    metaNode = opfDoc.CreateElement("metadata");
                    opfDoc.DocumentElement?.AppendChild(metaNode);
                }

                XmlElement langNode = null;
                foreach (XmlElement el in metaNode.GetElementsByTagName("dc:language"))
                {
                    langNode = el;
                    break;
                }

                if (langNode == null)
                {
                    foreach (XmlElement el in metaNode.GetElementsByTagName("language"))
                    {
                        langNode = el;
                        break;
                    }
                }

                var original = langNode?.InnerText ?? "undefined";
                if (langNode == null)
                {
                    langNode = opfDoc.CreateElement("dc:language");
                    langNode.InnerText = language;
                    metaNode.AppendChild(langNode);
                }
                else
                {
                    langNode.InnerText = language;
                }

                files[opfPath] = opfDoc.OuterXml;
                if (!string.Equals(original, language, StringComparison.OrdinalIgnoreCase))
                {
                    fixedProblems.Add($"Change document language from {original} to {language}.");
                }
            }
            catch
            {
                // ignore malformed XML
            }
        }

        private static void FixStrayImg(Dictionary<string, string> files, List<string> fixedProblems)
        {
            var imgNoSrcRegex = new Regex("<img(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase);

            foreach (var key in files.Keys.ToList())
            {
                var ext = Path.GetExtension(key).TrimStart('.').ToLowerInvariant();
                if (ext == "html" || ext == "xhtml")
                {
                    var content = files[key];
                    var newContent = imgNoSrcRegex.Replace(content, string.Empty);
                    if (!string.Equals(content, newContent, StringComparison.Ordinal))
                    {
                        files[key] = newContent;
                        fixedProblems.Add($"Remove stray image tag(s) in {key}");
                    }
                }
            }
        }
    }
}
