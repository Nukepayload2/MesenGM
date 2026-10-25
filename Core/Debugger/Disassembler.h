#pragma once
#include "pch.h"
#include "Debugger/DisassemblyInfo.h"
#include "Debugger/DebugTypes.h"
#include "Debugger/DebugUtilities.h"

class IConsole;
class Debugger;
class LabelManager;
class CodeDataLogger;
class MemoryDumper;
class DisassemblySearch;
class EmuSettings;
struct SnesCpuState;
enum class CpuType : uint8_t;

struct DisassemblerSource
{
	vector<DisassemblyInfo> Cache;
	uint32_t Size = 0;
};

class Disassembler
{
private:
	friend class DisassemblySearch;

	IConsole* _console;
	EmuSettings* _settings;
	Debugger* _debugger;
	LabelManager* _labelManager;
	MemoryDumper* _memoryDumper;

	DisassemblerSource _sources[DebugUtilities::GetMemoryTypeCount()] = {};

	//Forces CDL-marked code to be decoded instead of collapsed into an
	//"unidentified" block. Used by the API entry points (MCP bridge), which
	//must always return real instructions regardless of the display settings.
	bool _forceDisassembleAll = false;

	struct ScopedForceDisassembly
	{
		Disassembler* _owner;
		bool _previous;
		explicit ScopedForceDisassembly(Disassembler* owner) : _owner(owner), _previous(owner->_forceDisassembleAll)
		{
			owner->_forceDisassembleAll = true;
		}
		~ScopedForceDisassembly()
		{
			_owner->_forceDisassembleAll = _previous;
		}
	};

	void InitSource(MemoryType type);
	DisassemblerSource& GetSource(MemoryType type);

	void GetLineData(DisassemblyResult& result, CpuType type, MemoryType memType, CodeLineData& data);
	int32_t GetMatchingRow(vector<DisassemblyResult>& rows, uint32_t address, bool returnFirstRow);
	vector<DisassemblyResult> Disassemble(CpuType cpuType, uint16_t bank);
	uint16_t GetMaxBank(CpuType cpuType);

public:
	Disassembler(IConsole* console, Debugger* debugger);

	uint32_t BuildCache(AddressInfo& addrInfo, uint8_t cpuFlags, CpuType type);
	void ResetPrgCache();
	void InvalidateCache(AddressInfo addrInfo, CpuType type);

	__forceinline DisassemblyInfo GetDisassemblyInfo(AddressInfo& info, uint32_t cpuAddress, uint8_t cpuFlags, CpuType type)
	{
		DisassemblyInfo disassemblyInfo;
		if(info.Address >= 0) {
			disassemblyInfo = GetSource(info.Type).Cache[info.Address];
		}

		if(!disassemblyInfo.IsInitialized()) {
			disassemblyInfo.Initialize(cpuAddress, cpuFlags, type, DebugUtilities::GetCpuMemoryType(type), _memoryDumper);
		}
		return disassemblyInfo;
	}

	uint32_t GetDisassemblyOutput(CpuType type, uint32_t address, CodeLineData output[], uint32_t rowCount);
	int32_t GetDisassemblyRowAddress(CpuType type, uint32_t address, int32_t rowOffset);
};