# ReadOnlyLogMCP

ReadOnlyLogMCP is a standalone ASP.NET Core Model Context Protocol server that exposes application log files in a strictly read-only way for AI agents and MCP clients.

It uses the official `ModelContextProtocol.AspNetCore` SDK package and maps the MCP endpoint at `/mcp` with streamable HTTP enabled. Legacy SSE support is enabled through the SDK's built-in routing, not by custom controller code.

## What It Exposes

The server provides these MCP tools:

- `list_log_directories`
- `list_log_files`
- `read_log_file`
- `read_log_file_range`
- `tail_log_file`
- `search_logs`
- `latest_errors`
- `get_log_bundle_download_url`

All tools are read-only. The server never creates, edits, renames, or deletes files.

The guided download flow is intended for human-driven inspection:

- ask for a folder when none is provided
- ask for `startDate` and `endDate` when missing
- return a validated HTTP download URL when all inputs are present

## Requirements

- .NET 9 SDK or later

The current stable MCP ASP.NET Core package used here is `ModelContextProtocol.AspNetCore` `1.2.0`. That package supports `net8.0`, `net9.0`, and `net10.0`. This project targets `net9.0` as a stable baseline, so the package does not force a single .NET version.

## Run Locally

```bash
dotnet restore
dotnet run --launch-profile http
```

With the included launch settings, the server listens on:

- `http://localhost:5080`

The MCP endpoint is:

- `http://localhost:5080/mcp`

The manual download endpoint is:

- `http://localhost:5080/downloads/log-bundle`

Typical local MCP flow:

1. Call `get_log_bundle_download_url` with no arguments.
2. Pick one of the returned `availableDirectories`.
3. Call it again with `directoryName`.
4. Provide `startDate` and `endDate` in `yyyy-MM-dd`.
5. Open the returned `downloadUrl` in a browser or HTTP client.

## MCP Transport

This project uses the official SDK hosting pattern:

- `AddMcpServer()`
- `WithHttpTransport(...)`
- `MapMcp("/mcp")`

### Streamable HTTP clients

Use the MCP endpoint directly:

- `http://localhost:5080/mcp`

### Legacy SSE clients

Legacy SSE support is enabled by the SDK through `EnableLegacySse = true`.

Use:

- SSE stream: `http://localhost:5080/mcp/sse`
- JSON-RPC POST endpoint: `http://localhost:5080/mcp/message`

No custom SSE controller or manual protocol implementation is used.

## Configuration

Configuration is read from `appsettings.json`, `appsettings.Development.json`, and environment variables.

Default configuration:

```json
{
  "LogAccess": {
    "PublicBaseUrl": "http://localhost:5080",
    "LogRoot": "C:\\Logs",
    "MaxLinesPerRead": 300,
    "MaxLinesPerRangeRead": 500,
    "MaxSearchResults": 100,
    "MaxFileSizeBytes": 1048576,
    "MaxBundleFiles": 200,
    "MaxBundleBytes": 52428800,
    "MaxBundleRangeDays": 31
  }
}
```

Environment variable examples:

```bash
LogAccess__LogRoot=/var/log/myapp
LogAccess__PublicBaseUrl=http://localhost:5080
LogAccess__MaxLinesPerRead=500
LogAccess__MaxLinesPerRangeRead=800
LogAccess__MaxSearchResults=200
LogAccess__MaxFileSizeBytes=2097152
LogAccess__MaxBundleFiles=300
LogAccess__MaxBundleBytes=104857600
LogAccess__MaxBundleRangeDays=14
```

## Full-File Investigation Strategy

This project intentionally keeps MCP reads capped. Instead of allowing uncapped full-file reads for agents, it provides two safer options:

- `read_log_file_range` for bounded, line-based investigation by an AI agent
- `/downloads/log-bundle` for manual human inspection of multiple files in a zip bundle

### Ranged MCP reads

Use `read_log_file_range` when you know the line window you want:

- Inputs: `directoryName`, `relativePath`, `startLine`, `lineCount`

### Manual zip downloads

Use the HTTP download endpoint when you want to collect matching log files for offline inspection:

```text
GET /downloads/log-bundle?directoryName=Billing_API&startDate=2026-01-01&endDate=2026-01-07&recursive=true
```

Notes:

- `startDate` and `endDate` use `yyyy-MM-dd`
- file selection uses the server's local file timestamp date
- the endpoint returns a zip attachment directly
- the bundle includes `_bundle-manifest.txt` with included files and skipped-file warnings

### Download URL helper tool

Use `get_log_bundle_download_url` when you want the MCP server to guide the selection and return a ready-made URL for a human to open in a browser or download tool.

