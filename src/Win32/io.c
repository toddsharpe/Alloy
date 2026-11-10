// Output and text: the standard handles, WriteFile to pipes or the kernel log, the console questions CoreLib asks, and UTF-8 to UTF-16 conversion
#include "win32.h"

#define PhxLogLineMax 200

static PhxLock s_logLock;
static char s_logLine[PhxLogLineMax];
static size_t s_logLength;

static void PhxLogFlushLocked(void)
{
	if (s_logLength == 0)
		return;
	PhxSyscall2(PhxSysDebugPrint, s_logLine, s_logLength);
	s_logLength = 0;
}

// Whole lines go to the kernel log, as the user runtime's Console does; a carriage return is dropped since the log adds its own line ends
void PhxLogWrite(const char* bytes, size_t count)
{
	PhxLockAcquire(&s_logLock);
	for (size_t i = 0; i < count; i++)
	{
		char c = bytes[i];
		if (c == '\r')
			continue;
		if (c == '\n')
		{
			PhxLogFlushLocked();
			continue;
		}
		if (s_logLength == PhxLogLineMax)
			PhxLogFlushLocked();
		s_logLine[s_logLength++] = c;
	}
	PhxLockRelease(&s_logLock);
}

void PhxLogFlush(void)
{
	PhxLockAcquire(&s_logLock);
	PhxLogFlushLocked();
	PhxLockRelease(&s_logLock);
}

void PhxLogFormat(const char* format, ...)
{
	char line[PhxLogLineMax];
	va_list arguments;
	va_start(arguments, format);
	int length = PhxFormatV(line, sizeof(line), format, arguments);
	va_end(arguments);
	PhxLockAcquire(&s_logLock);
	PhxLogFlushLocked();
	PhxSyscall2(PhxSysDebugPrint, line, length < (int)sizeof(line) ? (size_t)length : sizeof(line));
	PhxLockRelease(&s_logLock);
}

HANDLE GetStdHandle(DWORD which)
{
	switch (which)
	{
		case STD_INPUT_HANDLE:
			return g_phx.StdHandles[0];
		case STD_OUTPUT_HANDLE:
			return g_phx.StdHandles[1];
		case STD_ERROR_HANDLE:
			return g_phx.StdHandles[2];
		default:
			SetLastError(ERROR_INVALID_PARAMETER);
			return INVALID_HANDLE_VALUE;
	}
}

BOOL WriteFile(HANDLE file, const void* buffer, DWORD count, DWORD* written, PVOID overlapped)
{
	(void)overlapped;
	if (file == PhxLogHandle)
	{
		PhxLogWrite((const char*)buffer, count);
		if (written != NULL)
			*written = count;
		return TRUE;
	}
	if (file == NULL || file == INVALID_HANDLE_VALUE || file == PhxCurrentThreadHandle)
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return FALSE;
	}
	LONG64 result = PhxSyscall3(PhxSysWriteFile, file, buffer, count);
	if (result < 0)
	{
		if (written != NULL)
			*written = 0;
		SetLastError(result == PhxErrorClosed ? 232 : ERROR_INVALID_HANDLE);
		return FALSE;
	}
	if (written != NULL)
		*written = (DWORD)result;
	return TRUE;
}

static int PhxEncodeUtf8(DWORD code, char* out)
{
	if (code < 0x80)
	{
		out[0] = (char)code;
		return 1;
	}
	if (code < 0x800)
	{
		out[0] = (char)(0xC0 | (code >> 6));
		out[1] = (char)(0x80 | (code & 0x3F));
		return 2;
	}
	if (code < 0x10000)
	{
		out[0] = (char)(0xE0 | (code >> 12));
		out[1] = (char)(0x80 | ((code >> 6) & 0x3F));
		out[2] = (char)(0x80 | (code & 0x3F));
		return 3;
	}
	out[0] = (char)(0xF0 | (code >> 18));
	out[1] = (char)(0x80 | ((code >> 12) & 0x3F));
	out[2] = (char)(0x80 | ((code >> 6) & 0x3F));
	out[3] = (char)(0x80 | (code & 0x3F));
	return 4;
}

