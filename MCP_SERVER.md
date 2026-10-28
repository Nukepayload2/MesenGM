# MCP debugger server

MesenGM can expose its debugger to local Model Context Protocol clients. The feature has two parts:

- Mesen owns the debugger service and a local named pipe.
- `MCPServer.exe` connects that pipe to either stdio or Streamable HTTP.

The bridge is available on Windows builds. Several bridges can be connected at the same time. A bridge launched by an MCP client stays usable if Mesen is closed and reopened during the conversation. The HTTP bridge started from Mesen's **MCP Server** window exits automatically when that Mesen process ends.

## Import the client configuration

Download [mcp.json](mcp.json) and import it in a client that supports the `mcpServers` JSON format and Streamable HTTP. In Mesen, open **Debug > MCP Server** and select **Start** before connecting. The file uses the default port, `51234`; if you choose another port, update the URL in the file.

Import support depends on the client. For clients without a JSON import option, use the commands below.

## Connect through stdio

Use stdio when the MCP client launches its own server process. The bridge starts immediately and answers `initialize` and `tools/list` on its own, so the client can finish its handshake before Mesen is open. Tool calls need a running Mesen and report a clear error until one is available.

For Codex CLI:

```powershell
codex mcp add mesen-debugger -- "C:\path\to\MCPServer.exe" --stdio
```

For Claude Code in PowerShell, use `add-json` so `--stdio` is passed to the bridge instead of being parsed as a Claude option:

```powershell
$claudeConfig = @{
    type = "stdio"
    command = "C:\path\to\MCPServer.exe"
    args = @("--stdio")
}
claude mcp add-json mesen-debugger ($claudeConfig | ConvertTo-Json -Compress)
```

Replace `C:\path\to\MCPServer.exe` with the full path to the `MCPServer.exe` next to `Mesen.exe`. The stdio bridge can run alongside the HTTP bridge and alongside other stdio clients.

## Connect through HTTP

Open **Debug > MCP Server**, choose a port, and select **Start**. The default endpoint is:

```text
http://127.0.0.1:51234/mcp/
```

For Codex CLI:

```powershell
codex mcp add mesen-debugger --url http://127.0.0.1:51234/mcp/
```

For Claude Code:

```powershell
claude mcp add --transport http mesen-debugger http://127.0.0.1:51234/mcp/
```

The endpoint listens only on the IPv4 loopback address. Stopping the HTTP bridge does not stop Mesen's debugger pipe.

The **MCP Server** window shows the bridge's own status and log output, so a failed start is visible without a console window. **Show console window instead of log** launches the bridge in a separate console; the in-app log stays empty in that mode because the output goes to the console.

## Tools

| Tool | What it does | Changes emulator state |
| --- | --- | --- |
| `debugger_status` | Reports emulator and debugger status | No |
| `get_rom_info` | Reports the loaded ROM and available CPU identifiers | No |
| `load_rom` | Loads a ROM file, optionally with a patch | Yes |
| `close_rom` | Stops and closes the loaded ROM | Yes |
| `get_cpu_state` | Reads CPU registers or the current program counter | No |
| `get_ppu_state` | Reads scanline, cycle, and frame state | No |
| `get_screen` | Returns the last rendered frame as a PNG image | No |
| `get_memory_range` | Reads up to 4096 bytes from a memory region | No |
| `search_memory` | Finds a byte pattern in a memory region | No |
| `set_memory` | Writes up to 4096 bytes to a memory region | Yes |
| `get_disassembly` | Disassembles code around an address | No |
| `get_trace_tail` | Reads recent execution trace rows | No |
| `get_debug_events` | Reads recent debugger events | No |
| `set_breakpoints` | Replaces the current breakpoint list | Yes |
| `step` | Advances debugger execution | Yes |
| `resume` | Resumes execution | Yes |
| `pause` | Stops after the next instruction | Yes |
| `run_frames` | Advances a number of frames, then stops in the debugger | Yes |
| `set_input` | Holds buttons on a controller until cleared | Yes |
| `clear_input` | Releases overridden buttons on a controller | Yes |
| `save_rom` | Writes the loaded ROM or an IPS patch to a file | Yes (writes a file) |
| `save_state` | Writes a save state file for the loaded game | Yes (writes a file) |
| `load_state` | Restores a save state file | Yes |

`get_cpu_state` returns structured SNES, NES, and Game Boy registers. Other debugger-supported CPUs return their program counter. `get_ppu_state` currently supports SNES, NES, and Game Boy.

`load_rom` requires an absolute `path` to an existing ROM file; `.lua` scripts are rejected. Optional `patch_path` is an absolute path to an IPS, UPS, or BPS patch applied while loading. Optional `console_type` forces the system for a `.bin` file or another ambiguous ROM; it accepts the `ConsoleType` identifiers 0-6 (Snes=0, Gameboy=1, Nes=2, PcEngine=3, Sms=4, Gba=5, Ws=6). The result contains the same fields as `get_rom_info` plus `success`, `was_running` (whether emulation was running before the call), and `patch_path`. Patches in the ROM's directory are not applied automatically.

