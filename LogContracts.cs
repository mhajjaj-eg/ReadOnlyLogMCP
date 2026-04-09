namespace ReadOnlyLogMCP;

public sealed record DirectoryListResult(
    string LogRoot,
    IReadOnlyList<string> Directories,
    string? Error = null);

public sealed record LogFileItem(
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastWriteUtc);

public sealed record LogFileListResult(
    string DirectoryName,
    bool Recursive,
    int Count,
    bool Truncated,
    IReadOnlyList<LogFileItem> Files,
    string? Error = null);

public sealed record FileReadResult(
    string DirectoryName,
    string RelativePath,
    int RequestedLines,
    int ReturnedLines,
    bool Truncated,
    string Content,
    string? Error = null);

public sealed record FileRangeReadResult(
    string DirectoryName,
    string RelativePath,
    int StartLine,
    int RequestedLineCount,
    int ReturnedLines,
    bool TruncatedBefore,
    bool TruncatedAfter,
    string Content,
    string? Error = null);

public sealed record SearchMatch(
    string RelativePath,
    int LineNumber,
    string LineText);

public sealed record SearchLogsResult(
    string DirectoryName,
    string Query,
    bool Recursive,
    int Count,
    bool Truncated,
    IReadOnlyList<SearchMatch> Matches,
    IReadOnlyList<string> Warnings,
    string? Error = null);

public sealed record LatestErrorEntry(
    string RelativePath,
    DateTimeOffset FileLastWriteUtc,
    string LineText);

public sealed record LatestErrorsResult(
    string DirectoryName,
    int Count,
    bool Truncated,
    IReadOnlyList<LatestErrorEntry> Entries,
    IReadOnlyList<string> Warnings,
    string? Error = null);

public sealed record LogBundleFileItem(
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastWriteUtc);

public sealed record LogBundleSelectionResult(
    string DirectoryName,
    DateOnly StartDate,
    DateOnly EndDate,
    bool Recursive,
    int Count,
    long TotalBytes,
    IReadOnlyList<LogBundleFileItem> Files,
    string? Error = null);

public sealed record LogBundleDownloadUrlResult(
    string Status,
    string DirectoryName,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool Recursive,
    int Count,
    long TotalBytes,
    string DownloadUrl,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> AvailableDirectories,
    string? Message = null,
    string? Error = null);