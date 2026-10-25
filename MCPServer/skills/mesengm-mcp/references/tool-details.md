# Tool arguments and behavior

Check the live tool schema first. These details reflect `McpDebugService.cs` in this project and are not a substitute for discovery on an older or newer binary.

## Reading state

| Tool | Arguments and limits |
| --- | --- |
| `get_rom_info` | No arguments. Returns `rom_loaded`, ROM identity, CPU IDs, memory region IDs/names/sizes. Available without a ROM: then `rom_loaded` is false, `format` is `Unknown`, `rom_path` and `patch_path` are empty, `console_type` is `-1`, `console_type_name` is `None`, and `cpu_types`/`memory_types` are empty arrays. |
| `debugger_status` | No arguments. Includes `debugger_running`, `emulation_running`, `execution_stopped`, `emulation_paused`. |
| `get_cpu_state` | Optional `cpu_type`; defaults to the loaded system's main CPU. |
| `get_ppu_state` | Optional `cpu_type`; structured PPU state for NES, SNES and Game Boy. |
| `get_screen` | Optional `apply_video_filter` (default false) and `include_base64` (default false). Returns the last rendered frame as an `image/png` content block plus `width`, `height`, `byte_length`, `frame_count`, `emulation_paused`. Fails until at least one frame has been rendered. |
| `get_disassembly` | Optional `cpu_type`, `address`, `line_count` (1-100, default 20). Omitting `address` centers on the current PC. |
| `get_trace_tail` | `count` (1-1000, default 100), `offset` (default 0, measured from the newest entry). No `cpu_type` argument. |
| `get_debug_events` | Optional `cpu_type`, `max_count` (1-1000, default 100). Returns event type, PC, scanline, cycle and breakpoint ID. |

## ROM lifecycle

`debugger_status` and `get_rom_info` are the only read tools that answer without a loaded ROM. All other tools except `load_rom` and `close_rom` fail with "No ROM is loaded" until a game is loaded.

- `load_rom`: required `path`, an absolute path to an existing ROM file. `.lua` scripts are rejected. Optional `patch_path` is an absolute path to an existing IPS, UPS, or BPS patch applied during the load; a same-directory IPS is not applied automatically. Optional `console_type` (0-6) forces the system and uses the same values as `ConsoleType`: Snes=0, Gameboy=1, Nes=2, PcEngine=3, Sms=4, Gba=5, Ws=6; use it for `.bin` files or when detection would choose the wrong system. The result reuses the `get_rom_info` fields and adds `success`, `was_running` (emulation state before the call), and `patch_path` (empty when no patch was applied). A false result from Mesen is reported as an error.
- `close_rom`: no arguments. Stops emulation if it is running and returns `success` and `was_running`. It leaves the emulator process running and does not release the emulator instance. Loading another ROM also replaces the current one. Afterwards `get_rom_info` reports `rom_loaded: false`, and the other ROM-dependent tools fail with "No ROM is loaded."

## Memory

- `get_memory_range`: required `memory_type`, `start_address`, `length` (1-4096). Optional `format`: `hex` (default, unspaced uppercase), `base64`, or `bytes` (integer array).
- `search_memory`: required `memory_type`, `pattern`. Pattern is 1-256 hex bytes; `??` matches one arbitrary byte. Spaces are optional. Optional `start_address` (default 0), inclusive `end_address` (default end of region), `max_results` (1-1000, default 100). If `truncated` is true, the result hit the cap and is not an exhaustive list. To continue, search from one byte after the last returned address, retaining the original end bound so overlapping matches are not skipped.
- `set_memory`: required `memory_type`, `address`, and either `hex` or `data`. Between 1 and 4096 bytes; integer-array values must be 0-255. The entire range must fit the region. Reads use `start_address`; writes use `address`.

## Breakpoints

`set_breakpoints` takes a required `breakpoints` array with at most 1000 entries. Each entry requires `address`. Optional fields:

- `end_address`: inclusive; defaults to the same address.
- `cpu_type`: defaults to the main CPU.
- `memory_type`: defaults to that CPU's relative memory.
- `type`: flags Read=1, Write=2, Execute=4, Forbid=8. Combine needed flags; default 4.
- `enabled`: defaults to true.
- `condition`: debugger expression; must fit within 999 UTF-8 bytes. Use this project's debugger expression syntax rather than guessing a programming-language expression.