`close_rom` takes no arguments. It stops emulation if it is running and returns `success` and `was_running`. It does not release the emulator instance or close Mesen. After it returns, `get_rom_info` reports `rom_loaded: false`, and the other ROM-dependent tools fail with "No ROM is loaded."

`run_frames` advances emulation from the current frame and then stops in the debugger. Its `timeout_ms` is capped at 7000 so the call stays inside the server's 10-second tool budget; the result reports whether the requested frame count was reached. A breakpoint that fires before the target stops the run early: the result then carries `stopped_by_breakpoint: true` with `breakpoint_id`, `break_source`, and `pc`, and `timed_out` stays false.

`get_debug_events` returns the newest retained debugger events, up to a rolling history of 512, including the frame that is still running. That makes a breakpoint hit visible immediately as a `Breakpoint` event with its `breakpoint_id`, without waiting for the next frame and without the Event Viewer window being open. `total` reports how many events the history holds and `history_limit` the cap.

`set_breakpoints` replaces the whole list. Each entry defaults to an execute breakpoint in the CPU's relative memory, which is the address space instruction and RAM addresses are expressed in. `mark_events` defaults to true so hits reach `get_debug_events`; a breakpoint that matches every frame keeps the CPU stopped until `resume`, `step`, or the next `run_frames`. With predictive breakpoints enabled (Mesen's default) the stop can land on the instruction that is about to access the address, so a reported hit may have no `Breakpoint` event yet.

`set_input` holds the given buttons on one controller and the override persists until `clear_input` or another `set_input` replaces it. It does not release automatically when execution stops, so call `clear_input` when the input is no longer wanted.

`get_screen` returns the most recent frame produced by the video decoder as an MCP `image` content block (`image/png`), followed by a JSON text block with `width`, `height`, `byte_length`, and `frame_count`. By default the PNG is the console's native output with the current rotation applied. Pass `apply_video_filter: true` to include the user's scale or NTSC filter, or `include_base64: true` to also receive the PNG inside the structured result for clients that cannot display image blocks. The frame is whatever the decoder last produced, so while execution is stopped it shows the last completed frame.

### Save states and A/B checks

`save_state` writes a save state for the loaded game. Its `path` must be absolute, the parent directory must already exist, and the target must not be a directory; a `.mst` extension is the convention. An existing file is refused unless `overwrite: true` is passed. The result contains `success`, `path`, `size`, `frame_count`, `console_type_name`, `execution_stopped`, and `emulation_running`. A file that Mesen did not create or that is empty is reported as an error.

`load_state` restores a state written by Mesen, including CPU, memory, and video state. Its `path` must be absolute and the file must exist. It returns `success`, `path`, `frame_count`, `console_type_name`, `execution_stopped`, and `emulation_running`. Both tools require a loaded ROM and work whether or not the debugger was already active.

A useful workflow for risky changes is: `save_state` before an edit, make the change, observe the result, then `load_state` to restore the snapshot and compare behavior. This provides an A/B check without reloading the ROM, and it also provides a rollback point for `set_memory` experiments.

Genesis-specific state tools are not included yet because this branch does not contain the Genesis core and interop types from mesen2-expanded. They can be added without changing the transport layer once those types arrive.

ROM loading (`load_rom`, `close_rom`), controller input (`set_input`, `clear_input`), and save states (`save_state`, `load_state`) are supported. Audio capture and recording are not exposed by these tools; use the Mesen UI when audio output is required.

## Finding identifiers

Call `get_rom_info` first, or call `load_rom` first when the ROM still needs to be loaded. Its response includes the CPU identifiers and memory regions available for the loaded ROM. Each memory region has the numeric ID used by the memory tools, its API name, and its size. Invalid or unavailable regions are rejected with an error. `get_rom_info` and `debugger_status` work without a loaded ROM: `get_rom_info` then returns `rom_loaded: false`, `format: "Unknown"`, an empty `rom_path` and `patch_path`, `console_type: -1`, `console_type_name: "None"`, and empty `cpu_types` and `memory_types` arrays.

Tool results contain both structured content and an equivalent JSON text block. This keeps them usable with clients that have not added structured tool-result support.

## Local security

The HTTP bridge binds to `127.0.0.1` and accepts browser origins only from localhost, `127.0.0.1`, or `[::1]`. It is not intended for network exposure.

An authorized client can read ROM state, change memory, replace breakpoints, and control execution. Review a client's tool requests before granting it access to a session you care about.

## Troubleshooting

- If tool calls report that Mesen is not running, open Mesen and try the request again. The client does not need to restart the bridge.
- If a tool call reports that Mesen did not answer in time, Mesen is paused in its own debugger or busy. Tool calls time out after 10 seconds so the client stays usable.
- If HTTP startup fails, choose an unused port in **Debug > MCP Server**. The log panel in that window shows why the bridge stopped.
- If debugger tools report that no ROM is loaded, load one with `load_rom` or open a game in the Mesen UI, then retry. `get_rom_info` and `debugger_status` remain available without a ROM.
