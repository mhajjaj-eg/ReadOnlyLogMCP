using ReadOnlyLogMCP;

var builder = WebApplication.CreateBuilder(args);

builder.Services
	.AddOptions<LogAccessOptions>()
	.Bind(builder.Configuration.GetSection(LogAccessOptions.SectionName))
	.Validate(options => !string.IsNullOrWhiteSpace(options.LogRoot), "LogAccess:LogRoot is required.")
	.Validate(options => options.MaxLinesPerRead > 0, "LogAccess:MaxLinesPerRead must be greater than 0.")
	.Validate(options => options.MaxLinesPerRangeRead > 0, "LogAccess:MaxLinesPerRangeRead must be greater than 0.")
	.Validate(options => options.MaxSearchResults > 0, "LogAccess:MaxSearchResults must be greater than 0.")
	.Validate(options => options.MaxFileSizeBytes > 0, "LogAccess:MaxFileSizeBytes must be greater than 0.")
	.Validate(options => options.MaxBundleFiles > 0, "LogAccess:MaxBundleFiles must be greater than 0.")
	.Validate(options => options.MaxBundleBytes > 0, "LogAccess:MaxBundleBytes must be greater than 0.")
	.Validate(options => options.MaxBundleRangeDays > 0, "LogAccess:MaxBundleRangeDays must be greater than 0.")
	.Validate(options => Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out _), "LogAccess:PublicBaseUrl must be an absolute URL.")
	.ValidateOnStart();

builder.Services.AddSingleton<LogQueryService>();

builder.Services.AddCors(options =>
{
	options.AddDefaultPolicy(policy =>
	{
		policy
			.AllowAnyOrigin()
			.AllowAnyHeader()
			.AllowAnyMethod();
	});
});

builder.Services
	.AddMcpServer()
#pragma warning disable MCP9004
	.WithHttpTransport(options =>
	{
		options.EnableLegacySse = true;
	})
#pragma warning restore MCP9004
	.WithToolsFromAssembly();

var app = builder.Build();

app.UseCors();

app.MapGet("/", (IConfiguration configuration) => Results.Ok(new
{
	name = "ReadOnlyLogMCP",
	mcpEndpoint = "/mcp",
	downloadFileEndpoint = "/downloads/log-file?directoryName={directoryName}&relativePath={relativePath}",
	downloadBundleEndpoint = "/downloads/log-bundle?directoryName={directoryName}&startDate=yyyy-MM-dd&endDate=yyyy-MM-dd&recursive=true",
	legacySse = new
	{
		sse = "/mcp/sse",
		message = "/mcp/message"
	},
	configuredLogRoot = configuration[$"{LogAccessOptions.SectionName}:{nameof(LogAccessOptions.LogRoot)}"]
}));

app.MapGet("/downloads/log-bundle", async (HttpContext httpContext, LogQueryService logQueryService, string directoryName, string startDate, string endDate, bool recursive, CancellationToken cancellationToken) =>
{
	if (!DateOnly.TryParse(startDate, out var parsedStartDate))
	{
		return Results.BadRequest(new { error = "startDate must be a valid date in yyyy-MM-dd format." });
	}

	if (!DateOnly.TryParse(endDate, out var parsedEndDate))
	{
		return Results.BadRequest(new { error = "endDate must be a valid date in yyyy-MM-dd format." });
	}

	var selection = logQueryService.SelectLogBundle(directoryName, parsedStartDate, parsedEndDate, recursive);
	if (selection.Error is not null)
	{
		return Results.BadRequest(new { error = selection.Error });
	}

	if (selection.Count == 0)
	{
		return Results.NotFound(new { error = "No log files matched the requested date range." });
	}

	var bundleName = $"{directoryName}-{parsedStartDate:yyyyMMdd}-{parsedEndDate:yyyyMMdd}.zip";
	httpContext.Response.StatusCode = StatusCodes.Status200OK;
	httpContext.Response.ContentType = "application/zip";
	httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{bundleName}\"";

	await logQueryService.WriteLogBundleAsync(httpContext.Response.Body, selection, cancellationToken);
	return Results.Empty;
});

app.MapGet("/downloads/log-file", async (HttpContext httpContext, LogQueryService logQueryService, string directoryName, string relativePath, CancellationToken cancellationToken) =>
{
	var access = logQueryService.GetLogFileAccess(directoryName, relativePath);
	if (access.Error is not null)
	{
		return Results.BadRequest(new { error = access.Error });
	}

	httpContext.Response.StatusCode = StatusCodes.Status200OK;
	httpContext.Response.ContentType = "application/octet-stream";
	httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{access.FileName}\"";

	await logQueryService.WriteLogFileAsync(httpContext.Response.Body, directoryName, relativePath, cancellationToken);
	return Results.Empty;
});

app.MapMcp("/mcp");

app.Run();
