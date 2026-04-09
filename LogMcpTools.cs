using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ReadOnlyLogMCP;

[McpServerToolType]
public sealed class LogMcpTools(LogQueryService logQueryService)
{
    [McpServerTool, Description("Lists the immediate subdirectories under the configured log root.")]
    public DirectoryListResult list_log_directories()
    {
        return logQueryService.ListLogDirectories();
    }

    [McpServerTool, Description("Lists .log, .txt, and .json files under a selected log directory.")]
    public LogFileListResult list_log_files(
        [Description("Immediate child directory name under the configured log root.")] string directoryName,
        [Description("Set to true to search subdirectories under the selected directory.")] bool recursive = false)
    {
        return logQueryService.ListLogFiles(directoryName, recursive);
    }

    [McpServerTool, Description("Reads a log file from the start and caps the response by line count and size limits.")]
    public FileReadResult read_log_file(
        [Description("Immediate child directory name under the configured log root.")] string directoryName,
        [Description("Relative path to the file inside the selected directory.")] string relativePath,
        [Description("Optional line cap. Values above configuration are reduced automatically.")] int? maxLines = null)
    {
        return logQueryService.ReadLogFile(directoryName, relativePath, maxLines);
    }

    [McpServerTool, Description("Reads a bounded line range from a log file for targeted investigation without exposing the whole file.")]
    public FileRangeReadResult read_log_file_range(
        [Description("Immediate child directory name under the configured log root.")] string directoryName,
        [Description("Relative path to the file inside the selected directory.")] string relativePath,
        [Description("1-based line number where reading should begin.")] int startLine,
        [Description("Number of lines to return. Values above configuration are reduced automatically.")] int lineCount)
    {
        return logQueryService.ReadLogFileRange(directoryName, relativePath, startLine, lineCount);
    }

    [McpServerTool, Description("Returns the last N lines from a log file without exposing the whole file.")]
    public FileReadResult tail_log_file(
        [Description("Immediate child directory name under the configured log root.")] string directoryName,
        [Description("Relative path to the file inside the selected directory.")] string relativePath,
        [Description("Optional number of trailing lines to return. Values above configuration are reduced automatically.")] int? lines = null)
    {
        return logQueryService.TailLogFile(directoryName, relativePath, lines);
    }

    [McpServerTool, Description("Searches supported log files for case-insensitive text matches.")]
    public SearchLogsResult search_logs(
        [Description("Immediate child directory name under the configured log root.")] string directoryName,
        [Description("Case-insensitive text to search for.")] string query,
        [Description("Set to true to search subdirectories under the selected directory.")] bool recursive = false,
        [Description("Optional maximum number of matches to return. Values above configuration are reduced automatically.")] int? maxResults = null)
    {
        return logQueryService.SearchLogs(directoryName, query, recursive, maxResults);
    }

    [McpServerTool, Description("Returns recent log lines that look like errors, exceptions, failures, or fatal events.")]
    public LatestErrorsResult latest_errors(
        [Description("Immediate child directory name under the configured log root.")] string directoryName,
        [Description("Optional maximum number of entries to return. Values above configuration are reduced automatically.")] int? maxResults = null)
    {
        return logQueryService.LatestErrors(directoryName, maxResults);
    }

    [McpServerTool, Description("Guides bundle download preparation. If directoryName is missing, it returns available directories. If dates are missing, it returns which fields are still needed. When all inputs are present and valid, it returns a ready-to-use HTTP download URL.")]
    public LogBundleDownloadUrlResult get_log_bundle_download_url(
        [Description("Immediate child directory name under the configured log root. Optional. If omitted, the tool returns available directory options.")] string? directoryName = null,
        [Description("Start date in yyyy-MM-dd format. Optional. If omitted, the tool asks for it in the response.")] string? startDate = null,
        [Description("End date in yyyy-MM-dd format. Optional. If omitted, the tool asks for it in the response.")] string? endDate = null,
        [Description("Set to true to search subdirectories under the selected directory.")] bool recursive = true)
    {
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            var options = logQueryService.GetDirectoryOptions();
            return new LogBundleDownloadUrlResult(
                "needs_directory",
                string.Empty,
                null,
                null,
                recursive,
                0,
                0,
                string.Empty,
                new[] { "directoryName" },
                options,
                "Select one of the availableDirectories values, then provide startDate and endDate in yyyy-MM-dd format.");
        }

        var missingFields = new List<string>();
        if (string.IsNullOrWhiteSpace(startDate))
        {
            missingFields.Add("startDate");
        }

        if (string.IsNullOrWhiteSpace(endDate))
        {
            missingFields.Add("endDate");
        }

        if (missingFields.Count > 0)
        {
            return new LogBundleDownloadUrlResult(
                "needs_dates",
                directoryName,
                null,
                null,
                recursive,
                0,
                0,
                string.Empty,
                missingFields,
                Array.Empty<string>(),
                "Provide the missing date fields in yyyy-MM-dd format.");
        }

        if (!DateOnly.TryParse(startDate, out var parsedStartDate))
        {
            return new LogBundleDownloadUrlResult(
                "invalid_input",
                directoryName,
                null,
                null,
                recursive,
                0,
                0,
                string.Empty,
                new[] { "startDate" },
                Array.Empty<string>(),
                "startDate must be a valid date in yyyy-MM-dd format.",
                "startDate must be a valid date in yyyy-MM-dd format.");
        }

        if (!DateOnly.TryParse(endDate, out var parsedEndDate))
        {
            return new LogBundleDownloadUrlResult(
                "invalid_input",
                directoryName,
                parsedStartDate,
                null,
                recursive,
                0,
                0,
                string.Empty,
                new[] { "endDate" },
                Array.Empty<string>(),
                "endDate must be a valid date in yyyy-MM-dd format.",
                "endDate must be a valid date in yyyy-MM-dd format.");
        }

        return logQueryService.CreateLogBundleDownloadUrl(directoryName, parsedStartDate, parsedEndDate, recursive);
    }
}