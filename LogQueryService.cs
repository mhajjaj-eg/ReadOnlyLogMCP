using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ReadOnlyLogMCP;

public sealed class LogQueryService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log",
        ".txt",
        ".json"
    };

    private static readonly Regex ErrorPattern = new(
        @"\b(error|exception|fail(?:ed|ure)?|fatal|critical)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly LogAccessOptions _options;
    private readonly ILogger<LogQueryService> _logger;
    private readonly StringComparison _pathComparison;

    public LogQueryService(IOptions<LogAccessOptions> options, ILogger<LogQueryService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public DirectoryListResult ListLogDirectories()
    {
        try
        {
            var rootPath = GetExistingRootPath();
            var directories = Directory
                .EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new DirectoryListResult(rootPath, directories);
        }
        catch (LogAccessException ex)
        {
            return new DirectoryListResult(GetConfiguredRootPath(), Array.Empty<string>(), ex.Message);
        }
    }

    public LogFileListResult ListLogFiles(string directoryName, bool recursive)
    {
        try
        {
            var directoryPath = ResolveDirectory(directoryName);
            var items = new List<LogFileItem>();
            var truncated = false;

            foreach (var filePath in EnumerateLogFiles(directoryPath, recursive))
            {
                if (items.Count >= _options.MaxSearchResults)
                {
                    truncated = true;
                    break;
                }

                var info = new FileInfo(filePath);
                items.Add(new LogFileItem(
                    NormalizeRelativePath(Path.GetRelativePath(directoryPath, filePath)),
                    info.Length,
                    DateTimeOffset.FromFileTime(info.LastWriteTimeUtc.ToFileTimeUtc())));
            }

            return new LogFileListResult(directoryName, recursive, items.Count, truncated, items);
        }
        catch (LogAccessException ex)
        {
            return new LogFileListResult(directoryName, recursive, 0, false, Array.Empty<LogFileItem>(), ex.Message);
        }
    }

    public FileReadResult ReadLogFile(string directoryName, string relativePath, int? maxLines)
    {
        var requestedLines = ClampLines(maxLines);
        var displayPath = FormatDisplayPath(relativePath);

        try
        {
            var filePath = ResolveFile(directoryName, relativePath);
            var content = ReadFromStart(filePath, requestedLines, out var returnedLines, out var truncated);

            return new FileReadResult(
                directoryName,
                displayPath,
                requestedLines,
                returnedLines,
                truncated,
                content);
        }
        catch (LogAccessException ex)
        {
            return new FileReadResult(directoryName, displayPath, requestedLines, 0, false, string.Empty, ex.Message);
        }
    }

    public FileReadResult TailLogFile(string directoryName, string relativePath, int? lines)
    {
        var requestedLines = ClampLines(lines);
        var displayPath = FormatDisplayPath(relativePath);

        try
        {
            var filePath = ResolveFile(directoryName, relativePath);
            var content = ReadFromEnd(filePath, requestedLines, out var returnedLines, out var truncated);

            return new FileReadResult(
                directoryName,
                displayPath,
                requestedLines,
                returnedLines,
                truncated,
                content);
        }
        catch (LogAccessException ex)
        {
            return new FileReadResult(directoryName, displayPath, requestedLines, 0, false, string.Empty, ex.Message);
        }
    }

    public FileRangeReadResult ReadLogFileRange(string directoryName, string relativePath, int startLine, int lineCount)
    {
        var requestedLineCount = ClampRangeLines(lineCount);
        var displayPath = FormatDisplayPath(relativePath);

        if (startLine < 1)
        {
            return new FileRangeReadResult(directoryName, displayPath, startLine, requestedLineCount, 0, false, false, string.Empty, "startLine must be greater than or equal to 1.");
        }

        try
        {
            var filePath = ResolveFile(directoryName, relativePath);
            var content = ReadLineRange(filePath, startLine, requestedLineCount, out var returnedLines, out var truncatedBefore, out var truncatedAfter);

            return new FileRangeReadResult(
                directoryName,
                displayPath,
                startLine,
                requestedLineCount,
                returnedLines,
                truncatedBefore,
                truncatedAfter,
                content);
        }
        catch (LogAccessException ex)
        {
            return new FileRangeReadResult(directoryName, displayPath, startLine, requestedLineCount, 0, false, false, string.Empty, ex.Message);
        }
    }

    public SearchLogsResult SearchLogs(string directoryName, string query, bool recursive, int? maxResults)
    {
        var effectiveMaxResults = ClampSearchResults(maxResults);

        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchLogsResult(directoryName, string.Empty, recursive, 0, false, Array.Empty<SearchMatch>(), Array.Empty<string>(), "query is required.");
        }

        try
        {
            var directoryPath = ResolveDirectory(directoryName);
            var matches = new List<SearchMatch>();
            var warnings = new List<string>();
            var truncated = false;

            foreach (var filePath in EnumerateLogFiles(directoryPath, recursive))
            {
                var relative = NormalizeRelativePath(Path.GetRelativePath(directoryPath, filePath));

                try
                {
                    using var stream = OpenReadStream(filePath);
                    using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

                    var lineNumber = 0;
                    while (!reader.EndOfStream)
                    {
                        var line = reader.ReadLine() ?? string.Empty;
                        lineNumber++;

                        if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        matches.Add(new SearchMatch(relative, lineNumber, line));
                        if (matches.Count >= effectiveMaxResults)
                        {
                            truncated = true;
                            break;
                        }
                    }
                }
                catch (IOException)
                {
                    warnings.Add($"Skipped '{relative}' because it is locked or unavailable.");
                }
                catch (UnauthorizedAccessException)
                {
                    warnings.Add($"Skipped '{relative}' because access was denied.");
                }

                if (truncated)
                {
                    break;
                }
            }

            return new SearchLogsResult(directoryName, query, recursive, matches.Count, truncated, matches, warnings);
        }
        catch (LogAccessException ex)
        {
            return new SearchLogsResult(directoryName, query, recursive, 0, false, Array.Empty<SearchMatch>(), Array.Empty<string>(), ex.Message);
        }
    }

    public LatestErrorsResult LatestErrors(string directoryName, int? maxResults)
    {
        var effectiveMaxResults = ClampSearchResults(maxResults);

        try
        {
            var directoryPath = ResolveDirectory(directoryName);
            var warnings = new List<string>();
            var entries = new List<LatestErrorEntry>();
            var truncated = false;
            var files = EnumerateLogFiles(directoryPath, recursive: true)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            foreach (var file in files)
            {
                var relative = NormalizeRelativePath(Path.GetRelativePath(directoryPath, file.FullName));

                try
                {
                    var tailText = ReadFromEnd(file.FullName, Math.Max(effectiveMaxResults * 20, 200), out _, out _);
                    var lines = SplitLines(tailText);

                    for (var index = lines.Count - 1; index >= 0; index--)
                    {
                        var line = lines[index];
                        if (!ErrorPattern.IsMatch(line))
                        {
                            continue;
                        }

                        entries.Add(new LatestErrorEntry(relative, file.LastWriteTimeUtc, line));
                        if (entries.Count >= effectiveMaxResults)
                        {
                            truncated = true;
                            break;
                        }
                    }
                }
                catch (IOException)
                {
                    warnings.Add($"Skipped '{relative}' because it is locked or unavailable.");
                }
                catch (UnauthorizedAccessException)
                {
                    warnings.Add($"Skipped '{relative}' because access was denied.");
                }

                if (truncated)
                {
                    break;
                }
            }

            return new LatestErrorsResult(directoryName, entries.Count, truncated, entries, warnings);
        }
        catch (LogAccessException ex)
        {
            return new LatestErrorsResult(directoryName, 0, false, Array.Empty<LatestErrorEntry>(), Array.Empty<string>(), ex.Message);
        }
    }

    public LogBundleSelectionResult SelectLogBundle(string directoryName, DateOnly startDate, DateOnly endDate, bool recursive)
    {
        try
        {
            if (endDate < startDate)
            {
                throw new LogAccessException("endDate must be greater than or equal to startDate.");
            }

            var requestedRangeDays = endDate.DayNumber - startDate.DayNumber + 1;
            if (requestedRangeDays > _options.MaxBundleRangeDays)
            {
                throw new LogAccessException($"Requested date range exceeds the configured limit of {_options.MaxBundleRangeDays} days.");
            }

            var directoryPath = ResolveDirectory(directoryName);
            var files = new List<LogBundleFileItem>();
            long totalBytes = 0;

            foreach (var filePath in EnumerateLogFiles(directoryPath, recursive))
            {
                var info = new FileInfo(filePath);
                var fileDate = DateOnly.FromDateTime(info.LastWriteTime);
                if (fileDate < startDate || fileDate > endDate)
                {
                    continue;
                }

                if (files.Count >= _options.MaxBundleFiles)
                {
                    throw new LogAccessException($"Requested bundle exceeds the configured file limit of {_options.MaxBundleFiles} files.");
                }

                totalBytes += info.Length;
                if (totalBytes > _options.MaxBundleBytes)
                {
                    throw new LogAccessException($"Requested bundle exceeds the configured size limit of {_options.MaxBundleBytes} bytes.");
                }

                files.Add(new LogBundleFileItem(
                    NormalizeRelativePath(Path.GetRelativePath(directoryPath, filePath)),
                    info.Length,
                    DateTimeOffset.FromFileTime(info.LastWriteTimeUtc.ToFileTimeUtc())));
            }

            return new LogBundleSelectionResult(directoryName, startDate, endDate, recursive, files.Count, totalBytes, files);
        }
        catch (LogAccessException ex)
        {
            return new LogBundleSelectionResult(directoryName, startDate, endDate, recursive, 0, 0, Array.Empty<LogBundleFileItem>(), ex.Message);
        }
    }

    public LogBundleDownloadUrlResult CreateLogBundleDownloadUrl(string directoryName, DateOnly startDate, DateOnly endDate, bool recursive)
    {
        var selection = SelectLogBundle(directoryName, startDate, endDate, recursive);
        if (selection.Error is not null)
        {
            return new LogBundleDownloadUrlResult(
                "invalid_input",
                directoryName,
                startDate,
                endDate,
                recursive,
                0,
                0,
                string.Empty,
                Array.Empty<string>(),
                GetDirectoryOptions(),
                selection.Error,
                selection.Error);
        }

        var baseUrl = (_options.PublicBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new LogBundleDownloadUrlResult(
                "invalid_input",
                directoryName,
                startDate,
                endDate,
                recursive,
                selection.Count,
                selection.TotalBytes,
                string.Empty,
                Array.Empty<string>(),
                Array.Empty<string>(),
                "LogAccess:PublicBaseUrl is not configured.",
                "LogAccess:PublicBaseUrl is not configured.");
        }

        var query = string.Join("&", new[]
        {
            $"directoryName={Uri.EscapeDataString(directoryName)}",
            $"startDate={Uri.EscapeDataString(startDate.ToString("yyyy-MM-dd"))}",
            $"endDate={Uri.EscapeDataString(endDate.ToString("yyyy-MM-dd"))}",
            $"recursive={recursive.ToString().ToLowerInvariant()}"
        });

        return new LogBundleDownloadUrlResult(
            "ready",
            directoryName,
            startDate,
            endDate,
            recursive,
            selection.Count,
            selection.TotalBytes,
            $"{baseUrl}/downloads/log-bundle?{query}",
            Array.Empty<string>(),
            Array.Empty<string>(),
            "Open the downloadUrl in a browser or HTTP client to download the zip bundle.");
    }

    public IReadOnlyList<string> GetDirectoryOptions()
    {
        return ListLogDirectories().Directories;
    }

    public async Task WriteLogBundleAsync(Stream output, LogBundleSelectionResult selection, CancellationToken cancellationToken)
    {
        if (selection.Error is not null)
        {
            throw new LogAccessException(selection.Error);
        }

        await using var buffer = new MemoryStream();
        var warnings = new List<string>();
        var manifest = new StringBuilder()
            .AppendLine("ReadOnlyLogMCP bundle manifest")
            .AppendLine($"Directory: {selection.DirectoryName}")
            .AppendLine($"StartDate: {selection.StartDate:yyyy-MM-dd}")
            .AppendLine($"EndDate: {selection.EndDate:yyyy-MM-dd}")
            .AppendLine($"Recursive: {selection.Recursive}")
            .AppendLine($"SelectedFiles: {selection.Count}")
            .AppendLine($"TotalBytes: {selection.TotalBytes}")
            .AppendLine();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in selection.Files)
            {
                try
                {
                    var fullPath = ResolveFile(selection.DirectoryName, file.RelativePath);
                    await using var sourceStream = OpenReadStream(fullPath);
                    var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await sourceStream.CopyToAsync(entryStream, cancellationToken);
                    manifest.AppendLine($"Included: {file.RelativePath} ({file.SizeBytes} bytes)");
                }
                catch (IOException)
                {
                    warnings.Add($"Skipped '{file.RelativePath}' because it is locked or unavailable.");
                }
                catch (UnauthorizedAccessException)
                {
                    warnings.Add($"Skipped '{file.RelativePath}' because access was denied.");
                }
            }

            if (warnings.Count > 0)
            {
                manifest.AppendLine().AppendLine("Warnings:");
                foreach (var warning in warnings)
                {
                    manifest.AppendLine(warning);
                }
            }

            var manifestEntry = archive.CreateEntry("_bundle-manifest.txt", CompressionLevel.Fastest);
            await using var manifestStream = manifestEntry.Open();
            await using var writer = new StreamWriter(manifestStream, Encoding.UTF8, leaveOpen: false);
            await writer.WriteAsync(manifest.ToString());
            await writer.FlushAsync(cancellationToken);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(output, cancellationToken);
    }

    private IEnumerable<string> EnumerateLogFiles(string directoryPath, bool recursive)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        return Directory
            .EnumerateFiles(directoryPath, "*", options)
            .Where(IsSupportedLogFile)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);
    }

    private string ReadFromStart(string filePath, int maxLines, out int returnedLines, out bool truncated)
    {
        using var stream = OpenReadStream(filePath);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        var lines = new List<string>(capacity: Math.Min(maxLines, 256));
        truncated = false;

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine() ?? string.Empty;
            lines.Add(line);

            if (lines.Count >= maxLines)
            {
                truncated = !reader.EndOfStream;
                break;
            }

            if (stream.Position >= _options.MaxFileSizeBytes)
            {
                truncated = !reader.EndOfStream;
                break;
            }
        }

        returnedLines = lines.Count;
        return string.Join(Environment.NewLine, lines);
    }

    private string ReadFromEnd(string filePath, int maxLines, out int returnedLines, out bool truncated)
    {
        using var stream = OpenReadStream(filePath);
        if (stream.Length == 0)
        {
            returnedLines = 0;
            truncated = false;
            return string.Empty;
        }

        var buffer = new byte[4096];
        long position = stream.Length;
        long startPosition = 0;
        var newlineCount = 0;

        while (position > 0 && newlineCount <= maxLines)
        {
            var bytesToRead = (int)Math.Min(buffer.Length, position);
            position -= bytesToRead;
            stream.Seek(position, SeekOrigin.Begin);
            var bytesRead = stream.Read(buffer, 0, bytesToRead);

            for (var index = bytesRead - 1; index >= 0; index--)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                newlineCount++;
                if (newlineCount > maxLines)
                {
                    startPosition = position + index + 1;
                    break;
                }
            }

            if (newlineCount > maxLines)
            {
                break;
            }
        }

        var cappedStart = Math.Max(startPosition, stream.Length - _options.MaxFileSizeBytes);
        truncated = cappedStart > 0;

        stream.Seek(cappedStart, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var lines = SplitLines(text);

        if (lines.Count > maxLines)
        {
            lines = lines.Skip(lines.Count - maxLines).ToList();
            truncated = true;
        }

        returnedLines = lines.Count;
        return string.Join(Environment.NewLine, lines);
    }

    private string ReadLineRange(string filePath, int startLine, int lineCount, out int returnedLines, out bool truncatedBefore, out bool truncatedAfter)
    {
        using var stream = OpenReadStream(filePath);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        var lines = new List<string>(capacity: Math.Min(lineCount, 256));
        var currentLine = 0;
        truncatedBefore = startLine > 1;
        truncatedAfter = false;

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine() ?? string.Empty;
            currentLine++;

            if (currentLine < startLine)
            {
                continue;
            }

            lines.Add(line);
            if (lines.Count >= lineCount)
            {
                truncatedAfter = !reader.EndOfStream;
                break;
            }
        }

        returnedLines = lines.Count;
        if (returnedLines == 0 && currentLine < startLine)
        {
            truncatedBefore = currentLine > 0;
        }

        return string.Join(Environment.NewLine, lines);
    }

    private FileStream OpenReadStream(string filePath)
    {
        try
        {
            return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            throw new LogAccessException("The requested log file was not found.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new LogAccessException("The requested log file path was not found.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new LogAccessException("Access to the requested log file was denied.");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to open log file '{FilePath}'.", filePath);
            throw new LogAccessException("The requested log file is locked or unavailable.");
        }
    }

    private string ResolveDirectory(string directoryName)
    {
        var rootPath = GetExistingRootPath();

        if (string.IsNullOrWhiteSpace(directoryName))
        {
            throw new LogAccessException("directoryName is required.");
        }

        var trimmed = directoryName.Trim();
        if (trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new LogAccessException("directoryName must be a single immediate child folder under the configured log root.");
        }

        var directoryPath = Path.GetFullPath(Path.Combine(rootPath, trimmed));
        EnsurePathUnderRoot(rootPath, directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            throw new LogAccessException($"Directory '{trimmed}' was not found under the configured log root.");
        }

        return directoryPath;
    }

    private string ResolveFile(string directoryName, string relativePath)
    {
        var directoryPath = ResolveDirectory(directoryName);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new LogAccessException("relativePath is required.");
        }

        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        if (Path.IsPathRooted(normalizedRelativePath))
        {
            throw new LogAccessException("Absolute paths are not allowed.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(directoryPath, normalizedRelativePath));
        EnsurePathUnderRoot(directoryPath, fullPath);

        if (!IsSupportedLogFile(fullPath))
        {
            throw new LogAccessException("Only .log, .txt, and .json files are allowed.");
        }

        if (!File.Exists(fullPath))
        {
            throw new LogAccessException("The requested log file was not found.");
        }

        return fullPath;
    }

    private void EnsurePathUnderRoot(string rootPath, string candidatePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var normalizedCandidate = Path.GetFullPath(candidatePath);

        if (string.Equals(normalizedRoot, normalizedCandidate, _pathComparison))
        {
            return;
        }

        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(rootWithSeparator, _pathComparison))
        {
            throw new LogAccessException("Path traversal is not allowed.");
        }
    }

    private string GetExistingRootPath()
    {
        var rootPath = GetConfiguredRootPath();
        if (!Directory.Exists(rootPath))
        {
            throw new LogAccessException($"Configured log root '{rootPath}' does not exist.");
        }

        return rootPath;
    }

    private string GetConfiguredRootPath()
    {
        return Path.GetFullPath(_options.LogRoot.Trim());
    }

    private static bool IsSupportedLogFile(string filePath)
    {
        return AllowedExtensions.Contains(Path.GetExtension(filePath));
    }

    private int ClampLines(int? requestedLines)
    {
        return Math.Clamp(requestedLines ?? _options.MaxLinesPerRead, 1, _options.MaxLinesPerRead);
    }

    private int ClampRangeLines(int requestedLines)
    {
        return Math.Clamp(requestedLines, 1, _options.MaxLinesPerRangeRead);
    }

    private int ClampSearchResults(int? requestedResults)
    {
        return Math.Clamp(requestedResults ?? _options.MaxSearchResults, 1, _options.MaxSearchResults);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        var normalized = FormatDisplayPath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new LogAccessException("Path traversal is not allowed.");
        }

        return normalized.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string FormatDisplayPath(string? relativePath)
    {
        return (relativePath ?? string.Empty)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim()
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        using var reader = new StringReader(text);

        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private sealed class LogAccessException(string message) : Exception(message);
}
