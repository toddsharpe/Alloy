// The rest of what the runtime imports: modules and GetProcAddress, time, COM apartments, the event log, randomness, tokens and jobs, version checks
#include "win32.h"

PVOID PhxFindExport(const char* name);
BOOL PhxRandom64(ULONG64* value);

static ULONG64 s_fileTimeBase;
static BOOL s_hasRdrand;
static ULONG64 s_randomState;

// The one image is every module there is: NULL and the exe's own name are it, and the Windows DLL names all map to the layer
static BOOL PhxNameMatches(const wchar_t* name, const char* candidate)
{
	for (;; name++, candidate++)
	{
		wchar_t c = *name;
		if (c >= 'A' && c <= 'Z')
			c = (wchar_t)(c + 32);
		if (*candidate == 0)
			return c == 0 || c == '.';
		if (c != (wchar_t)(BYTE)*candidate)
			return FALSE;
	}
}

static HMODULE PhxFindModule(const wchar_t* name)
{
	static const char* const systemNames[] = { "kernel32", "kernelbase", "ntdll", "advapi32", "ole32", "combase", "user32", "bcrypt", "psapi", "api-ms-win-core-fibers-l1-1-1", "api-ms-win-core-synch-l1-2-0" };
	if (name == NULL)
		return PhxImageModule;
	for (size_t i = 0; i < sizeof(systemNames) / sizeof(systemNames[0]); i++)
	{
		if (PhxNameMatches(name, systemNames[i]))
			return PhxSystemModule;
	}
	const wchar_t apiSet[] = L"api-ms-win-";
	BOOL isApiSet = TRUE;
	for (int i = 0; apiSet[i] != 0; i++)
	{
		wchar_t c = name[i];
		if (c >= 'A' && c <= 'Z')
			c = (wchar_t)(c + 32);
		if (c != apiSet[i])
			isApiSet = FALSE;
	}
	return isApiSet ? PhxSystemModule : NULL;
}

HMODULE GetModuleHandleW(const wchar_t* name)
{
	HMODULE module = PhxFindModule(name);
	if (module == NULL)
		SetLastError(ERROR_MOD_NOT_FOUND);
	return module;
}

BOOL GetModuleHandleExW(DWORD flags, const wchar_t* name, HMODULE* module)
{
	if ((flags & 4) != 0)
	{
		BOOL inside = (const BYTE*)name >= g_phx.ImageBase && (const BYTE*)name < g_phx.ImageBase + g_phx.ImageSize;
		*module = inside ? PhxImageModule : NULL;
	}
	else
	{
		*module = PhxFindModule(name);
	}
	if (*module == NULL)
	{
		SetLastError(ERROR_MOD_NOT_FOUND);
		return FALSE;
	}
	return TRUE;
}

HMODULE LoadLibraryExW(const wchar_t* name, HANDLE file, DWORD flags)
{
	(void)file;
	(void)flags;
	return GetModuleHandleW(name);
}

BOOL FreeLibrary(HMODULE module)
{
	(void)module;
	return TRUE;
}

// Only the layer's own functions can be found; a name it lacks is reported missing, which every caller treats as an optional API being absent
PVOID GetProcAddress(HMODULE module, const char* name)
{
	PVOID address = NULL;
	if (module == PhxSystemModule && (ULONG_PTR)name > 0xFFFF)
		address = PhxFindExport(name);
	if (address == NULL)
		SetLastError(ERROR_PROC_NOT_FOUND);
	return address;
}

PVOID EncodePointer(PVOID pointer)
{
	return pointer;
}

ULONGLONG VerSetConditionMask(ULONGLONG mask, DWORD type, BYTE condition)
{
	(void)type;
	(void)condition;
	return mask;
}

BOOL VerifyVersionInfoW(OSVERSIONINFOEXW* info, DWORD typeMask, DWORDLONG conditionMask)
{
	(void)info;
	(void)typeMask;
	(void)conditionMask;
	return TRUE;
}

