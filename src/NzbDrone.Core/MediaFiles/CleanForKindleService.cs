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
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public interface ICleanForKindle
    {
        BookFileMoveResult CleanForKindle(BookFile bookFile, LocalBook localBook, bool copyOnly = false);
    }

    public class CleanForKindleService : ICleanForKindle
    {
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IMoveBookFiles _bookFileMover;
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderService _rootFolderService;
        private readonly ICalibreProxy _calibre;
        private readonly Logger _logger;

        public CleanForKindleService(IRecycleBinProvider recycleBinProvider,
                                       IMediaFileService mediaFileService,
                                       IMetadataTagService metadataTagService,
                                       IMoveBookFiles bookFileMover,
                                       IDiskProvider diskProvider,
                                       IRootFolderService rootFolderService,
                                       ICalibreProxy calibre,
                                       Logger logger)
        {
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _metadataTagService = metadataTagService;
            _bookFileMover = bookFileMover;
            _diskProvider = diskProvider;
            _rootFolderService = rootFolderService;
            _calibre = calibre;
            _logger = logger;
        }

        public BookFileMoveResult CleanForKindle(BookFile bookFile, LocalBook localBook, bool copyOnly = false)
        {
            var moveFileResult = new BookFileMoveResult();
            var existingFiles = localBook.Book.BookFiles.Value;

            var rootFolderPath = _diskProvider.GetParentFolder(localBook.Author.Path);
            var rootFolder = _rootFolderService.GetBestRootFolder(rootFolderPath);
            var isCalibre = rootFolder.IsCalibreLibrary && rootFolder.CalibreSettings != null;

            var settings = rootFolder.CalibreSettings;

            // If there are existing book files and the root folder is missing, throw, so the old file isn't left behind during the import process.
            if (existingFiles.Any() && !_diskProvider.FolderExists(rootFolderPath))
            {
                throw new RootFolderNotFoundException($"Root folder '{rootFolderPath}' was not found.");
            }

            foreach (var file in existingFiles)
            {
                var bookFilePath = file.Path;
                var subfolder = rootFolderPath.GetRelativePath(_diskProvider.GetParentFolder(bookFilePath));

                bookFile.CalibreId = file.CalibreId;

                if (_diskProvider.FileExists(bookFilePath))
                {
                    _logger.Debug("Removing existing book file: {0} CalibreId: {1}", file, file.CalibreId);

                    if (!isCalibre)
                    {
                        _recycleBinProvider.DeleteFile(bookFilePath, subfolder);
                    }
                    else
                    {
                        var existing = _calibre.GetBook(file.CalibreId, settings);
                        var existingFormats = existing.Formats.Keys;
                        _logger.Debug($"Removing existing formats {existingFormats.ConcatToString()} from calibre");
                        _calibre.RemoveFormats(file.CalibreId, existingFormats, settings);
                    }
                }

                moveFileResult.OldFiles.Add(file);
                _mediaFileService.Delete(file, DeleteMediaFileReason.Upgrade);
            }

            if (!isCalibre)
            {
                if (copyOnly)
                {
                    moveFileResult.BookFile = _bookFileMover.CopyBookFile(bookFile, localBook);
                }
                else
                {
                    moveFileResult.BookFile = _bookFileMover.MoveBookFile(bookFile, localBook);
                }

                // Process EPUB fixes on the moved/copied file
                TryFixEpub(moveFileResult.BookFile?.Path);
                _metadataTagService.WriteTags(bookFile, true);
            }
            else
            {
                var source = bookFile.Path;

                // Process EPUB fixes before adding to Calibre
                TryFixEpub(source);

                // Skip Calibre field updates to avoid overwriting metadata; we only want conversion
                moveFileResult.BookFile = _calibre.AddAndConvert(bookFile, settings, updateFields: false);

                if (!copyOnly)
                {
                    _diskProvider.DeleteFile(source);
                }
            }

            return moveFileResult;
        }

        private void TryFixEpub(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!_diskProvider.FileExists(path))
                {
                    return;
                }

                var fixes = FixEpub(path);
                if (fixes.Count > 0)
                {
                    _logger.Debug("EPUB fixes applied to {0}: {1}", path, fixes.ConcatToString());
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to fix EPUB file: {0}", path);
            }
        }

        private List<string> FixEpub(string epubPath)
        {
            var fixedProblems = new List<string>();

            // Use a temporary file to rebuild the zip
            var tempFile = Path.Combine(Path.GetDirectoryName(epubPath) ?? ".", Path.GetFileNameWithoutExtension(epubPath) + ".tmp.epub");

            // Read entries
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

            // Fixes
            FixEncoding(textFiles, fixedProblems);
            FixBodyIdLink(textFiles, fixedProblems);
            FixBookLanguage(textFiles, fixedProblems);
            FixStrayImg(textFiles, fixedProblems);

            // Write back
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }

            using (var dest = ZipFile.Open(tempFile, ZipArchiveMode.Create))
            {
                // Write mimetype first and store only
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

            // Replace original atomically using disk provider
            if (_diskProvider.FileExists(epubPath))
            {
                _diskProvider.MoveFile(tempFile, epubPath, overwrite: true);
            }
            else
            {
                _diskProvider.MoveFile(tempFile, epubPath, overwrite: false);
            }

            return fixedProblems;
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
            // Always set language to en
            const string language = "en";

            if (!files.TryGetValue("META-INF/container.xml", out var containerXml))
            {
                return;
            }

            try
            {
                var xdoc = new XmlDocument();
                xdoc.LoadXml(containerXml);
                var nsMgr = new XmlNamespaceManager(xdoc.NameTable);
                nsMgr.AddNamespace("c", xdoc.DocumentElement?.NamespaceURI ?? "");
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
                    // Create metadata if missing
                    metaNode = opfDoc.CreateElement("metadata");
                    opfDoc.DocumentElement?.AppendChild(metaNode);
                }

                // Try to find dc:language, with or without ns
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
                // Ignore malformed XML
            }
        }

        private static void FixStrayImg(Dictionary<string, string> files, List<string> fixedProblems)
        {
            // Remove <img ...> tags that don't contain a src attribute
            var imgNoSrcRegex = new Regex("<img(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase);

            foreach (var key in files.Keys.ToList())
            {
                var ext = Path.GetExtension(key).TrimStart('.').ToLowerInvariant();
                if (ext == "html" || ext == "xhtml")
                {
                    var content = files[key];
                    var newContent = imgNoSrcRegex.Replace(content, string.Empty);
                    if (!ReferenceEquals(content, newContent) && !string.Equals(content, newContent, StringComparison.Ordinal))
                    {
                        files[key] = newContent;
                        fixedProblems.Add($"Remove stray image tag(s) in {key}");
                    }
                }
            }
        }
    }
}
