# MCP server setup for Claude Desktop

`interfaces/mcp` is a dependency-free stdio MCP server. It exposes the Core tools `diff`, `validate`, `room` and `plan`, and the Revit command catalogue tools, which forward to the add-in pipe. `scripts/install-mcp.ps1` registers it in the Claude Desktop config with one command.

## One-command setup

Quit Claude Desktop first, because it may rewrite its config while running. Then run this from the repository root on Windows or macOS, with the .NET 8 SDK installed:

```powershell
pwsh -NoProfile -File scripts/install-mcp.ps1 -Build
```

`-Build` runs `dotnet build interfaces/mcp --configuration Release --warnaserror` and then registers the result. Without `-Build` the script registers an existing build and refuses if the DLL is missing. To preview the change, add `-DryRun`. It prints the planned file and writes nothing.

| Parameter | Default |
| --- | --- |
| `-ConfigPath` | Windows: `%APPDATA%\Claude\claude_desktop_config.json`; macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`. Linux has no default, so you must pass this parameter there. |
| `-ServerName` | `bim-agentic-os` |
| `-Dotnet` | `.tools/dotnet/dotnet.exe` (Windows, if present), otherwise `dotnet` from `PATH`. The path is resolved to an absolute path. |
| `-McpDll` | `interfaces/mcp/bin/Release/net8.0/Bim.Mcp.dll` in this checkout. The path is resolved to an absolute path. |
| `-Build`, `-DryRun`, `-Uninstall` | off |

Register the DLL inside this checkout. Do not copy it elsewhere: the Revit write policy is located relative to the DLL, and if no policy is found, every Revit write is refused.

## What it changes

It changes only `mcpServers.<ServerName>` in that one file:

```json
{ "mcpServers": { "bim-agentic-os": { "command": "C:\\Program Files\\dotnet\\dotnet.exe", "args": ["C:\\src\\bim-agentic-os\\interfaces\\mcp\\bin\\Release\\net8.0\\Bim.Mcp.dll"] } } }
```

- **Missing config:** the folder and file are created. No backup is made, because there is nothing to back up.
- **Existing config:** the file is parsed first. If it has invalid JSON, duplicate keys, a top level that is not an object, or an `mcpServers` that is not an object, the script exits non-zero and changes nothing.
- **Other content:** every other key and server is preserved. An existing entry with the same name is replaced in place, and any extra keys it had, such as `env`, are dropped.
- **Backup:** before any change to an existing file, a byte-exact copy `claude_desktop_config.json.bak-<yyyyMMddTHHmmssfffZ>` (UTC) is written next to it, and its path is printed as `BACKUP <path>`. The new content is written to a temp file in the same folder and moved over the config.
- **Formatting:** the rewritten file is indented UTF-8 without a BOM. The values of other keys are unchanged, but their whitespace may be reformatted.
- **Repeat runs:** if the entry is already identical, the run prints `UNCHANGED` and writes nothing, not even a backup. Running the script again with the same paths is a no-op.

## Roll back

- **Remove only this server:** run `pwsh -NoProfile -File scripts/install-mcp.ps1 -Uninstall`. This writes a backup first. If the file or the entry is missing, the command does nothing. An empty `mcpServers` object is left in place.
- **Restore the exact previous file:** quit Claude Desktop and copy the backup printed by the run over the config, for example `Copy-Item "$env:APPDATA\Claude\claude_desktop_config.json.bak-20261102T120000000Z" "$env:APPDATA\Claude\claude_desktop_config.json"`.

## Verify in the Claude app

1. Restart Claude Desktop.
2. Open Settings → Developer: `bim-agentic-os` should be listed as running. In a chat, the tools menu should list `diff`, `validate`, `room`, `plan` and the Revit command tools.
3. Ask Claude to call `validate` on a small Project IR. The result should match `dotnet interfaces/cli/bin/Release/net8.0/Bim.Cli.dll validate project.json`.
4. If the server fails to start, check the Claude MCP logs (Windows `%APPDATA%\Claude\logs\mcp*.log`, macOS `~/Library/Logs/Claude/mcp*.log`). Also run the registered `command` and `args` by hand. The server reads JSON-RPC lines on stdin and exits on end of input.

## Evidence

`tests/integration/test-install-mcp.ps1` runs on Ubuntu and Windows CI from `scripts/verify.ps1` and uses temp configs only. Its cases:

- Fresh install.
- Preservation of other keys and servers, including number text and server order.
- Replacement of a stale entry.
- Idempotent rerun.
- Backup bytes equal to the original.
- Refusal of invalid, empty, array-root, non-object `mcpServers` and duplicate-key files, with the file left unchanged.
- A missing DLL.
- Dry run.
- Uninstall.
- Paths with spaces.

In full mode, `install-mcp-launch` also starts the registered command against the real built server and checks its `initialize` answer.

The status is unit-tested. The script has not yet been run against the owner's real Claude Desktop install. Step 2 above is the manual check that is still owed.