// Days since 1970-01-01 for a civil date (Howard Hinnant's algorithm)
static LONG64 PhxDaysFromCivil(LONG64 year, DWORD month, DWORD day)
{
	year -= month <= 2;
	LONG64 era = (year >= 0 ? year : year - 399) / 400;
	LONG64 yearOfEra = year - era * 400;
	LONG64 dayOfYear = (153 * (LONG64)(month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
	LONG64 dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
	return era * 146097 + dayOfEra - 719468;
}

ULONG64 PhxReferenceTime(void)
{
	return (ULONG64)PhxSyscall0(PhxSysQueryTime);
}

// Wall-clock time is the RTC read once at start, carried forward by the 100 ns reference counter
void PhxInitializeTime(void)
{
	PhxSystemTime time;
	memset(&time, 0, sizeof(time));
	PhxSyscall1(PhxSysGetSystemTime, &time);
	ULONG64 reference = PhxReferenceTime();
	LONG64 days = PhxDaysFromCivil(time.Year, time.Month, time.Day) + 134774;
	ULONG64 seconds = (ULONG64)days * 86400 + (ULONG64)time.Hour * 3600 + (ULONG64)time.Minute * 60 + time.Second;
	s_fileTimeBase = seconds * 10000000 + (ULONG64)time.Milliseconds * 10000 - reference;

	int info[4];
	__cpuid(info, 1);
	s_hasRdrand = (info[2] & (1 << 30)) != 0;
	s_randomState = __rdtsc() | 1;
}

void GetSystemTimeAsFileTime(FILETIME* time)
{
	ULONG64 now = s_fileTimeBase + PhxReferenceTime();
	time->dwLowDateTime = (DWORD)now;
	time->dwHighDateTime = (DWORD)(now >> 32);
}

// Civil time to a FILETIME, the same way the clock base is built; the runtime's DateTime code imports this and the two below
BOOL SystemTimeToFileTime(const SYSTEMTIME* systemTime, FILETIME* fileTime)
{
	LONG64 days = PhxDaysFromCivil(systemTime->wYear, systemTime->wMonth, systemTime->wDay) + 134774;
	ULONG64 seconds = (ULONG64)days * 86400 + (ULONG64)systemTime->wHour * 3600 + (ULONG64)systemTime->wMinute * 60 + systemTime->wSecond;
	ULONG64 ticks = seconds * 10000000 + (ULONG64)systemTime->wMilliseconds * 10000;
	fileTime->dwLowDateTime = (DWORD)ticks;
	fileTime->dwHighDateTime = (DWORD)(ticks >> 32);
	return TRUE;
}

// A FILETIME back to civil time (Hinnant's algorithm in reverse), for DateTime.UtcNow's fallback path
BOOL FileTimeToSystemTime(const FILETIME* fileTime, SYSTEMTIME* systemTime)
{
	ULONG64 ticks = ((ULONG64)fileTime->dwHighDateTime << 32) | fileTime->dwLowDateTime;
	ULONG64 seconds = ticks / 10000000;
	LONG64 days = (LONG64)(seconds / 86400) - 134774;
	DWORD secondOfDay = (DWORD)(seconds % 86400);
	LONG64 z = days + 719468;
	LONG64 era = (z >= 0 ? z : z - 146096) / 146097;
	LONG64 dayOfEra = z - era * 146097;
	LONG64 yearOfEra = (dayOfEra - dayOfEra / 1460 + dayOfEra / 36524 - dayOfEra / 146096) / 365;
	LONG64 dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
	LONG64 mp = (5 * dayOfYear + 2) / 153;
	LONG64 day = dayOfYear - (153 * mp + 2) / 5 + 1;
	LONG64 month = mp < 10 ? mp + 3 : mp - 9;
	LONG64 year = yearOfEra + era * 400 + (month <= 2);
	systemTime->wYear = (WORD)year;
	systemTime->wMonth = (WORD)month;
	systemTime->wDay = (WORD)day;
	systemTime->wDayOfWeek = (WORD)(((days % 7) + 11) % 7);
	systemTime->wHour = (WORD)(secondOfDay / 3600);
	systemTime->wMinute = (WORD)(secondOfDay / 60 % 60);
	systemTime->wSecond = (WORD)(secondOfDay % 60);
	systemTime->wMilliseconds = (WORD)(ticks / 10000 % 1000);
	return TRUE;
}

void GetSystemTime(SYSTEMTIME* systemTime)
{
	FILETIME now;
	GetSystemTimeAsFileTime(&now);
	FileTimeToSystemTime(&now, systemTime);
}

// Regex's non-backtracking engine links the thread pool and condition variables through Task; the pages' regexes never take that path
PTP_WORK CreateThreadpoolWork(PTP_WORK_CALLBACK callback, PVOID context, PTP_CALLBACK_ENVIRON environment)
{
	(void)callback;
	(void)context;
	(void)environment;
	PhxFatal(STATUS_FAIL_FAST_EXCEPTION, "CreateThreadpoolWork: the thread pool is not available");
}

void SubmitThreadpoolWork(PTP_WORK work)
{
	(void)work;
}

void CloseThreadpoolWork(PTP_WORK work)
{
	(void)work;
}

void InitializeConditionVariable(CONDITION_VARIABLE* variable)
{
	variable->Ptr = NULL;
}

BOOL SleepConditionVariableCS(CONDITION_VARIABLE* variable, CRITICAL_SECTION* section, DWORD milliseconds)
{
	(void)variable;
	(void)section;
	(void)milliseconds;
	PhxFatal(STATUS_FAIL_FAST_EXCEPTION, "SleepConditionVariableCS: condition variables are not available");
}

void WakeConditionVariable(CONDITION_VARIABLE* variable)
{
	(void)variable;
}

// Alloy keeps the clock in UTC, so local and universal time are the same
BOOL TzSpecificLocalTimeToSystemTime(PVOID timeZone, const SYSTEMTIME* localTime, SYSTEMTIME* universalTime)
{
	(void)timeZone;
	*universalTime = *localTime;
	return TRUE;
}

BOOL SetThreadErrorMode(DWORD newMode, DWORD* oldMode)
{
	(void)newMode;
	if (oldMode != NULL)
		*oldMode = 0;
	return TRUE;
}

BOOL QueryPerformanceCounter(LONGLONG* counter)
{
	*counter = (LONGLONG)PhxReferenceTime();
	return TRUE;
}

BOOL QueryPerformanceFrequency(LONGLONG* frequency)
{
	*frequency = 10000000;
	return TRUE;
}

ULONGLONG GetTickCount64(void)
{
	return (ULONGLONG)PhxSyscall0(PhxSysGetTickCount);
}

// rdrand when the CPU has it, otherwise xorshift seeded from the TSC; the runtime uses this for hash seeds and Random
LONG BCryptGenRandom(PVOID algorithm, BYTE* buffer, ULONG count, ULONG flags)
{
	(void)algorithm;
	(void)flags;
	for (ULONG offset = 0; offset < count; offset += 8)
	{
		ULONG64 value;
		if (!s_hasRdrand || !PhxRandom64(&value))
		{
			ULONG64 x = s_randomState;
			x ^= x << 13;
			x ^= x >> 7;
			x ^= x << 17;
			s_randomState = x;
			value = x * 0x2545F4914F6CDD1DULL;
		}
		ULONG chunk = count - offset < 8 ? count - offset : 8;
		memcpy(buffer + offset, &value, chunk);
	}
	return 0;
}

// COM: every thread that asks is in the one multithreaded apartment
HRESULT CoInitializeEx(PVOID reserved, DWORD model)
{
	(void)reserved;
	(void)model;
	PhxTeb* teb = PhxCurrentTeb();
	return teb->ComInitialized++ == 0 ? 0 : 1;
}

void CoUninitialize(void)
{
	PhxTeb* teb = PhxCurrentTeb();
	if (teb->ComInitialized > 0)
		teb->ComInitialized--;
}

HRESULT CoGetApartmentType(int* type, int* qualifier)
{
	*qualifier = 0;
	if (PhxCurrentTeb()->ComInitialized == 0)
	{
		*type = 0;
		return (HRESULT)0x800401F0;
	}
	*type = 1;
	return 0;
}

HRESULT CoWaitForMultipleHandles(DWORD flags, DWORD timeout, ULONG count, HANDLE* handles, DWORD* index)
{
	DWORD result = WaitForMultipleObjectsEx(count, handles, (flags & 1) != 0, timeout, FALSE);
	if (result == WAIT_TIMEOUT)
		return (HRESULT)0x80010115;
	if (result == WAIT_FAILED)
		return (HRESULT)0x80004005;
	*index = result;
	return 0;
}

HANDLE RegisterEventSourceW(const wchar_t* server, const wchar_t* source)
{
	(void)server;
	(void)source;
	SetLastError(ERROR_CALL_NOT_IMPLEMENTED);
	return NULL;
}

BOOL ReportEventW(HANDLE log, WORD type, WORD category, DWORD eventId, PVOID sid, WORD stringCount, DWORD dataSize, const wchar_t** strings, PVOID data)
{
	(void)log;
	(void)type;
	(void)category;
	(void)eventId;
	(void)sid;
	(void)stringCount;
	(void)dataSize;
	(void)strings;
	(void)data;
	SetLastError(ERROR_CALL_NOT_IMPLEMENTED);
	return FALSE;
}

BOOL DeregisterEventSource(HANDLE source)
{
	(void)source;
	return TRUE;
}

// No tokens, privileges or jobs: large pages stay off and there is no memory limit from a job
BOOL OpenProcessToken(HANDLE process, DWORD access, HANDLE* token)
{
	(void)process;
	(void)access;
	*token = NULL;
	SetLastError(ERROR_CALL_NOT_IMPLEMENTED);
	return FALSE;
}

BOOL AdjustTokenPrivileges(HANDLE token, BOOL disableAll, TOKEN_PRIVILEGES* newState, DWORD bufferLength, TOKEN_PRIVILEGES* previousState, DWORD* returnLength)
{
	(void)token;
	(void)disableAll;
	(void)newState;
	(void)bufferLength;
	(void)previousState;
	(void)returnLength;
	SetLastError(ERROR_CALL_NOT_IMPLEMENTED);
	return FALSE;
}

BOOL LookupPrivilegeValueW(const wchar_t* system, const wchar_t* name, LUID* luid)
{
	(void)system;
	(void)name;
	(void)luid;
	SetLastError(ERROR_CALL_NOT_IMPLEMENTED);
	return FALSE;
}

BOOL IsProcessInJob(HANDLE process, HANDLE job, BOOL* result)
{
	(void)process;
	(void)job;
	*result = FALSE;
	return TRUE;
}

BOOL QueryInformationJobObject(HANDLE job, int information, PVOID buffer, DWORD length, DWORD* returned)
{
	(void)job;
	(void)information;
	(void)buffer;
	(void)length;
	(void)returned;
	SetLastError(ERROR_INVALID_PARAMETER);
	return FALSE;
}
