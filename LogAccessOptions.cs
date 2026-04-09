using System.ComponentModel.DataAnnotations;

namespace ReadOnlyLogMCP;

public sealed class LogAccessOptions
{
    public const string SectionName = "LogAccess";

    public string PublicBaseUrl { get; init; } = "http://localhost:5080";

    [Required]
    public string LogRoot { get; init; } = @"C:\Logs";

    [Range(1, 5000)]
    public int MaxLinesPerRead { get; init; } = 300;

    [Range(1, 10000)]
    public int MaxLinesPerRangeRead { get; init; } = 500;

    [Range(1, 1000)]
    public int MaxSearchResults { get; init; } = 100;

    [Range(1024, 104857600)]
    public long MaxFileSizeBytes { get; init; } = 1048576;

    [Range(1, 5000)]
    public int MaxBundleFiles { get; init; } = 200;

    [Range(1024, 1073741824)]
    public long MaxBundleBytes { get; init; } = 52428800;

    [Range(1, 366)]
    public int MaxBundleRangeDays { get; init; } = 31;
}