// Reads one UTF-16 code point, pairing surrogates; a lone surrogate becomes the replacement character
static DWORD PhxReadUtf16(const wchar_t* chars, int count, int* index)
{
	DWORD code = chars[(*index)++];
	if (code >= 0xD800 && code < 0xDC00 && *index < count && chars[*index] >= 0xDC00 && chars[*index] < 0xE000)
		code = 0x10000 + ((code - 0xD800) << 10) + (chars[(*index)++] - 0xDC00);
	else if (code >= 0xD800 && code < 0xE000)
		code = 0xFFFD;
	return code;
}

BOOL WriteConsoleW(HANDLE console, const void* buffer, DWORD count, DWORD* written, PVOID reserved)
{
	(void)reserved;
	const wchar_t* chars = (const wchar_t*)buffer;
	char bytes[256];
	int used = 0;
	for (int i = 0; i < (int)count;)
	{
		DWORD code = PhxReadUtf16(chars, (int)count, &i);
		if (used > (int)sizeof(bytes) - 4)
		{
			if (!WriteFile(console, bytes, used, NULL, NULL))
				return FALSE;
			used = 0;
		}
		used += PhxEncodeUtf8(code, bytes + used);
	}
	if (used > 0 && !WriteFile(console, bytes, used, NULL, NULL))
		return FALSE;
	if (written != NULL)
		*written = count;
	return TRUE;
}

// The kernel log is a console as far as CoreLib can tell; anything else it holds is a pipe from the terminal
BOOL GetConsoleMode(HANDLE console, DWORD* mode)
{
	if (console != PhxLogHandle)
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return FALSE;
	}
	*mode = 0x3;
	return TRUE;
}

DWORD GetConsoleOutputCP(void)
{
	return 65001;
}

DWORD GetFileType(HANDLE file)
{
	if (file == PhxLogHandle)
		return FILE_TYPE_CHAR;
	if (file == NULL || file == INVALID_HANDLE_VALUE || file == PhxCurrentThreadHandle)
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return FILE_TYPE_UNKNOWN;
	}
	SetLastError(ERROR_SUCCESS);
	return FILE_TYPE_PIPE;
}

// The image's initrd path with a root and Windows separators, so Environment.ProcessPath and AppContext.BaseDirectory have something sensible
DWORD GetModuleFileNameW(HMODULE module, wchar_t* buffer, DWORD size)
{
	(void)module;
	if (size == 0)
		return 0;
	DWORD length = 0;
	buffer[length++] = '\\';
	for (const char* c = g_phx.ImageName; *c != 0 && length < size - 1; c++)
		buffer[length++] = *c == '/' ? (wchar_t)'\\' : (wchar_t)(BYTE)*c;
	if (length == 1 && size > 8)
	{
		const char* fallback = "app.exe";
		for (const char* c = fallback; *c != 0; c++)
			buffer[length++] = (wchar_t)(BYTE)*c;
	}
	buffer[length] = 0;
	return length;
}

// The kernel's command line (ASCII, in the process block) as the wide string Environment.GetCommandLineArgs parses
wchar_t* GetCommandLineW(void)
{
	static wchar_t s_commandLine[0xB00 + 1];
	static BOOL s_built;
	if (!s_built)
	{
		size_t length = g_phx.Peb->CommandLineLength < 0xB00 ? (size_t)g_phx.Peb->CommandLineLength : 0xB00;
		for (size_t i = 0; i < length; i++)
			s_commandLine[i] = (wchar_t)(BYTE)g_phx.Peb->CommandLine[i];
		s_commandLine[length] = 0;
		s_built = TRUE;
	}
	return s_commandLine;
}

DWORD GetEnvironmentVariableW(const wchar_t* name, wchar_t* buffer, DWORD size)
{
	(void)name;
	(void)buffer;
	(void)size;
	SetLastError(ERROR_ENVVAR_NOT_FOUND);
	return 0;
}

void GetStartupInfoW(STARTUPINFOW* info)
{
	memset(info, 0, sizeof(STARTUPINFOW));
	info->cb = sizeof(STARTUPINFOW);
}