- If `directoryName` is missing, the tool returns `availableDirectories`
- If `startDate` or `endDate` is missing, the tool returns `missingFields`
- When all values are present and valid, the tool returns `downloadUrl`, plus matching file count and total bytes

Example guided responses:

- `status: needs_directory`
- `status: needs_dates`
- `status: ready`

Example complete tool call:

```json
{
  "directoryName": "Billing_API",
  "startDate": "2026-01-07",
  "endDate": "2026-01-07",
  "recursive": true
}
```

## Safety Model

- Access is restricted to the configured log root.
- `directoryName` is limited to immediate child folders under the root.
- File paths are normalized and validated.
- Path traversal attempts are rejected.
- Only `.log`, `.txt`, and `.json` files are exposed.
- Reads are capped by line count and configured maximum file size.
- Ranged MCP reads remain capped instead of allowing unlimited full-file reads.
- Zip downloads are limited by configured file-count, size, and date-range caps.
- Large result sets are truncated.
- Locked or unreadable files are handled with clear error or warning messages.

## Project Structure

- `Program.cs`: ASP.NET Core host and MCP registration
- `LogAccessOptions.cs`: configuration model
- `LogQueryService.cs`: read-only filesystem and log query logic
- `LogMcpTools.cs`: MCP tool surface
- `LogContracts.cs`: response models

## Deploy To IIS

This is a standard ASP.NET Core app using the `Microsoft.NET.Sdk.Web` SDK, so IIS hosting follows the normal ASP.NET Core Module deployment model.

### Prerequisites

On the IIS server, install:

1. IIS with the `Web Server` role.
2. The ASP.NET Core Hosting Bundle for the target runtime.

This project targets `net9.0`, so for a framework-dependent deployment you should install the `.NET 9 ASP.NET Core Hosting Bundle` on the IIS server.

### Publish

From the project root:

```powershell
dotnet publish -c Release -o .\publish
```

That produces a framework-dependent deployment and generates the IIS `web.config` automatically during publish.

If you prefer a self-contained deployment:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o .\publish
```

Even for self-contained deployment, IIS still needs the ASP.NET Core Module from the hosting bundle.

### IIS Site Setup

Recommended setup:

1. Create a dedicated site in IIS rather than hosting under a nested application path.
2. Point the site physical path to the published output folder.
3. Use an Application Pool with:
  `No Managed Code`
4. Bind the site to the hostname and port you want to expose.

### Required Permissions

The IIS application pool identity needs:

1. Read and execute access to the published application folder.
2. Read access to the configured log root, for example `C:\Logs`.

Without read permission on the log root, directory listing and log reads will fail.

### Recommended Configuration For IIS

Set these values for the deployed environment:

```json
{
  "LogAccess": {
   "PublicBaseUrl": "https://your-server-name",
   "LogRoot": "C:\\Logs"
  }
}
```

`PublicBaseUrl` should match the externally reachable IIS site URL, because `get_log_bundle_download_url` uses it to generate the download link.

You can set configuration either in `appsettings.Production.json` or as environment variables in IIS:

```text
ASPNETCORE_ENVIRONMENT=Production
LogAccess__PublicBaseUrl=https://your-server-name
LogAccess__LogRoot=C:\Logs
LogAccess__MaxLinesPerRead=300
LogAccess__MaxLinesPerRangeRead=500
LogAccess__MaxSearchResults=100
LogAccess__MaxFileSizeBytes=1048576
LogAccess__MaxBundleFiles=200
LogAccess__MaxBundleBytes=52428800
LogAccess__MaxBundleRangeDays=31
```

### Verify After Deployment

Once deployed, verify these URLs:

1. `/`
  Expected: JSON status payload.
2. `/mcp`
  Expected: MCP HTTP endpoint for streamable HTTP clients.
3. `/mcp/sse`
  Expected: legacy SSE endpoint if enabled.
4. `/downloads/log-bundle?...`
  Expected: zip download for valid inputs.

### VS Code Connection After IIS Deployment

If the app is deployed to `https://logs.example.com`, configure the MCP server URL in VS Code as:

```json
{
  "type": "http",
  "url": "https://logs.example.com/mcp"
}
```

### Operational Notes

1. Use HTTPS in front of IIS for any non-local deployment.
2. Restrict CORS for production instead of leaving it fully open.
3. Keep the app at the IIS site root when possible. It simplifies `PublicBaseUrl` and endpoint paths.
4. If you deploy under a subpath, remember that the effective endpoints become `/<app-path>/mcp` and `/<app-path>/downloads/log-bundle`, and `PublicBaseUrl` should reflect that public path.

## Notes

- CORS is enabled broadly to keep local MCP and browser-based testing simple. Restrict it for production deployments.
- The sample log root uses a Windows path because the primary example is Windows-based. Override it in configuration for Linux or macOS.