The call replaces all breakpoints. IDs are assigned from array indices. It does not return the previous list.

## Execution

`step` accepts optional `cpu_type`, `count` (0-1000000, default 1), and `step_type` (default 0). The meaning of `count` depends on the selected mode, so use a positive count for ordinary instruction stepping. CPU support is checked by the handler.

| ID | Mode |
| --- | --- |
| 0 | Step |
| 1 | StepOut |
| 2 | StepOver |
| 3 | CpuCycleStep |
| 4 | PpuStep |
| 5 | PpuScanline |
| 6 | PpuFrame |
| 7 | SpecificScanline |
| 8 | RunToNmi |
| 9 | RunToIrq |
| 10 | StepBack |

`pause` and `resume` take no arguments. Read `debugger_status` after execution requests when a stopped state matters to the next observation.

## Input and frame advance

- `run_frames`: required `frames` (1-100000). Optional `cpu_type` (defaults to the main CPU) and `timeout_ms` (100-7000, default 5000). Resumes execution, waits until that many frames have elapsed on the selected CPU, then stops in the debugger. Returns `success`, `frames_requested`, `frames_elapsed`, `timed_out`, `execution_stopped`, `emulation_running`, and `cpu_type`. `success` is false when the frame target was not reached because of the timeout or because emulation stopped. `frames_elapsed` can overshoot the request by a frame because it is sampled by polling. Fails when emulation is not running. The server times out tool calls after 10 seconds, and stopping can add up to one second, so keep `timeout_ms` at or below 7000.
- `set_input`: optional `controller` (0-7, default 0) and required `buttons` array. Button names are case-insensitive: `A`, `B`, `X`, `Y`, `L`, `R`, `U`, `D`, `Up`, `Down`, `Left`, `Right`, `Select`, `Start`. Unknown names are rejected with the valid list. A controller that is not available for the loaded ROM is rejected with the list of available controllers; configure the input port in Mesen if needed. The call replaces the override for that controller and returns `success`, `controller`, normalized uppercase `buttons`, `available_controllers`, and `debugger_running`.
- `clear_input`: optional `controller` (0-7, default 0). Sets an all-released override for that controller and returns the same shape as `set_input` with an empty `buttons` array. It does not fail when the controller is unavailable; `available_controllers` reports the controllers that actually exist.

Input overrides persist until `clear_input` or another `set_input` replaces them; stopping execution does not release them. `U`/`D` and `Up`/`Down` are separate fields: use `Up`/`Down`/`Left`/`Right` for the normal d-pad and `U`/`D` only for systems with a second directional input.

## Save states

Both tools require a loaded ROM. Save states are separate from `save_rom`: a state captures the running console (CPU, memory, and video state), while `save_rom` exports the loaded ROM image or an IPS patch. Use the `.mst` extension by convention.

- `save_state`: required absolute `path`; optional `overwrite` (default false) and `cpu_type` (defaults to the main CPU, used only for the reported frame count). The parent directory must already exist; a path that resolves to a directory is rejected. If the file already exists and `overwrite` is not true, the call fails instead of replacing it. After Mesen writes the file, the handler verifies that the file exists and is not empty. Returns `success`, `path`, `size` (bytes), `frame_count`, `console_type_name`, `execution_stopped`, and `emulation_running`. The tool is not read-only and is not idempotent because a repeat call without `overwrite` is rejected.
- `load_state`: required absolute `path` to an existing file. Restores the saved CPU, memory, and video state. Returns `success`, `path`, `frame_count` (after the restore), `console_type_name`, `execution_stopped`, and `emulation_running`. It is not read-only; restoring the same file again is idempotent. It does not initialize the debugger, so a subsequent debugger tool may initialize it as needed.

For an A/B check, call `save_state` before changing memory, observe the modified behavior, then call `load_state` to return to the snapshot. Keep the state file outside directories that are cleaned up between runs when it must survive a restart.

## Export

`save_rom` requires an absolute `path`. Optional `as_ips` defaults to false, `overwrite` to false, and `cdl_strip_option` to 0 (StripNone; 1=StripUnused, 2=StripUsed). Use `as_ips: true` for an IPS patch containing ROM edits. Export support depends on the loaded format; report the server's format error instead of treating every loaded game as exportable.

Check tool results for `isError` and JSON-RPC errors. A transport response alone does not mean an edit or export succeeded.
