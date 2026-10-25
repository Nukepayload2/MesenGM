using Mesen.Debugger.Disassembly;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Debugger.Utilities
{
	internal sealed class McpDebugService
	{
		private const string ServerName = "Mesen2-MCP";
		private const string LatestProtocolVersion = "2025-06-18";
		private const int MaxMemoryTransfer = 4096;
		private const int MaxTraceRows = 1000;
		private const int MaxBreakpoints = 1000;
		private const int MaxSearchResults = 1000;
		private const int MaxSearchPattern = 256;

		// Searches read the region in overlapping chunks instead of one buffer so a
		// multi-megabyte ROM does not need a matching managed allocation.
		private const int SearchChunkSize = 65536;

		// Debugger calls run on a worker so a request that blocks inside the
		// emulator cannot wedge the connection for the rest of the session.
		private const int ToolTimeoutMs = 10000;
		private const int ToolQueueTimeoutMs = 15000;

		private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal) {
			"2024-11-05",
			"2025-03-26",
			LatestProtocolVersion
		};

		//These tools must answer before a ROM exists, so a call can discover the
		//session or load a game from the client.
		private static readonly HashSet<string> ToolsAllowedWithoutRom = new(StringComparer.Ordinal) {
			"debugger_status",
			"get_rom_info",
			"load_rom",
			"close_rom"
		};

		private readonly SemaphoreSlim _toolGate = new(1, 1);

		public string HandleRequest(string body)
		{
			JsonObject? request;
			try {
				request = JsonNode.Parse(body) as JsonObject;
			} catch {
				return MakeJsonRpcError(null, -32700, "Parse error");
			}

			if(request == null || request["jsonrpc"] is not JsonValue jsonRpc || !jsonRpc.TryGetValue(out string? version) || version != "2.0") {
				return MakeJsonRpcError(null, -32600, "Invalid JSON-RPC request");
			}

			if(!request.ContainsKey("id")) {
				return "{}";
			}

			JsonNode? id = request["id"]?.DeepClone();
			if(id == null) {
				return MakeJsonRpcError(null, -32600, "JSON-RPC request IDs cannot be null");
			}

			try {
				string? method = request["method"]?.GetValue<string>();
				JsonObject? parameters = request["params"] as JsonObject;

				// initialize, ping, and tools/list never touch the debugger, so they
				// stay outside the gate and always answer immediately.
				return method switch {
					"initialize" => HandleInitialize(id, parameters),
					"ping" => MakeJsonRpcResult(id, new JsonObject()),
					"tools/list" => HandleToolsList(id),
					"tools/call" => RunToolCall(id, parameters),
					_ => MakeJsonRpcError(id, -32601, $"Unknown method: {method}")
				};
			} catch(Exception ex) {
				return MakeJsonRpcError(id, -32603, $"Internal error: {ex.Message}");
			}
		}

		// Runs one tool call with a timeout. Concurrent bridges are serialized here
		// because Mesen's debugger state is global.
		private string RunToolCall(JsonNode id, JsonObject? parameters)
		{
			if(!_toolGate.Wait(ToolQueueTimeoutMs)) {
				return MakeJsonRpcError(id, -32603, "Another debugger tool call is still running. Try again in a moment.");
			}

			// The worker gets its own copy of the id because a JsonNode cannot be
			// attached to two response objects.
			JsonNode workerId = id.DeepClone();
			Task<string> call = Task.Run(() => HandleToolsCall(workerId, parameters));
			if(call.Wait(ToolTimeoutMs)) {
				_toolGate.Release();
				try {
					return call.Result;
				} catch(Exception ex) {
					return MakeJsonRpcError(id, -32603, $"Internal error: {ex.Message}");
				}
			}

			// The call is stuck inside the emulator. Hold the gate until it finishes
			// so later requests fail fast instead of piling up behind it.
			call.ContinueWith(_ => _toolGate.Release(), TaskScheduler.Default);
			return MakeJsonRpcError(id, -32603, $"Mesen did not answer within {ToolTimeoutMs / 1000} seconds. The emulator may be paused in the debugger or busy; check Mesen, then try again.");
		}

		private static string HandleInitialize(JsonNode id, JsonObject? parameters)
		{
			string requestedVersion = parameters?["protocolVersion"]?.GetValue<string>() ?? LatestProtocolVersion;
			string protocolVersion = SupportedProtocolVersions.Contains(requestedVersion) ? requestedVersion : LatestProtocolVersion;

			return MakeJsonRpcResult(id, new JsonObject {
				["protocolVersion"] = protocolVersion,
				["capabilities"] = new JsonObject {
					["tools"] = new JsonObject { ["listChanged"] = false }
				},
				["serverInfo"] = new JsonObject {
					["name"] = ServerName,
					["version"] = "1.4.0"
				},
				["instructions"] = "Use get_rom_info first to discover the loaded system, CPU type identifiers, and memory region identifiers. If no ROM is loaded, load one with load_rom or in the Mesen UI."
			});
		}

		private static string HandleToolsList(JsonNode id)
		{
			JsonArray tools = new() {
				(JsonNode)MakeToolDef(
					"debugger_status",
					"Report the Mesen emulator and debugger state.",
					EmptySchema(),
					readOnly: true
				),
				(JsonNode)MakeToolDef(
					"get_rom_info",
					"Get information about the loaded ROM plus the CPU and memory region identifiers available to debugger tools. Returns rom_loaded=false and format \"Unknown\" when no ROM is loaded.",
					EmptySchema(),
					readOnly: true
				),
				(JsonNode)MakeToolDef(
					"load_rom",
					"Load a ROM file, optionally applying an IPS, UPS, or BPS patch. Use console_type to force the system for a .bin file or other ambiguous ROM. Returns the same fields as get_rom_info plus success, was_running, and patch_path.",
					new JsonObject {
						["type"] = "object",
						["required"] = StringArray("path"),
						["properties"] = new JsonObject {
							["path"] = StringProperty("Absolute path of the ROM file to load. Lua scripts are rejected."),
							["patch_path"] = StringProperty("Optional absolute path of an IPS, UPS, or BPS patch to apply while loading."),
							["console_type"] = IntegerProperty("Optional ConsoleType identifier to force: Snes=0, Gameboy=1, Nes=2, PcEngine=3, Sms=4, Gba=5, Ws=6. Use it for .bin files or when detection would pick the wrong system.", minimum: 0, maximum: 6)
						}
					},
					readOnly: false,
					idempotent: false
				),
				(JsonNode)MakeToolDef(
					"close_rom",
					"Stop and close the loaded ROM. Returns whether emulation was running before the call. Afterwards get_rom_info reports rom_loaded=false and ROM-dependent tools fail with \"No ROM is loaded.\"",
					EmptySchema(),
					readOnly: false,
					destructive: true,
					idempotent: true
				),
				(JsonNode)MakeToolDef(
					"get_cpu_state",
					"Get CPU registers. SNES, NES, and Game Boy return structured register sets; other supported CPUs return the program counter.",
					new JsonObject {
						["type"] = "object",
						["properties"] = new JsonObject {
							["cpu_type"] = IntegerProperty("CPU type identifier. Omit it to use the loaded system's main CPU.")
						}
					},
					readOnly: true
				),
				(JsonNode)MakeToolDef(
					"get_ppu_state",
					"Get scanline, cycle, and frame state for the SNES, NES, or Game Boy graphics processor.",
					new JsonObject {
						["type"] = "object",
						["properties"] = new JsonObject {
							["cpu_type"] = IntegerProperty("CPU type identifier. Omit it to use the loaded system's main CPU.")
						}
					},
					readOnly: true
				),
				(JsonNode)MakeToolDef(
					"get_screen",
					"Capture the most recently rendered frame as a PNG image. The result contains an image content block plus width, height, and frame count. By default the image is the console's native output; set apply_video_filter to include the user's scale/NTSC filter.",
					new JsonObject {
						["type"] = "object",
						["properties"] = new JsonObject {
							["apply_video_filter"] = BooleanProperty("Apply the emulator's current video filter (scale filters, NTSC, etc.) before encoding. False returns the native-resolution frame.", defaultValue: false),
							["include_base64"] = BooleanProperty("Also place the base64 PNG in the structured result for clients that ignore image content blocks.", defaultValue: false)
						}
					},
					readOnly: true
				),
				(JsonNode)MakeToolDef(
					"get_memory_range",
					$"Read up to {MaxMemoryTransfer} bytes from a Mesen memory region.",
					new JsonObject {
						["type"] = "object",
						["required"] = StringArray("memory_type", "start_address", "length"),
						["properties"] = new JsonObject {
							["memory_type"] = IntegerProperty("MemoryType identifier."),
							["start_address"] = IntegerProperty("First byte address.", minimum: 0),
							["length"] = IntegerProperty("Number of bytes to read.", minimum: 1, maximum: MaxMemoryTransfer),
							["format"] = StringProperty(
								"How to encode the bytes. \"hex\" returns an unspaced uppercase hex string, \"base64\" is the most compact, and \"bytes\" returns a JSON array of integers.",
								defaultValue: "hex",
								allowedValues: new[] { "hex", "base64", "bytes" }
							)
						}
					},
					readOnly: true
				),
				(JsonNode)MakeToolDef(
					"search_memory",
					$"Find a byte pattern in a Mesen memory region. The pattern is hex bytes with ?? as a single-byte wildcard, for example \"A9 00 8D ?? 20\". Whitespace is optional, so \"A9008D\" works too.",
					new JsonObject {
						["type"] = "object",
						["required"] = StringArray("memory_type", "pattern"),
						["properties"] = new JsonObject {
							["memory_type"] = IntegerProperty("MemoryType identifier."),
							["pattern"] = StringProperty($"Hex byte pattern, up to {MaxSearchPattern} bytes, using ?? for any byte."),
							["start_address"] = IntegerProperty("First address to search. Defaults to 0.", minimum: 0, defaultValue: 0),
							["end_address"] = IntegerProperty("Last address to search. Defaults to the end of the region."),
							["max_results"] = IntegerProperty("Maximum match addresses to return.", minimum: 1, maximum: MaxSearchResults, defaultValue: 100)
						}
					},
					readOnly: true
				),
				(JsonNode)MakeToolDef(
					"set_memory",
					$"Write up to {MaxMemoryTransfer} bytes to a Mesen memory region. Supply the bytes as either hex or data.",
					new JsonObject {
						["type"] = "object",
						["required"] = StringArray("memory_type", "address"),
						["properties"] = new JsonObject {
							["memory_type"] = IntegerProperty("MemoryType identifier."),
							["address"] = IntegerProperty("First byte address.", minimum: 0),
							["hex"] = StringProperty("Bytes to write as a hex string, for example \"A9 00 8D\" or \"A9008D\". Use this or data, not both."),
							["data"] = new JsonObject {
								["type"] = "array",
								["minItems"] = 1,
								["maxItems"] = MaxMemoryTransfer,
								["items"] = IntegerProperty("Byte value.", minimum: 0, maximum: 255)
							}
						}
					},
					readOnly: false,
					destructive: true,
					idempotent: true
				),
				(JsonNode)MakeToolDef(
					"get_disassembly",
					"Disassemble code around an address. Omit address to center the result on the current program counter.",
					new JsonObject {
						["type"] = "object",
						["properties"] = new JsonObject {
							["cpu_type"] = IntegerProperty("CPU type identifier. Omit it to use the loaded system's main CPU."),
							["address"] = IntegerProperty("Address to disassemble.", minimum: 0),
							["line_count"] = IntegerProperty("Maximum disassembly rows.", minimum: 1, maximum: 100, defaultValue: 20)
						}
					},
					readOnly: true
				),
				(JsonNode)MakeToolDef(
					"get_trace_tail",
					"Get recent execution trace rows.",
					new JsonObject {
						["type"] = "object",
						["properties"] = new JsonObject {
							["count"] = IntegerProperty("Maximum rows to return.", minimum: 1, maximum: MaxTraceRows, defaultValue: 100),
							["offset"] = IntegerProperty("Row offset from the newest trace entry.", minimum: 0, defaultValue: 0)
						}
					},
					readOnly: true
				),
				(JsonNode)MakeToolDef(
					"get_debug_events",
					"Get recent debugger events for one CPU.",
					new JsonObject {
						["type"] = "object",
						["properties"] = new JsonObject {
							["cpu_type"] = IntegerProperty("CPU type identifier. Omit it to use the loaded system's main CPU."),
							["max_count"] = IntegerProperty("Maximum events to return.", minimum: 1, maximum: MaxTraceRows, defaultValue: 100)
						}
					},
					readOnly: true
				),
				(JsonNode)MakeToolDef(
					"set_breakpoints",
					"Replace all debugger breakpoints. Breakpoint flags are Read=1, Write=2, Execute=4, and Forbid=8.",
					new JsonObject {
						["type"] = "object",
						["required"] = StringArray("breakpoints"),
						["properties"] = new JsonObject {
							["breakpoints"] = new JsonObject {
								["type"] = "array",
								["maxItems"] = MaxBreakpoints,
								["items"] = new JsonObject {
									["type"] = "object",
									["required"] = StringArray("address"),
									["properties"] = new JsonObject {
										["type"] = IntegerProperty("BreakpointTypeFlags value.", minimum: 1, maximum: 15, defaultValue: (int)BreakpointTypeFlags.Execute),
										["address"] = IntegerProperty("First address.", minimum: 0),
										["end_address"] = IntegerProperty("Last address. Omit it for a single-address breakpoint.", minimum: 0),
										["cpu_type"] = IntegerProperty("CPU type identifier. Omit it to use the loaded system's main CPU."),
										["memory_type"] = IntegerProperty("MemoryType identifier. Omit it to use the CPU's relative memory."),
										["enabled"] = new JsonObject { ["type"] = "boolean", ["default"] = true },
										["condition"] = new JsonObject { ["type"] = "string", ["maxLength"] = 999 }
									}
								}
							}
						}
					},
					readOnly: false,
					idempotent: true
				),
				(JsonNode)MakeToolDef(
					"step",
					"Step debugger execution. Step types: Step=0, StepOut=1, StepOver=2, CpuCycleStep=3, PpuStep=4, PpuScanline=5, PpuFrame=6, SpecificScanline=7, RunToNmi=8, RunToIrq=9, StepBack=10.",
					new JsonObject {
						["type"] = "object",
						["properties"] = new JsonObject {
							["cpu_type"] = IntegerProperty("CPU type identifier. Omit it to use the loaded system's main CPU."),
							["count"] = IntegerProperty("Instruction count or step-specific value.", minimum: 0, maximum: 1000000, defaultValue: 1),
							["step_type"] = IntegerProperty("StepType identifier.", minimum: 0, maximum: 10, defaultValue: 0)
						}
					},
					readOnly: false
				),
				(JsonNode)MakeToolDef(
					"run_frames",
					"Advance emulation from the current frame by a number of frames, then stop in the debugger. Returns the frames actually elapsed. Keeps the total wait within the tool's 10-second budget.",
					new JsonObject {
						["type"] = "object",
						["required"] = StringArray("frames"),
						["properties"] = new JsonObject {
							["frames"] = IntegerProperty("Number of frames to advance.", minimum: 1, maximum: 100000),
							["cpu_type"] = IntegerProperty("CPU type identifier. Omit it to use the loaded system's main CPU."),
							["timeout_ms"] = IntegerProperty("Maximum time to wait for the frames before stopping. Keep this below the 10-second tool limit.", minimum: 100, maximum: 7000, defaultValue: 5000)
						}
					},
					readOnly: false,
					idempotent: false
				),
				(JsonNode)MakeToolDef("resume", "Resume debugger execution.", EmptySchema(), readOnly: false, idempotent: true),
				(JsonNode)MakeToolDef("pause", "Break debugger execution after the current instruction.", EmptySchema(), readOnly: false),
				(JsonNode)MakeToolDef(
					"set_input",
					"Override the buttons held on one controller. Button names are case-insensitive: A, B, X, Y, L, R, U, D, Up, Down, Left, Right, Select, Start. The override stays active until clear_input or another set_input call.",
					new JsonObject {
						["type"] = "object",
						["required"] = StringArray("buttons"),
						["properties"] = new JsonObject {
							["controller"] = IntegerProperty("Controller index.", minimum: 0, maximum: 7, defaultValue: 0),
							["buttons"] = new JsonObject {
								["type"] = "array",
								["items"] = StringProperty("Button name."),
								["description"] = "Buttons to hold. Replaces any previous override for this controller."
							}
						}
					},
					readOnly: false,
					idempotent: true
				),
				(JsonNode)MakeToolDef(
					"clear_input",
					"Release all overridden buttons on one controller.",
					new JsonObject {
						["type"] = "object",
						["properties"] = new JsonObject {
							["controller"] = IntegerProperty("Controller index.", minimum: 0, maximum: 7, defaultValue: 0)
						}
					},
					readOnly: false,
					idempotent: true
				),
				(JsonNode)MakeToolDef(
					"save_rom",
					"Write the loaded ROM, including any set_memory edits to its ROM regions, to a file. Use save_state instead to snapshot the running emulator.",
					new JsonObject {
						["type"] = "object",
						["required"] = StringArray("path"),
						["properties"] = new JsonObject {
							["path"] = StringProperty("Absolute path of the file to write. There is no default; an existing file is only replaced when overwrite is true."),
							["as_ips"] = BooleanProperty("Write an IPS patch containing only the edits instead of a full ROM.", defaultValue: false),
							["overwrite"] = BooleanProperty("Allow replacing an existing file.", defaultValue: false),
							["cdl_strip_option"] = IntegerProperty("CdlStripOption value: StripNone=0, StripUnused=1, StripUsed=2.", minimum: 0, maximum: 2, defaultValue: 0)
						}
					},
					readOnly: false,
					destructive: true,
					idempotent: true
				),
				(JsonNode)MakeToolDef(
					"save_state",
					"Write a save state for the loaded game to a file. The path must be absolute and its parent directory must already exist. An existing file is only replaced when overwrite is true. Returns the file size and current frame count.",
					new JsonObject {
						["type"] = "object",
						["required"] = StringArray("path"),
						["properties"] = new JsonObject {
							["path"] = StringProperty("Absolute path of the save state file to write. A .mst extension is recommended."),
							["overwrite"] = BooleanProperty("Allow replacing an existing save state file.", defaultValue: false),
							["cpu_type"] = IntegerProperty("CPU type identifier used for the reported frame count. Omit it to use the loaded system's main CPU.")
						}
					},
					readOnly: false,
					idempotent: false
				),
				(JsonNode)MakeToolDef(
					"load_state",
					"Restore a save state file written by Mesen. Restores CPU, memory, and video state from the file. The path must be absolute and the file must exist. Returns the restored frame count.",
					new JsonObject {
						["type"] = "object",
						["required"] = StringArray("path"),
						["properties"] = new JsonObject {
							["path"] = StringProperty("Absolute path of the save state file to load, normally a .mst file."),
							["cpu_type"] = IntegerProperty("CPU type identifier used for the reported frame count. Omit it to use the loaded system's main CPU.")
						}
					},
					readOnly: false,
					idempotent: true
				)
			};

			return MakeJsonRpcResult(id, new JsonObject { ["tools"] = tools });
		}

		private static string HandleToolsCall(JsonNode id, JsonObject? parameters)
		{
			string? toolName = parameters?["name"]?.GetValue<string>();
			JsonObject? arguments = parameters?["arguments"] as JsonObject;
			if(string.IsNullOrWhiteSpace(toolName)) {
				return MakeJsonRpcError(id, -32602, "Missing tool name");
			}

			try {
				if(!ToolsAllowedWithoutRom.Contains(toolName)) {
					EnsureRomLoaded();
				}
				return toolName switch {
					"debugger_status" => ToolSuccess(id, GetDebuggerStatus()),
					"get_rom_info" => ToolSuccess(id, GetRomInfo()),
					"load_rom" => ToolSuccess(id, LoadRom(arguments)),
					"close_rom" => ToolSuccess(id, CloseRom()),
					"get_cpu_state" => ToolSuccess(id, GetCpuState(arguments)),
					"get_ppu_state" => ToolSuccess(id, GetPpuState(arguments)),
					"get_screen" => GetScreen(id, arguments),
					"get_memory_range" => ToolSuccess(id, GetMemoryRange(arguments)),
					"search_memory" => ToolSuccess(id, SearchMemory(arguments)),
					"set_memory" => ToolSuccess(id, SetMemory(arguments)),
					"get_disassembly" => ToolSuccess(id, GetDisassembly(arguments)),
					"get_trace_tail" => ToolSuccess(id, GetTraceTail(arguments)),
					"get_debug_events" => ToolSuccess(id, GetDebugEvents(arguments)),
					"set_breakpoints" => ToolSuccess(id, SetBreakpoints(arguments)),
					"step" => ToolSuccess(id, Step(arguments)),
					"run_frames" => ToolSuccess(id, RunFrames(arguments)),
					"resume" => ToolSuccess(id, Resume()),
					"pause" => ToolSuccess(id, Pause()),
					"set_input" => ToolSuccess(id, SetInput(arguments)),
					"clear_input" => ToolSuccess(id, ClearInput(arguments)),
					"save_rom" => ToolSuccess(id, SaveRom(arguments)),
					"save_state" => ToolSuccess(id, SaveState(arguments)),
					"load_state" => ToolSuccess(id, LoadState(arguments)),
					_ => MakeJsonRpcError(id, -32602, $"Unknown tool: {toolName}")
				};
			} catch(Exception ex) {
				return ToolError(id, ex.Message);
			}
		}

		private static JsonObject GetDebuggerStatus()
		{
			bool debuggerRunning = DebugApi.IsDebuggerRunning();
			return new JsonObject {
				["debugger_running"] = debuggerRunning,
				["emulation_running"] = EmuApi.IsRunning(),
				["execution_stopped"] = debuggerRunning && DebugApi.IsExecutionStopped(),
				["emulation_paused"] = EmuApi.IsPaused(),
				["mesen_version"] = EmuApi.GetMesenVersion().ToString()
			};
		}

		private static JsonObject GetRomInfo()
		{
			RomInfo romInfo = EmuApi.GetRomInfo();

			// Stop() destroys the console but leaves the native RomInfo behind, so
			// the format alone cannot tell whether a ROM is still usable.
			bool romLoaded = IsRomLoaded(romInfo);
			JsonArray cpuTypes = new();
			if(romLoaded) {
				foreach(CpuType cpuType in romInfo.CpuTypes) {
					cpuTypes.Add((JsonNode)new JsonObject {
						["id"] = (int)cpuType,
						["name"] = cpuType.ToString()
					});
				}
			}

			JsonArray memoryTypes = new();
			if(romLoaded) {
				foreach(MemoryType memoryType in Enum.GetValues<MemoryType>()) {
					int size = memoryType == MemoryType.None ? 0 : DebugApi.GetMemorySize(memoryType);
					if(size > 0) {
						memoryTypes.Add((JsonNode)new JsonObject {
							["id"] = (int)memoryType,
							["name"] = memoryType.ToString(),
							["size"] = size
						});
					}
				}
			}

			// Never leak the stale RomInfo left behind by close_rom: without a
			// running console every identity field is reported as absent.
			return new JsonObject {
				["rom_loaded"] = romLoaded,
				["console_type"] = romLoaded ? (int)romInfo.ConsoleType : -1,
				["console_type_name"] = romLoaded ? romInfo.ConsoleType.ToString() : "None",
				["rom_path"] = romLoaded ? romInfo.RomPath : "",
				// load_rom overwrites this with the patch it applied; get_rom_info
				// has no way to recover a patch path from the native RomInfo.
				["patch_path"] = "",
				["format"] = romLoaded ? romInfo.Format.ToString() : RomFormat.Unknown.ToString(),
				["cpu_types"] = cpuTypes,
				["memory_types"] = memoryTypes
			};
		}

		private static JsonObject LoadRom(JsonObject? arguments)
		{
			string path = RequiredString(arguments, "path");
			if(!Path.IsPathRooted(path)) {
				throw new ArgumentException("path must be an absolute path.");
			}
			if(string.Equals(Path.GetExtension(path), ".lua", StringComparison.OrdinalIgnoreCase)) {
				throw new ArgumentException("Lua scripts cannot be loaded as a ROM.");
			}
			if(!File.Exists(path)) {
				throw new ArgumentException($"ROM file does not exist: {path}");
			}

			string? patchPath = arguments?["patch_path"]?.GetValue<string>();
			if(string.IsNullOrWhiteSpace(patchPath)) {
				patchPath = null;
			} else {
				if(!Path.IsPathRooted(patchPath)) {
					throw new ArgumentException("patch_path must be an absolute path.");
				}
				if(!File.Exists(patchPath)) {
					throw new ArgumentException($"Patch file does not exist: {patchPath}");
				}
			}

			int? consoleTypeValue = arguments?["console_type"] == null
				? null
				: OptionalInt(arguments, "console_type", 0, 0, 6);

			bool wasRunning = EmuApi.IsRunning();
			bool loaded = consoleTypeValue.HasValue
				? EmuApi.LoadRomWithConsoleType(path, patchPath, (ConsoleType)consoleTypeValue.Value)
				: EmuApi.LoadRom(path, patchPath);
			if(!loaded) {
				throw new InvalidOperationException($"Mesen could not load {path}.");
			}

			//InitializeDebugger is idempotent; this also makes later tools work
			//when the user had already started a game manually.
			DebugApi.InitializeDebugger();

			JsonObject result = GetRomInfo();
			result["success"] = true;
			result["was_running"] = wasRunning;
			result["patch_path"] = patchPath ?? "";
			return result;
		}

		private static JsonObject CloseRom()
		{
			bool wasRunning = EmuApi.IsRunning();
			if(wasRunning) {
				EmuApi.Stop();
			}
			return new JsonObject {
				["success"] = true,
				["was_running"] = wasRunning
			};
		}

		private static JsonObject GetCpuState(JsonObject? arguments)
		{
			CpuType cpuType = GetCpuType(arguments);
			switch(cpuType) {
				case CpuType.Snes: {
					SnesCpuState state = DebugApi.GetCpuState<SnesCpuState>(cpuType);
					return new JsonObject {
						["cpu_type"] = (int)cpuType,
						["cpu_type_name"] = cpuType.ToString(),
						["PC"] = state.PC,
						["K"] = state.K,
						["A"] = state.A,
						["X"] = state.X,
						["Y"] = state.Y,
						["SP"] = state.SP,
						["D"] = state.D,
						["DBR"] = state.DBR,
						["PS"] = (int)state.PS,
						["emulation_mode"] = state.EmulationMode,
						["cycle_count"] = state.CycleCount
					};
				}
				case CpuType.Gameboy: {
					GbCpuState state = DebugApi.GetCpuState<GbCpuState>(cpuType);
					return new JsonObject {
						["cpu_type"] = (int)cpuType,
						["cpu_type_name"] = cpuType.ToString(),
						["PC"] = state.PC,
						["SP"] = state.SP,
						["A"] = state.A,
						["flags"] = state.Flags,
						["B"] = state.B,
						["C"] = state.C,
						["D"] = state.D,
						["E"] = state.E,
						["H"] = state.H,
						["L"] = state.L,
						["halt_counter"] = state.HaltCounter,
						["cycle_count"] = state.CycleCount
					};
				}
				case CpuType.Nes: {
					NesCpuState state = DebugApi.GetCpuState<NesCpuState>(cpuType);
					return new JsonObject {
						["cpu_type"] = (int)cpuType,
						["cpu_type_name"] = cpuType.ToString(),
						["PC"] = state.PC,
						["SP"] = state.SP,
						["A"] = state.A,
						["X"] = state.X,
						["Y"] = state.Y,
						["PS"] = state.PS,
						["cycle_count"] = state.CycleCount
					};
				}
				default:
					return new JsonObject {
						["cpu_type"] = (int)cpuType,
						["cpu_type_name"] = cpuType.ToString(),
						["program_counter"] = DebugApi.GetProgramCounter(cpuType, false)
					};
			}
		}

		private static JsonObject GetPpuState(JsonObject? arguments)
		{
			CpuType cpuType = GetCpuType(arguments);
			return cpuType switch {
				CpuType.Snes => MakePpuState(cpuType, DebugApi.GetPpuState<SnesPpuState>(cpuType)),
				CpuType.Gameboy => MakePpuState(cpuType, DebugApi.GetPpuState<GbPpuState>(cpuType)),
				CpuType.Nes => MakePpuState(cpuType, DebugApi.GetPpuState<NesPpuState>(cpuType)),
				_ => throw new ArgumentException($"Structured PPU state is not available for {cpuType}.")
			};
		}

		private static JsonObject MakePpuState(CpuType cpuType, BaseState state)
		{
			return state switch {
				SnesPpuState snes => PpuStateObject(cpuType, snes.Scanline, snes.Cycle, snes.FrameCount),
				GbPpuState gameboy => PpuStateObject(cpuType, gameboy.Scanline, gameboy.Cycle, gameboy.FrameCount),
				NesPpuState nes => PpuStateObject(cpuType, nes.Scanline, nes.Cycle, nes.FrameCount),
				_ => throw new ArgumentException($"Structured PPU state is not available for {cpuType}.")
			};
		}

		private static JsonObject PpuStateObject(CpuType cpuType, int scanline, uint cycle, uint frameCount)
		{
			return new JsonObject {
				["cpu_type"] = (int)cpuType,
				["cpu_type_name"] = cpuType.ToString(),
				["scanline"] = scanline,
				["cycle"] = cycle,
				["frame_count"] = frameCount
			};
		}

		private static string GetScreen(JsonNode id, JsonObject? arguments)
		{
			bool applyVideoFilter = OptionalBool(arguments, "apply_video_filter", false);
			bool includeBase64 = OptionalBool(arguments, "include_base64", false);

			byte[]? png = EmuApi.GetScreenshotPng(applyVideoFilter);
			if(png == null) {
				throw new InvalidOperationException("No rendered frame is available yet. Let the emulator run at least one frame, then try again.");
			}

			(int width, int height) = ReadPngDimensions(png);
			CpuType cpuType = GetCpuType(null);
			JsonObject data = new() {
				["width"] = width,
				["height"] = height,
				["mime_type"] = "image/png",
				["byte_length"] = png.Length,
				["video_filter_applied"] = applyVideoFilter,
				["frame_count"] = EmuApi.GetTimingInfo(cpuType).FrameCount,
				["emulation_paused"] = EmuApi.IsPaused()
			};

			string base64 = Convert.ToBase64String(png);
			if(includeBase64) {
				data["png_base64"] = base64;
			}
			return ToolSuccessWithImage(id, data, base64, "image/png");
		}

		// PNG files start with an 8-byte signature followed by the IHDR chunk, whose
		// data holds the big-endian width and height.
		private static (int Width, int Height) ReadPngDimensions(byte[] png)
		{
			if(png.Length < 24) {
				throw new InvalidOperationException("The screenshot data is not a valid PNG.");
			}
			int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
			int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
			return (width, height);
		}

		private static JsonObject GetMemoryRange(JsonObject? arguments)
		{
			MemoryType memoryType = GetMemoryType(arguments, "memory_type");
			int startAddress = RequiredInt(arguments, "start_address", 0, Int32.MaxValue);
			int length = RequiredInt(arguments, "length", 1, MaxMemoryTransfer);
			string format = OptionalString(arguments, "format", "hex");
			if(format != "hex" && format != "base64" && format != "bytes") {
				throw new ArgumentException("format must be hex, base64, or bytes.");
			}
			ValidateMemoryRange(memoryType, startAddress, length);

			byte[] bytes = DebugApi.GetMemoryValues(memoryType, (uint)startAddress, (uint)(startAddress + length - 1));
			JsonObject result = new() {
				["memory_type"] = (int)memoryType,
				["memory_type_name"] = memoryType.ToString(),
				["start_address"] = startAddress,
				["length"] = bytes.Length,
				["format"] = format
			};

			// Only one encoding is emitted. Returning several costs a large multiple
			// of the data size for no extra information.
			if(format == "hex") {
				result["hex"] = Convert.ToHexString(bytes);
			} else if(format == "base64") {
				result["base64"] = Convert.ToBase64String(bytes);
			} else {
				JsonArray values = new();
				foreach(byte value in bytes) {
					values.Add((JsonNode)value);
				}
				result["bytes"] = values;
			}
			return result;
		}

		private static JsonObject SearchMemory(JsonObject? arguments)
		{
			MemoryType memoryType = GetMemoryType(arguments, "memory_type");
			int memorySize = DebugApi.GetMemorySize(memoryType);
			(byte[] pattern, bool[] mask) = ParseSearchPattern(RequiredString(arguments, "pattern"));
			int startAddress = OptionalInt(arguments, "start_address", 0, 0, Int32.MaxValue);
			int endAddress = OptionalInt(arguments, "end_address", memorySize - 1, 0, Int32.MaxValue);
			int maxResults = OptionalInt(arguments, "max_results", 100, 1, MaxSearchResults);

			endAddress = Math.Min(endAddress, memorySize - 1);
			if(startAddress > endAddress) {
				throw new ArgumentException($"start_address must not be past end_address ({endAddress}).");
			}

			JsonArray addresses = new();
			bool truncated = false;
			int lastStart = endAddress - pattern.Length + 1;
			int chunkStart = startAddress;
			while(chunkStart <= lastStart && !truncated) {
				// Chunks overlap by pattern.Length - 1 bytes so a match straddling a
				// chunk boundary is still found.
				int chunkEnd = (int)Math.Min((long)chunkStart + SearchChunkSize - 1, endAddress);
				byte[] chunk = DebugApi.GetMemoryValues(memoryType, (uint)chunkStart, (uint)chunkEnd);
				int limit = chunk.Length - pattern.Length;
				for(int offset = 0; offset <= limit; offset++) {
					if(MatchesPattern(chunk, offset, pattern, mask)) {
						addresses.Add((JsonNode)(chunkStart + offset));
						if(addresses.Count >= maxResults) {
							truncated = true;
							break;
						}
					}
				}
				chunkStart = chunkEnd - pattern.Length + 2;
			}

			return new JsonObject {
				["memory_type"] = (int)memoryType,
				["memory_type_name"] = memoryType.ToString(),
				["pattern_length"] = pattern.Length,
				["searched_start"] = startAddress,
				["searched_end"] = endAddress,
				["match_count"] = addresses.Count,
				["truncated"] = truncated,
				["addresses"] = addresses
			};
		}

		private static JsonObject SetMemory(JsonObject? arguments)
		{
			MemoryType memoryType = GetMemoryType(arguments, "memory_type");
			int address = RequiredInt(arguments, "address", 0, Int32.MaxValue);
			byte[] bytes = ReadWriteBytes(arguments);
			ValidateMemoryRange(memoryType, address, bytes.Length);

			DebugApi.SetMemoryValues(memoryType, (uint)address, bytes, bytes.Length);
			return new JsonObject {
				["success"] = true,
				["bytes_written"] = bytes.Length
			};
		}

		private static JsonObject SaveRom(JsonObject? arguments)
		{
			RomInfo romInfo = EnsureRomLoaded();
			// The format list is duplicated from SaveRomActionHelper rather than shared
			// because that helper reads MainWindowViewModel, and tool calls run on a
			// worker thread.
			if(!IsSaveRomSupported(romInfo.Format)) {
				throw new InvalidOperationException($"Saving is not supported for {romInfo.Format} ROMs.");
			}

			string path = RequiredString(arguments, "path");
			if(!Path.IsPathRooted(path)) {
				throw new ArgumentException("path must be an absolute path.");
			}
			bool asIps = OptionalBool(arguments, "as_ips", false);
			bool overwrite = OptionalBool(arguments, "overwrite", false);
			CdlStripOption stripOption = (CdlStripOption)OptionalInt(arguments, "cdl_strip_option", 0, 0, 2);

			string? directory = Path.GetDirectoryName(path);
			if(string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) {
				throw new ArgumentException($"Directory does not exist: {directory}");
			}
			// Refusing to clobber also stops this from replacing the ROM that is
			// currently loaded unless the caller explicitly asks for it.
			if(File.Exists(path) && !overwrite) {
				throw new ArgumentException($"{path} already exists. Pass overwrite=true to replace it.");
			}

			if(!DebugApi.SaveRomToDisk(path, asIps, stripOption)) {
				throw new InvalidOperationException("Mesen could not write the file. Check the path and its permissions.");
			}

			return new JsonObject {
				["success"] = true,
				["path"] = path,
				["as_ips"] = asIps,
				["bytes_written"] = new FileInfo(path).Length
			};
		}

		private static JsonObject SaveState(JsonObject? arguments)
		{
			CpuType cpuType = GetCpuType(arguments);
			string path = RequiredString(arguments, "path");
			if(!Path.IsPathRooted(path)) {
				throw new ArgumentException("path must be an absolute path.");
			}
			bool overwrite = OptionalBool(arguments, "overwrite", false);

			string? directory = Path.GetDirectoryName(path);
			if(string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) {
				throw new ArgumentException($"Directory does not exist: {directory}");
			}
			if(Directory.Exists(path)) {
				throw new ArgumentException($"path refers to a directory: {path}");
			}
			if(File.Exists(path) && !overwrite) {
				throw new InvalidOperationException($"State file already exists: {path}. Set overwrite to true to replace it.");
			}

			EmuApi.SaveStateFile(path);

			// SaveStateFile returns void in the interop layer, so a missing or empty
			// file is the only way to detect that Mesen refused to write it.
			FileInfo file = new(path);
			if(!file.Exists || file.Length == 0) {
				throw new InvalidOperationException($"Mesen did not write the state file: {path}.");
			}

			TimingInfo timing = EmuApi.GetTimingInfo(cpuType);
			return new JsonObject {
				["success"] = true,
				["path"] = path,
				["size"] = file.Length,
				["frame_count"] = timing.FrameCount,
				["console_type_name"] = EnsureRomLoaded().ConsoleType.ToString(),
				["execution_stopped"] = DebugApi.IsDebuggerRunning() && DebugApi.IsExecutionStopped(),
				["emulation_running"] = EmuApi.IsRunning()
			};
		}

		private static JsonObject LoadState(JsonObject? arguments)
		{
			CpuType cpuType = GetCpuType(arguments);
			string path = RequiredString(arguments, "path");
			if(!Path.IsPathRooted(path)) {
				throw new ArgumentException("path must be an absolute path.");
			}
			if(!File.Exists(path)) {
				throw new ArgumentException($"State file does not exist: {path}");
			}

			EmuApi.LoadStateFile(path);

			TimingInfo timing = EmuApi.GetTimingInfo(cpuType);
			return new JsonObject {
				["success"] = true,
				["path"] = path,
				["frame_count"] = timing.FrameCount,
				["console_type_name"] = EnsureRomLoaded().ConsoleType.ToString(),
				["execution_stopped"] = DebugApi.IsDebuggerRunning() && DebugApi.IsExecutionStopped(),
				["emulation_running"] = EmuApi.IsRunning()
			};
		}

		private static bool IsSaveRomSupported(RomFormat format)
		{
			return format switch {
				RomFormat.Sfc => true,
				RomFormat.Gb => true,
				RomFormat.Gbs => true,
				RomFormat.iNes => true,
				RomFormat.VsDualSystem => true,
				RomFormat.VsSystem => true,
				RomFormat.Pce => true,
				RomFormat.Sms => true,
				RomFormat.Sg => true,
				RomFormat.ColecoVision => true,
				RomFormat.Gba => true,
				RomFormat.Ws => true,
				_ => false
			};
		}

		private static JsonObject GetDisassembly(JsonObject? arguments)
		{
			CpuType cpuType = GetCpuType(arguments);
			int lineCount = OptionalInt(arguments, "line_count", 20, 1, 100);
			uint address = arguments?["address"] == null
				? DebugApi.GetProgramCounter(cpuType, false)
				: (uint)RequiredInt(arguments, "address", 0, Int32.MaxValue);

			int startAddress = DebugApi.GetDisassemblyRowAddress(cpuType, address, -(lineCount / 2));
			if(startAddress < 0) {
				throw new ArgumentException("The requested address cannot be disassembled.");
			}

			CodeLineData[] lines = DebugApi.GetDisassemblyOutput(cpuType, (uint)startAddress, (uint)lineCount);
			JsonArray output = new();
			foreach(CodeLineData line in lines) {
				if(line.Address < 0) {
					continue;
				}
				output.Add((JsonNode)new JsonObject {
					["address"] = line.Address,
					["text"] = line.Text.Trim(),
					["bytes"] = line.ByteCodeStr.Trim(),
					["size"] = line.OpSize
				});
			}

			return new JsonObject {
				["cpu_type"] = (int)cpuType,
				["current_pc"] = DebugApi.GetProgramCounter(cpuType, false),
				["lines"] = output
			};
		}

		private static JsonObject GetTraceTail(JsonObject? arguments)
		{
			int count = OptionalInt(arguments, "count", 100, 1, MaxTraceRows);
			int offset = OptionalInt(arguments, "offset", 0, 0, Int32.MaxValue);
			TraceRow[] rows = DebugApi.GetExecutionTrace((uint)offset, (uint)count);
			JsonArray lines = new();
			foreach(TraceRow row in rows) {
				byte[] byteCode = row.GetByteCode();
				StringBuilder hex = new();
				for(int index = 0; index < byteCode.Length; index++) {
					if(index > 0) {
						hex.Append(' ');
					}
					hex.Append(byteCode[index].ToString("X2"));
				}
				lines.Add((JsonNode)new JsonObject {
					["pc"] = row.ProgramCounter,
					["text"] = row.GetOutput(),
					["bytes"] = hex.ToString()
				});
			}

			return new JsonObject {
				["count"] = lines.Count,
				["lines"] = lines
			};
		}

		private static JsonObject GetDebugEvents(JsonObject? arguments)
		{
			CpuType cpuType = GetCpuType(arguments);
			int maxCount = OptionalInt(arguments, "max_count", 100, 1, MaxTraceRows);
			DebugEventInfo[] events = DebugApi.GetDebugEvents(cpuType);
			JsonArray output = new();
			for(int index = 0; index < Math.Min(events.Length, maxCount); index++) {
				DebugEventInfo debugEvent = events[index];
				output.Add((JsonNode)new JsonObject {
					["type"] = debugEvent.Type.ToString(),
					["pc"] = debugEvent.ProgramCounter,
					["scanline"] = debugEvent.Scanline,
					["cycle"] = debugEvent.Cycle,
					["breakpoint_id"] = debugEvent.BreakpointId
				});
			}

			return new JsonObject {
				["cpu_type"] = (int)cpuType,
				["count"] = output.Count,
				["events"] = output
			};
		}

		private static JsonObject SetBreakpoints(JsonObject? arguments)
		{
			JsonArray values = arguments?["breakpoints"] as JsonArray ?? throw new ArgumentException("Missing breakpoints array.");
			if(values.Count > MaxBreakpoints) {
				throw new ArgumentException($"At most {MaxBreakpoints} breakpoints can be set at once.");
			}

			InteropBreakpoint[] breakpoints = new InteropBreakpoint[values.Count];
			for(int index = 0; index < values.Count; index++) {
				JsonObject value = values[index] as JsonObject ?? throw new ArgumentException($"Breakpoint {index} is not an object.");
				CpuType cpuType = GetCpuType(value);
				MemoryType memoryType = value["memory_type"] == null
					? cpuType.ToMemoryType()
					: GetMemoryType(value, "memory_type");
				int startAddress = RequiredInt(value, "address", 0, Int32.MaxValue);
				int endAddress = OptionalInt(value, "end_address", startAddress, startAddress, Int32.MaxValue);
				long breakpointLength = (long)endAddress - startAddress + 1;
				ValidateMemoryRange(memoryType, startAddress, breakpointLength);

				int typeValue = OptionalInt(value, "type", (int)BreakpointTypeFlags.Execute, 1, 15);
				BreakpointTypeFlags type = (BreakpointTypeFlags)typeValue;
				BreakpointTypeFlags allowed = BreakpointTypeFlags.Read | BreakpointTypeFlags.Write | BreakpointTypeFlags.Execute | BreakpointTypeFlags.Forbid;
				if((type & ~allowed) != 0) {
					throw new ArgumentException($"Breakpoint {index} has unsupported type flags.");
				}

				byte[] conditionBuffer = new byte[1000];
				string condition = value["condition"]?.GetValue<string>()?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? "";
				byte[] conditionBytes = Encoding.UTF8.GetBytes(condition);
				if(conditionBytes.Length >= conditionBuffer.Length) {
					throw new ArgumentException($"Breakpoint {index} condition is too long after UTF-8 encoding.");
				}
				Array.Copy(conditionBytes, conditionBuffer, conditionBytes.Length);

				breakpoints[index] = new InteropBreakpoint {
					Id = index,
					CpuType = cpuType,
					MemoryType = memoryType,
					Type = type,
					StartAddress = startAddress,
					EndAddress = endAddress,
					Enabled = value["enabled"]?.GetValue<bool>() ?? true,
					MarkEvent = false,
					IgnoreDummyOperations = false,
					Condition = conditionBuffer
				};
			}

			DebugApi.SetBreakpoints(breakpoints, (uint)breakpoints.Length);
			return new JsonObject {
				["success"] = true,
				["breakpoints_set"] = breakpoints.Length
			};
		}

		private static JsonObject Step(JsonObject? arguments)
		{
			CpuType cpuType = GetCpuType(arguments);
			int count = OptionalInt(arguments, "count", 1, 0, 1000000);
			int stepTypeValue = OptionalInt(arguments, "step_type", 0, 0, 10);
			StepType stepType = (StepType)stepTypeValue;
			if(!Enum.IsDefined(stepType)) {
				throw new ArgumentException($"Unknown step_type value: {stepTypeValue}.");
			}

			DebugApi.Step(cpuType, count, stepType);
			return new JsonObject {
				["success"] = true,
				["cpu_type"] = (int)cpuType,
				["step_type"] = (int)stepType,
				["count"] = count
			};
		}

		private static JsonObject Resume()
		{
			EnsureRomLoaded();
			DebugApi.InitializeDebugger();
			DebugApi.ResumeExecution();
			return new JsonObject { ["success"] = true };
		}

		private static JsonObject Pause()
		{
			CpuType cpuType = GetCpuType(null);
			DebugApi.Step(cpuType, 1, StepType.Step);
			return new JsonObject {
				["success"] = true,
				["cpu_type"] = (int)cpuType
			};
		}

		private static JsonObject RunFrames(JsonObject? arguments)
		{
			CpuType cpuType = GetCpuType(arguments);
			int frames = RequiredInt(arguments, "frames", 1, 100000);
			int timeoutMs = OptionalInt(arguments, "timeout_ms", 5000, 100, 7000);

			if(!EmuApi.IsRunning()) {
				throw new InvalidOperationException("No emulation is running.");
			}

			DebugApi.InitializeDebugger();
			uint startFrame = EmuApi.GetTimingInfo(cpuType).FrameCount;
			DebugApi.ResumeExecution();

			bool timedOut = false;
			uint elapsed = 0;
			DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while(true) {
				// Unsigned subtraction handles FrameCount wrapping naturally.
				elapsed = EmuApi.GetTimingInfo(cpuType).FrameCount - startFrame;
				if(elapsed >= (uint)frames) {
					break;
				}
				if(!EmuApi.IsRunning()) {
					break;
				}
				if(DateTime.UtcNow >= deadline) {
					timedOut = true;
					break;
				}
				Thread.Sleep(1);
			}

			bool executionStopped = DebugApi.IsExecutionStopped();
			if(!executionStopped) {
				DebugApi.Step(cpuType, 1, StepType.Step);
				DateTime stopDeadline = DateTime.UtcNow.AddMilliseconds(1000);
				while(!DebugApi.IsExecutionStopped() && DateTime.UtcNow < stopDeadline) {
					Thread.Sleep(1);
				}
				executionStopped = DebugApi.IsExecutionStopped();
			}

			elapsed = EmuApi.GetTimingInfo(cpuType).FrameCount - startFrame;
			return new JsonObject {
				["success"] = elapsed >= (uint)frames,
				["frames_requested"] = frames,
				["frames_elapsed"] = elapsed,
				["timed_out"] = timedOut,
				["execution_stopped"] = executionStopped,
				["emulation_running"] = EmuApi.IsRunning(),
				["cpu_type"] = (int)cpuType
			};
		}

		private static JsonObject SetInput(JsonObject? arguments)
		{
			int controller = OptionalInt(arguments, "controller", 0, 0, 7);
			JsonArray? buttons = arguments?["buttons"] as JsonArray
				?? throw new ArgumentException("Missing required argument: buttons.");

			DebugControllerState state = new();
			JsonArray normalizedButtons = new();
			HashSet<string> seenButtons = new(StringComparer.Ordinal);
			foreach(JsonNode? node in buttons) {
				string? name = node?.GetValue<string>();
				if(string.IsNullOrWhiteSpace(name)) {
					throw new ArgumentException("Button names must be non-empty strings.");
				}

				string normalizedName = name.Trim().ToUpperInvariant();
				if(!seenButtons.Add(normalizedName)) {
					continue;
				}
				ApplyButton(ref state, normalizedName);
				normalizedButtons.Add((JsonNode)normalizedName);
			}

			DebugApi.InitializeDebugger();
			List<int> available = DebugApi.GetAvailableInputOverrides();
			if(!available.Contains(controller)) {
				throw new ArgumentException($"Controller {controller} is not available. Available controllers: [{string.Join(", ", available)}]. Configure the input port in Mesen if needed.");
			}
			DebugApi.SetInputOverrides((uint)controller, state);
			return MakeInputResult(controller, normalizedButtons);
		}

		private static JsonObject ClearInput(JsonObject? arguments)
		{
			int controller = OptionalInt(arguments, "controller", 0, 0, 7);
			DebugApi.InitializeDebugger();
			DebugApi.SetInputOverrides((uint)controller, new DebugControllerState());
			return MakeInputResult(controller, new JsonArray());
		}

		private static void ApplyButton(ref DebugControllerState state, string name)
		{
			switch(name) {
				case "A": state.A = true; break;
				case "B": state.B = true; break;
				case "X": state.X = true; break;
				case "Y": state.Y = true; break;
				case "L": state.L = true; break;
				case "R": state.R = true; break;
				case "U": state.U = true; break;
				case "UP": state.Up = true; break;
				case "D": state.D = true; break;
				case "DOWN": state.Down = true; break;
				case "LEFT": state.Left = true; break;
				case "RIGHT": state.Right = true; break;
				case "SELECT": state.Select = true; break;
				case "START": state.Start = true; break;
				default:
					throw new ArgumentException($"Unknown button: {name}. Valid buttons are: A, B, X, Y, L, R, U, D, Up, Down, Left, Right, Select, Start.");
			}
		}

		private static JsonObject MakeInputResult(int controller, JsonArray buttons)
		{
			JsonArray availableControllers = new();
			foreach(int index in DebugApi.GetAvailableInputOverrides()) {
				availableControllers.Add((JsonNode)index);
			}

			return new JsonObject {
				["success"] = true,
				["controller"] = controller,
				["buttons"] = buttons,
				["available_controllers"] = availableControllers,
				["debugger_running"] = DebugApi.IsDebuggerRunning()
			};
		}

		private static CpuType GetCpuType(JsonObject? arguments)
		{
			RomInfo romInfo = EnsureRomLoaded();
			CpuType cpuType = arguments?["cpu_type"] == null
				? romInfo.ConsoleType.GetMainCpuType()
				: (CpuType)RequiredInt(arguments, "cpu_type", Byte.MinValue, Byte.MaxValue);
			if(!Enum.IsDefined(cpuType)) {
				throw new ArgumentException($"Unknown CPU type: {(int)cpuType}.");
			}
			if(!romInfo.CpuTypes.Contains(cpuType)) {
				throw new ArgumentException($"CPU type {cpuType} is not available for the loaded ROM.");
			}
			return cpuType;
		}

		private static MemoryType GetMemoryType(JsonObject? arguments, string name)
		{
			EnsureRomLoaded();
			int value = RequiredInt(arguments, name, 0, Int32.MaxValue);
			MemoryType memoryType = (MemoryType)value;
			if(!Enum.IsDefined(memoryType) || memoryType == MemoryType.None) {
				throw new ArgumentException($"Unknown memory type: {value}.");
			}
			if(DebugApi.GetMemorySize(memoryType) <= 0) {
				throw new ArgumentException($"Memory type {memoryType} is not available for the loaded ROM.");
			}
			return memoryType;
		}

		private static bool IsRomLoaded(RomInfo romInfo)
		{
			return romInfo.Format != RomFormat.Unknown && EmuApi.IsRunning();
		}

		private static RomInfo EnsureRomLoaded()
		{
			RomInfo romInfo = EmuApi.GetRomInfo();
			if(!IsRomLoaded(romInfo)) {
				throw new InvalidOperationException("No ROM is loaded.");
			}
			return romInfo;
		}

		private static void ValidateMemoryRange(MemoryType memoryType, int startAddress, long length)
		{
			int memorySize = DebugApi.GetMemorySize(memoryType);
			long endAddress = (long)startAddress + length;
			if(startAddress < 0 || length < 1 || endAddress > memorySize) {
				throw new ArgumentException($"Memory range exceeds {memoryType} (size {memorySize}).");
			}
		}

		private static byte[] ReadWriteBytes(JsonObject? arguments)
		{
			JsonArray? data = arguments?["data"] as JsonArray;
			string? hex = arguments?["hex"]?.GetValue<string>();
			if(data != null && !string.IsNullOrWhiteSpace(hex)) {
				throw new ArgumentException("Provide either hex or data, not both.");
			}

			byte[] bytes;
			if(!string.IsNullOrWhiteSpace(hex)) {
				bytes = ParseHexBytes(hex);
			} else if(data != null) {
				bytes = new byte[data.Count];
				for(int index = 0; index < data.Count; index++) {
					int value = data[index]?.GetValue<int>() ?? throw new ArgumentException($"Data item {index} is missing.");
					if(value < 0 || value > 255) {
						throw new ArgumentException($"Data item {index} is outside the byte range.");
					}
					bytes[index] = (byte)value;
				}
			} else {
				throw new ArgumentException("Missing hex or data argument.");
			}

			if(bytes.Length < 1 || bytes.Length > MaxMemoryTransfer) {
				throw new ArgumentException($"Between 1 and {MaxMemoryTransfer} bytes must be written.");
			}
			return bytes;
		}

		private static byte[] ParseHexBytes(string hex)
		{
			string packed = new(hex.Where(character => !Char.IsWhiteSpace(character)).ToArray());
			if(packed.Length % 2 != 0) {
				throw new ArgumentException("hex must contain an even number of hex digits.");
			}
			try {
				return Convert.FromHexString(packed);
			} catch(FormatException) {
				throw new ArgumentException("hex contains a character that is not a hex digit.");
			}
		}

		// Returns the pattern bytes plus a mask where false means "any byte here".
		private static (byte[] Pattern, bool[] Mask) ParseSearchPattern(string pattern)
		{
			List<byte> bytes = new();
			List<bool> mask = new();
			string packed = new(pattern.Where(character => !Char.IsWhiteSpace(character)).ToArray());
			if(packed.Length % 2 != 0) {
				throw new ArgumentException("pattern must contain an even number of characters per byte.");
			}

			for(int index = 0; index < packed.Length; index += 2) {
				string pair = packed.Substring(index, 2);
				if(pair == "??" || pair == "**") {
					bytes.Add(0);
					mask.Add(false);
				} else if(Byte.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)) {
					bytes.Add(value);
					mask.Add(true);
				} else {
					throw new ArgumentException($"'{pair}' is not a hex byte or a ?? wildcard.");
				}
			}

			if(bytes.Count < 1) {
				throw new ArgumentException("pattern is empty.");
			}
			if(bytes.Count > MaxSearchPattern) {
				throw new ArgumentException($"pattern must be {MaxSearchPattern} bytes or fewer.");
			}
			if(!mask.Contains(true)) {
				throw new ArgumentException("pattern must contain at least one non-wildcard byte.");
			}
			return (bytes.ToArray(), mask.ToArray());
		}

		private static bool MatchesPattern(byte[] buffer, int offset, byte[] pattern, bool[] mask)
		{
			for(int index = 0; index < pattern.Length; index++) {
				if(mask[index] && buffer[offset + index] != pattern[index]) {
					return false;
				}
			}
			return true;
		}

		private static string RequiredString(JsonObject? arguments, string name)
		{
			string? value = arguments?[name]?.GetValue<string>();
			if(string.IsNullOrWhiteSpace(value)) {
				throw new ArgumentException($"Missing required argument: {name}.");
			}
			return value;
		}

		private static string OptionalString(JsonObject? arguments, string name, string defaultValue)
		{
			string? value = arguments?[name]?.GetValue<string>();
			return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
		}

		private static bool OptionalBool(JsonObject? arguments, string name, bool defaultValue)
		{
			return arguments?[name] == null ? defaultValue : arguments[name]!.GetValue<bool>();
		}

		private static int RequiredInt(JsonObject? arguments, string name, int minimum, int maximum)
		{
			if(arguments?[name] == null) {
				throw new ArgumentException($"Missing required argument: {name}.");
			}
			return RangedInt(arguments[name]!.GetValue<int>(), name, minimum, maximum);
		}

		private static int OptionalInt(JsonObject? arguments, string name, int defaultValue, int minimum, int maximum)
		{
			return arguments?[name] == null
				? defaultValue
				: RangedInt(arguments[name]!.GetValue<int>(), name, minimum, maximum);
		}

		private static int RangedInt(int value, string name, int minimum, int maximum)
		{
			if(value < minimum || value > maximum) {
				throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
			}
			return value;
		}

		private static JsonObject MakeToolDef(
			string name,
			string description,
			JsonObject inputSchema,
			bool readOnly,
			bool destructive = false,
			bool idempotent = false
		)
		{
			return new JsonObject {
				["name"] = name,
				["description"] = description,
				["inputSchema"] = inputSchema,
				["outputSchema"] = new JsonObject {
					["type"] = "object",
					["additionalProperties"] = true
				},
				["annotations"] = new JsonObject {
					["readOnlyHint"] = readOnly,
					["destructiveHint"] = destructive,
					["idempotentHint"] = idempotent,
					["openWorldHint"] = false
				}
			};
		}

		private static JsonObject EmptySchema()
		{
			return new JsonObject {
				["type"] = "object",
				["properties"] = new JsonObject()
			};
		}

		private static JsonObject IntegerProperty(
			string description,
			int? minimum = null,
			int? maximum = null,
			int? defaultValue = null
		)
		{
			JsonObject property = new() {
				["type"] = "integer",
				["description"] = description
			};
			if(minimum.HasValue) {
				property["minimum"] = minimum.Value;
			}
			if(maximum.HasValue) {
				property["maximum"] = maximum.Value;
			}
			if(defaultValue.HasValue) {
				property["default"] = defaultValue.Value;
			}
			return property;
		}

		private static JsonObject StringProperty(
			string description,
			string? defaultValue = null,
			string[]? allowedValues = null
		)
		{
			JsonObject property = new() {
				["type"] = "string",
				["description"] = description
			};
			if(defaultValue != null) {
				property["default"] = defaultValue;
			}
			if(allowedValues != null) {
				property["enum"] = StringArray(allowedValues);
			}
			return property;
		}

		private static JsonObject BooleanProperty(string description, bool defaultValue)
		{
			return new JsonObject {
				["type"] = "boolean",
				["description"] = description,
				["default"] = defaultValue
			};
		}

		private static JsonArray StringArray(params string[] values)
		{
			JsonArray array = new();
			foreach(string value in values) {
				array.Add((JsonNode)value);
			}
			return array;
		}

		private static string ToolSuccess(JsonNode id, JsonObject data)
		{
			string text = data.ToJsonString();
			return MakeJsonRpcResult(id, new JsonObject {
				["content"] = new JsonArray {
					(JsonNode)new JsonObject {
						["type"] = "text",
						["text"] = text
					}
				},
				["structuredContent"] = data,
				["isError"] = false
			});
		}

		// Like ToolSuccess, but the image travels as an MCP image content block so
		// clients can display it, while the JSON text block keeps the metadata.
		private static string ToolSuccessWithImage(JsonNode id, JsonObject data, string base64Image, string mimeType)
		{
			string text = data.ToJsonString();
			return MakeJsonRpcResult(id, new JsonObject {
				["content"] = new JsonArray {
					(JsonNode)new JsonObject {
						["type"] = "image",
						["data"] = base64Image,
						["mimeType"] = mimeType
					},
					(JsonNode)new JsonObject {
						["type"] = "text",
						["text"] = text
					}
				},
				["structuredContent"] = data,
				["isError"] = false
			});
		}

		private static string ToolError(JsonNode id, string message)
		{
			return MakeJsonRpcResult(id, new JsonObject {
				["content"] = new JsonArray {
					(JsonNode)new JsonObject {
						["type"] = "text",
						["text"] = message
					}
				},
				["isError"] = true
			});
		}

		private static string MakeJsonRpcResult(JsonNode id, JsonObject result)
		{
			return new JsonObject {
				["jsonrpc"] = "2.0",
				["id"] = id,
				["result"] = result
			}.ToJsonString();
		}

		private static string MakeJsonRpcError(JsonNode? id, int code, string message)
		{
			return new JsonObject {
				["jsonrpc"] = "2.0",
				["id"] = id,
				["error"] = new JsonObject {
					["code"] = code,
					["message"] = message
				}
			}.ToJsonString();
		}
	}
}