DWORD FormatMessageW(DWORD flags, const void* source, DWORD messageId, DWORD languageId, wchar_t* buffer, DWORD size, va_list* arguments)
{
	(void)flags;
	(void)source;
	(void)messageId;
	(void)languageId;
	(void)buffer;
	(void)size;
	(void)arguments;
	SetLastError(ERROR_MR_MID_NOT_FOUND);
	return 0;
}

HANDLE LocalFree(HANDLE memory)
{
	free(memory);
	return NULL;
}

static BOOL PhxIsUtf8CodePage(DWORD codePage)
{
	return codePage == 65001 || codePage == 0 || codePage == 1 || codePage == 3;
}

// Decodes one UTF-8 sequence; malformed input decodes to the replacement character one byte at a time
static DWORD PhxReadUtf8(const BYTE* bytes, int count, int* index, BOOL* valid)
{
	BYTE lead = bytes[(*index)++];
	*valid = TRUE;
	if (lead < 0x80)
		return lead;
	int extra = lead >= 0xF0 ? 3 : lead >= 0xE0 ? 2 : lead >= 0xC0 ? 1 : 0;
	DWORD code = lead & (0x3F >> extra);
	if (extra == 0 || *index + extra > count)
	{
		*valid = FALSE;
		return 0xFFFD;
	}
	for (int i = 0; i < extra; i++)
	{
		BYTE next = bytes[*index];
		if ((next & 0xC0) != 0x80)
		{
			*valid = FALSE;
			return 0xFFFD;
		}
		code = (code << 6) | (next & 0x3F);
		(*index)++;
	}
	return code;
}

int MultiByteToWideChar(DWORD codePage, DWORD flags, const char* bytes, int byteCount, wchar_t* chars, int charCount)
{
	BOOL utf8 = PhxIsUtf8CodePage(codePage);
	if (byteCount < 0)
		byteCount = (int)strlen(bytes) + 1;
	int produced = 0;
	for (int i = 0; i < byteCount;)
	{
		DWORD code;
		if (utf8)
		{
			BOOL valid;
			code = PhxReadUtf8((const BYTE*)bytes, byteCount, &i, &valid);
			if (!valid && (flags & 8) != 0)
			{
				SetLastError(ERROR_NO_UNICODE_TRANSLATION);
				return 0;
			}
		}
		else
		{
			code = (BYTE)bytes[i++];
		}
		int needed = code >= 0x10000 ? 2 : 1;
		if (charCount != 0)
		{
			if (produced + needed > charCount)
			{
				SetLastError(ERROR_INSUFFICIENT_BUFFER);
				return 0;
			}
			if (needed == 2)
			{
				chars[produced] = (wchar_t)(0xD800 + ((code - 0x10000) >> 10));
				chars[produced + 1] = (wchar_t)(0xDC00 + ((code - 0x10000) & 0x3FF));
			}
			else
			{
				chars[produced] = (wchar_t)code;
			}
		}
		produced += needed;
	}
	return produced;
}

int WideCharToMultiByte(DWORD codePage, DWORD flags, const wchar_t* chars, int charCount, char* bytes, int byteCount, const char* defaultChar, BOOL* usedDefault)
{
	(void)flags;
	BOOL utf8 = PhxIsUtf8CodePage(codePage);
	if (charCount < 0)
	{
		charCount = 0;
		while (chars[charCount] != 0)
			charCount++;
		charCount++;
	}
	if (usedDefault != NULL)
		*usedDefault = FALSE;
	int produced = 0;
	for (int i = 0; i < charCount;)
	{
		DWORD code = PhxReadUtf16(chars, charCount, &i);
		char encoded[4];
		int needed;
		if (utf8)
		{
			needed = PhxEncodeUtf8(code, encoded);
		}
		else
		{
			needed = 1;
			encoded[0] = code < 0x100 ? (char)code : defaultChar != NULL ? *defaultChar : '?';
			if (code >= 0x100 && usedDefault != NULL)
				*usedDefault = TRUE;
		}
		if (byteCount != 0)
		{
			if (produced + needed > byteCount)
			{
				SetLastError(ERROR_INSUFFICIENT_BUFFER);
				return 0;
			}
			memcpy(bytes + produced, encoded, (size_t)needed);
		}
		produced += needed;
	}
	return produced;
}
