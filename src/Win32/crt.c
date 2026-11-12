// The UCRT functions libcmt's startup and the runtime import: strings, number parsing, the printf family, the atexit tables and the initialization hooks
#include "win32.h"

// Intrinsics under /Oi, implemented here
#pragma function(strlen, strcmp, strcpy)

// libcmt's pre-terminator and terminator tables
extern _PVFV __xp_a[];
extern _PVFV __xp_z[];
extern _PVFV __xt_a[];
extern _PVFV __xt_z[];

static PhxLock s_exitLock;
static _onexit_table_t s_atexitTable;
static _onexit_table_t s_quickExitTable;
static BOOL s_terminationComplete;

size_t strlen(const char* text)
{
	const char* at = text;
	while (*at != 0)
		at++;
	return (size_t)(at - text);
}

size_t strnlen(const char* text, size_t max)
{
	size_t length = 0;
	while (length < max && text[length] != 0)
		length++;
	return length;
}

int strcmp(const char* left, const char* right)
{
	while (*left != 0 && *left == *right)
	{
		left++;
		right++;
	}
	return (BYTE)*left - (BYTE)*right;
}

int strncmp(const char* left, const char* right, size_t count)
{
	for (; count > 0; count--, left++, right++)
	{
		if (*left != *right || *left == 0)
			return (BYTE)*left - (BYTE)*right;
	}
	return 0;
}

static int PhxLower(int c)
{
	return c >= 'A' && c <= 'Z' ? c + 32 : c;
}

int _stricmp(const char* left, const char* right)
{
	for (;; left++, right++)
	{
		int difference = PhxLower((BYTE)*left) - PhxLower((BYTE)*right);
		if (difference != 0 || *left == 0)
			return difference;
	}
}

int wcsncmp(const wchar_t* left, const wchar_t* right, size_t count)
{
	for (; count > 0; count--, left++, right++)
	{
		if (*left != *right || *left == 0)
			return *left - *right;
	}
	return 0;
}

char* strcpy(char* destination, const char* source)
{
	char* at = destination;
	while ((*at++ = *source++) != 0)
	{
	}
	return destination;
}

int strcpy_s(char* destination, size_t size, const char* source)
{
	if (destination == NULL || source == NULL || size == 0)
		return 22;
	size_t length = strlen(source);
	if (length >= size)
	{
		destination[0] = 0;
		return 34;
	}
	memcpy(destination, source, length + 1);
	return 0;
}

static int PhxIsSpace(char c)
{
	return c == ' ' || (c >= '\t' && c <= '\r');
}

static int PhxDigit(char c, int base)
{
	int value;
	if (c >= '0' && c <= '9')
		value = c - '0';
	else if (c >= 'a' && c <= 'z')
		value = c - 'a' + 10;
	else if (c >= 'A' && c <= 'Z')
		value = c - 'A' + 10;
	else
		return -1;
	return value < base ? value : -1;
}

unsigned __int64 strtoull(const char* text, char** end, int base)
{
	const char* at = text;
	while (PhxIsSpace(*at))
		at++;
	BOOL negative = FALSE;
	if (*at == '-' || *at == '+')
		negative = *at++ == '-';
	if ((base == 0 || base == 16) && at[0] == '0' && (at[1] == 'x' || at[1] == 'X') && PhxDigit(at[2], 16) >= 0)
	{
		base = 16;
		at += 2;
	}
	if (base == 0)
		base = *at == '0' ? 8 : 10;

	unsigned __int64 value = 0;
	const char* start = at;
	for (int digit; (digit = PhxDigit(*at, base)) >= 0; at++)
	{
		if (value > ((unsigned __int64)-1 - (unsigned __int64)digit) / (unsigned __int64)base)
			value = (unsigned __int64)-1;
		else
			value = value * (unsigned __int64)base + (unsigned __int64)digit;
	}
	if (end != NULL)
		*end = (char*)(at == start ? text : at);
	return negative ? (unsigned __int64)0 - value : value;
}

unsigned long strtoul(const char* text, char** end, int base)
{
	unsigned __int64 value = strtoull(text, end, base);
	return value > 0xFFFFFFFFULL ? 0xFFFFFFFFUL : (unsigned long)value;
}

long atol(const char* text)
{
	while (PhxIsSpace(*text))
		text++;
	BOOL negative = *text == '-';
	if (*text == '-' || *text == '+')
		text++;
	long value = 0;
	for (; *text >= '0' && *text <= '9'; text++)
		value = value * 10 + (*text - '0');
	return negative ? -value : value;
}

// printf: a writer that counts everything and stores what fits
typedef struct { char* Buffer; size_t Capacity; size_t Length; } PhxWriter;

static void PhxPut(PhxWriter* writer, char c)
{
	if (writer->Length < writer->Capacity)
		writer->Buffer[writer->Length] = c;
	writer->Length++;
}

static void PhxPutText(PhxWriter* writer, const char* text, size_t length)
{
	for (size_t i = 0; i < length; i++)
		PhxPut(writer, text[i]);
}

static void PhxPutRepeated(PhxWriter* writer, char c, int count)
{
	for (int i = 0; i < count; i++)
		PhxPut(writer, c);
}

// Emits sign and prefix, then digits padded to the width with spaces or zeros as the flags say
static void PhxPutNumber(PhxWriter* writer, const char* prefix, const char* digits, size_t digitCount, int precision, int width, BOOL left, BOOL zero)
{
	int zeros = precision > (int)digitCount ? precision - (int)digitCount : 0;
	int prefixLength = (int)strlen(prefix);
	int total = prefixLength + zeros + (int)digitCount;
	int pad = width > total ? width - total : 0;
	if (!left && !(zero && precision < 0))
		PhxPutRepeated(writer, ' ', pad);
	PhxPutText(writer, prefix, (size_t)prefixLength);
	if (!left && zero && precision < 0)
		PhxPutRepeated(writer, '0', pad);
	PhxPutRepeated(writer, '0', zeros);
	PhxPutText(writer, digits, digitCount);
	if (left)
		PhxPutRepeated(writer, ' ', pad);
}

static size_t PhxDigits(unsigned __int64 value, unsigned base, BOOL upper, char* digits)
{
	const char* alphabet = upper ? "0123456789ABCDEF" : "0123456789abcdef";
	char reversed[32];
	size_t count = 0;
	do
	{
		reversed[count++] = alphabet[value % base];
		value /= base;
	} while (value != 0);
	for (size_t i = 0; i < count; i++)
		digits[i] = reversed[count - 1 - i];
	return count;
}

// Fixed notation for a finite non-negative double below 1e18; larger values and %e fall back to exponent notation
static size_t PhxFixed(double value, int precision, char* out)
{
	unsigned __int64 scale = 1;
	for (int i = 0; i < precision && i < 18; i++)
		scale *= 10;
	double scaled = value * (double)scale + 0.5;
	unsigned __int64 whole = (unsigned __int64)(LONG64)(scaled / (double)scale);
	unsigned __int64 fraction = (unsigned __int64)(LONG64)(scaled - (double)whole * (double)scale);
	if (fraction >= scale)
	{
		whole++;
		fraction -= scale;
	}
	size_t length = PhxDigits(whole, 10, FALSE, out);
	if (precision > 0)
	{
		out[length++] = '.';
		char digits[32];
		size_t count = PhxDigits(fraction, 10, FALSE, digits);
		for (int i = 0; i < precision - (int)count; i++)
			out[length++] = '0';
		memcpy(out + length, digits, count);
		length += count;
	}
	return length;
}

static size_t PhxFloat(double value, char conversion, int precision, char* out)
{
	if (value != value)
	{
		memcpy(out, "nan", 3);
		return 3;
	}
	if (precision < 0)
		precision = 6;
	size_t length = 0;
	if (value < 0)
	{
		out[length++] = '-';
		value = -value;
	}
	if (value > 1.7976931348623157e308)
	{
		memcpy(out + length, "inf", 3);
		return length + 3;
	}
	BOOL lower = conversion == 'e' || conversion == 'f' || conversion == 'g';
	BOOL exponentForm = conversion == 'e' || conversion == 'E' || value >= 1e18;
	int exponent = 0;
	if (exponentForm || conversion == 'g' || conversion == 'G')
	{
		double mantissa = value;
		if (mantissa != 0)
		{
			while (mantissa >= 10)
			{
				mantissa /= 10;
				exponent++;
			}
			while (mantissa < 1)
			{
				mantissa *= 10;
				exponent--;
			}
		}
		if (conversion == 'g' || conversion == 'G')
		{
			if (precision == 0)
				precision = 1;
			exponentForm = exponent < -4 || exponent >= precision;
			precision = exponentForm ? precision - 1 : precision - 1 - exponent;
			if (precision < 0)
				precision = 0;
		}
		if (exponentForm)
		{
			length += PhxFixed(mantissa, precision, out + length);
			out[length++] = lower ? 'e' : 'E';
			out[length++] = exponent < 0 ? '-' : '+';
			unsigned magnitude = (unsigned)(exponent < 0 ? -exponent : exponent);
			if (magnitude < 10)
				out[length++] = '0';
			length += PhxDigits(magnitude, 10, FALSE, out + length);
			return length;
		}
	}
	length += PhxFixed(value, precision, out + length);
	if (conversion == 'g' || conversion == 'G')
	{
		while (length > 0 && out[length - 1] == '0')
			length--;
		if (length > 0 && out[length - 1] == '.')
			length--;
	}
	return length;
}

int PhxFormatV(char* buffer, size_t capacity, const char* format, va_list arguments)
{
	PhxWriter writer = { buffer, capacity, 0 };
	for (const char* at = format; *at != 0; at++)
	{
		if (*at != '%')
		{
			PhxPut(&writer, *at);
			continue;
		}
		at++;
		BOOL left = FALSE;
		BOOL plus = FALSE;
		BOOL space = FALSE;
		BOOL alternate = FALSE;
		BOOL zero = FALSE;
		for (;; at++)
		{
			if (*at == '-')
				left = TRUE;
			else if (*at == '+')
				plus = TRUE;
			else if (*at == ' ')
				space = TRUE;
			else if (*at == '#')
				alternate = TRUE;
			else if (*at == '0')
				zero = TRUE;
			else
				break;
		}
		int width = 0;
		if (*at == '*')
		{
			width = va_arg(arguments, int);
			if (width < 0)
			{
				left = TRUE;
				width = -width;
			}
			at++;
		}
		else
		{
			for (; *at >= '0' && *at <= '9'; at++)
				width = width * 10 + (*at - '0');
		}
		int precision = -1;
		if (*at == '.')
		{
			at++;
			precision = 0;
			if (*at == '*')
			{
				precision = va_arg(arguments, int);
				at++;
			}
			else
			{
				for (; *at >= '0' && *at <= '9'; at++)
					precision = precision * 10 + (*at - '0');
			}
		}
		int size = 32;
		BOOL wide = FALSE;
		for (;; at++)
		{
			if (*at == 'h')
				size = at[1] == 'h' ? (at++, 8) : 16;
			else if (*at == 'l')
				size = at[1] == 'l' ? (at++, 64) : 32, wide = TRUE;
			else if (*at == 'I')
			{
				if (at[1] == '6' && at[2] == '4')
					size = 64, at += 2;
				else if (at[1] == '3' && at[2] == '2')
					size = 32, at += 2;
				else
					size = 64;
			}
			else if (*at == 'z' || *at == 't' || *at == 'j')
				size = 64;
			else if (*at == 'w')
				wide = TRUE;
			else if (*at == 'L')
				size = 64;
			else
				break;
		}

		char conversion = *at;
		char digits[32];
		switch (conversion)
		{
			case '%':
				PhxPut(&writer, '%');
				break;
			case 'c':
			case 'C':
			{
				int c = va_arg(arguments, int);
				if (!left)
					PhxPutRepeated(&writer, ' ', width - 1);
				PhxPut(&writer, (char)c);
				if (left)
					PhxPutRepeated(&writer, ' ', width - 1);
				break;
			}
			case 's':
			case 'S':
			{
				const void* pointer = va_arg(arguments, const void*);
				BOOL isWide = conversion == 'S' ? !wide : wide;
				size_t length = 0;
				if (pointer == NULL)
				{
					pointer = "(null)";
					isWide = FALSE;
				}
				if (isWide)
					while (((const wchar_t*)pointer)[length] != 0 && (precision < 0 || length < (size_t)precision))
						length++;
				else
					while (((const char*)pointer)[length] != 0 && (precision < 0 || length < (size_t)precision))
						length++;
				int pad = width > (int)length ? width - (int)length : 0;
				if (!left)
					PhxPutRepeated(&writer, ' ', pad);
				for (size_t i = 0; i < length; i++)
				{
					if (isWide)
					{
						wchar_t c = ((const wchar_t*)pointer)[i];
						PhxPut(&writer, c < 0x80 ? (char)c : '?');
					}
					else
					{
						PhxPut(&writer, ((const char*)pointer)[i]);
					}
				}
				if (left)
					PhxPutRepeated(&writer, ' ', pad);
				break;
			}
			case 'd':
			case 'i':
			{
				__int64 value = size == 64 ? va_arg(arguments, __int64) : size == 16 ? (short)va_arg(arguments, int) : size == 8 ? (signed char)va_arg(arguments, int) : va_arg(arguments, int);
				const char* prefix = value < 0 ? "-" : plus ? "+" : space ? " " : "";
				unsigned __int64 magnitude = value < 0 ? (unsigned __int64)0 - (unsigned __int64)value : (unsigned __int64)value;
				PhxPutNumber(&writer, prefix, digits, PhxDigits(magnitude, 10, FALSE, digits), precision, width, left, zero);
				break;
			}
			case 'u':
			case 'x':
			case 'X':
			case 'o':
			{
				unsigned __int64 value = size == 64 ? va_arg(arguments, unsigned __int64) : size == 16 ? (unsigned short)va_arg(arguments, unsigned) : size == 8 ? (unsigned char)va_arg(arguments, unsigned) : va_arg(arguments, unsigned);
				unsigned base = conversion == 'u' ? 10 : conversion == 'o' ? 8 : 16;
				const char* prefix = alternate && value != 0 ? (conversion == 'x' ? "0x" : conversion == 'X' ? "0X" : conversion == 'o' ? "0" : "") : "";
				PhxPutNumber(&writer, prefix, digits, PhxDigits(value, base, conversion == 'X', digits), precision, width, left, zero);
				break;
			}
			case 'p':
			{
				unsigned __int64 value = (unsigned __int64)va_arg(arguments, void*);
				PhxPutNumber(&writer, "", digits, PhxDigits(value, 16, TRUE, digits), 16, width, left, FALSE);
				break;
			}
			case 'f':
			case 'F':
			case 'e':
			case 'E':
			case 'g':
			case 'G':
			{
				double value = va_arg(arguments, double);
				char text[64];
				size_t length = PhxFloat(value, conversion, precision, text);
				const char* prefix = text[0] == '-' ? "-" : plus ? "+" : space ? " " : "";
				size_t skip = text[0] == '-' ? 1 : 0;
				PhxPutNumber(&writer, prefix, text + skip, length - skip, -1, width, left, zero);
				break;
			}
			case 0:
				return (int)writer.Length;
			default:
				PhxPut(&writer, '%');
				PhxPut(&writer, conversion);
				break;
		}
	}
	if (writer.Length < capacity)
		buffer[writer.Length] = 0;
	return (int)writer.Length;
}

// The UCRT's one formatting entry behind sprintf, _snprintf and vsnprintf; option bit 2 asks for C99 snprintf semantics, bit 1 for a terminator even when truncated
int __stdio_common_vsprintf(unsigned __int64 options, char* buffer, size_t count, const char* format, PVOID locale, va_list arguments)
{
	(void)locale;
	size_t capacity = buffer == NULL ? 0 : count;
	int length = PhxFormatV(buffer, capacity, format, arguments);
	if ((options & 2) != 0)
	{
		if (capacity > 0 && (size_t)length >= capacity)
			buffer[capacity - 1] = 0;
		return length;
	}
	if ((size_t)length >= capacity && capacity != (size_t)-1)
	{
		if ((options & 1) != 0 && capacity > 0)
			buffer[capacity - 1] = 0;
		return capacity == 0 && buffer == NULL ? length : -1;
	}
	return length;
}

int __stdio_common_vsprintf_s(unsigned __int64 options, char* buffer, size_t count, const char* format, PVOID locale, va_list arguments)
{
	(void)options;
	(void)locale;
	if (buffer == NULL || count == 0)
		return -1;
	int length = PhxFormatV(buffer, count, format, arguments);
	if ((size_t)length >= count)
	{
		buffer[0] = 0;
		return -1;
	}
	return length;
}

int __stdio_common_vsnprintf_s(unsigned __int64 options, char* buffer, size_t bufferCount, size_t maxCount, const char* format, PVOID locale, va_list arguments)
{
	(void)options;
	(void)locale;
	if (buffer == NULL || bufferCount == 0)
		return -1;
	BOOL truncateAllowed = maxCount == (size_t)-1;
	size_t limit = truncateAllowed || maxCount >= bufferCount ? bufferCount - 1 : maxCount;
	int length = PhxFormatV(buffer, limit + 1, format, arguments);
	if ((size_t)length > limit)
	{
		if (truncateAllowed)
		{
			buffer[limit] = 0;
			return -1;
		}
		buffer[0] = 0;
		return -1;
	}
	return length;
}

// Exit tables: libcmt's onexit tables and the process's atexit list, run at exit in reverse order of registration
int _initialize_onexit_table(_onexit_table_t* table)
{
	if (table == NULL)
		return -1;
	table->_first = NULL;
	table->_last = NULL;
	table->_end = NULL;
	return 0;
}

int _register_onexit_function(_onexit_table_t* table, _PVFV function)
{
	if (table == NULL)
		return -1;
	PhxLockAcquire(&s_exitLock);
	if (table->_last == table->_end)
	{
		size_t count = (size_t)(table->_last - table->_first);
		size_t capacity = count == 0 ? 32 : count * 2;
		_PVFV* grown = (_PVFV*)malloc(capacity * sizeof(_PVFV));
		if (grown == NULL)
		{
			PhxLockRelease(&s_exitLock);
			return -1;
		}
		memcpy(grown, table->_first, count * sizeof(_PVFV));
		free(table->_first);
		table->_first = grown;
		table->_last = grown + count;
		table->_end = grown + capacity;
	}
	*table->_last++ = function;
	PhxLockRelease(&s_exitLock);
	return 0;
}

int _execute_onexit_table(_onexit_table_t* table)
{
	if (table == NULL)
		return -1;
	PhxLockAcquire(&s_exitLock);
	_PVFV* first = table->_first;
	_PVFV* last = table->_last;
	table->_first = NULL;
	table->_last = NULL;
	table->_end = NULL;
	PhxLockRelease(&s_exitLock);
	while (last > first)
	{
		last--;
		if (*last != NULL)
			(*last)();
	}
	free(first);
	return 0;
}

int _crt_atexit(_PVFV function)
{
	return _register_onexit_function(&s_atexitTable, function);
}

int _crt_at_quick_exit(_PVFV function)
{
	return _register_onexit_function(&s_quickExitTable, function);
}

void PhxRunExitTables(void)
{
	_execute_onexit_table(&s_atexitTable);
	for (_PVFV* terminator = __xp_a; terminator < __xp_z; terminator++)
	{
		if (*terminator != NULL)
			(*terminator)();
	}
	for (_PVFV* terminator = __xt_a; terminator < __xt_z; terminator++)
	{
		if (*terminator != NULL)
			(*terminator)();
	}
	s_terminationComplete = TRUE;
}

void _cexit(void)
{
	PhxRunExitTables();
}

BOOL _is_c_termination_complete(void)
{
	return s_terminationComplete;
}

// The UCRT's own initialization has nothing to set up here; the layer is ready before libcmt's startup pieces run
BOOL __acrt_initialize(void)
{
	return TRUE;
}

BOOL __acrt_uninitialize(BOOL terminating)
{
	(void)terminating;
	return TRUE;
}

BOOL __acrt_uninitialize_critical(BOOL terminating)
{
	(void)terminating;
	return TRUE;
}

BOOL __acrt_thread_attach(void)
{
	return TRUE;
}

BOOL __acrt_thread_detach(void)
{
	return TRUE;
}

int _configure_narrow_argv(int mode)
{
	(void)mode;
	return 0;
}

int _initialize_narrow_environment(void)
{
	return 0;
}

int _seh_filter_dll(unsigned long code, EXCEPTION_POINTERS* pointers)
{
	(void)code;
	(void)pointers;
	return EXCEPTION_CONTINUE_SEARCH;
}

// The CRT's single-precision rounding, which MathF.Floor and MathF.Ceiling compile to (win32.asm has the double ones); truncation then a step, exact below 2^63
#pragma function(floorf, ceilf)
float floorf(float value)
{
	float truncated = (float)(__int64)value;
	return truncated > value ? truncated - 1 : truncated;
}

float ceilf(float value)
{
	float truncated = (float)(__int64)value;
	return truncated < value ? truncated + 1 : truncated;
}

// The single-precision transcendentals MathF compiles to in unoptimized builds; the double versions in win32.asm do the work
double sin(double x);
double cos(double x);
double tan(double x);
double acos(double x);
double sqrt(double x);
double fmod(double x, double y);
#pragma function(sinf, cosf, tanf, acosf, sqrtf, fmodf)
float sinf(float x)
{
	return (float)sin(x);
}

float cosf(float x)
{
	return (float)cos(x);
}

float tanf(float x)
{
	return (float)tan(x);
}

float acosf(float x)
{
	return (float)acos(x);
}

float sqrtf(float x)
{
	return (float)sqrt(x);
}

float fmodf(float x, float y)
{
	return (float)fmod(x, y);
}

// The rest of the math library: special cases here in C, the transcendental core on the x87 in win32.asm
double PhxExpCore(double x);
double PhxPowCore(double x, double y);
double exp(double x);
double log(double x);
double sqrt(double x);
double floor(double x);
double ceil(double x);
double fmod(double x, double y);

static const union { unsigned __int64 bits; double value; } PhxInfinityBits = { 0x7FF0000000000000ULL };
static const union { unsigned __int64 bits; double value; } PhxNanBits = { 0x7FF8000000000000ULL };
#define PhxInfinity (PhxInfinityBits.value)
#define PhxNan (PhxNanBits.value)

static int PhxIsNan(double x)
{
	return x != x;
}

static int PhxIsInfinite(double x)
{
	return x == PhxInfinity || x == -PhxInfinity;
}

#pragma function(exp, pow, sinh, cosh, tanh)
double exp(double x)
{
	if (PhxIsNan(x))
		return x;
	if (x > 709.782712893384)
		return PhxInfinity;
	if (x < -745.1332191019412)
		return 0;
	return PhxExpCore(x);
}

double pow(double x, double y)
{
	if (y == 0)
		return 1;
	if (PhxIsNan(x) || PhxIsNan(y))
		return PhxNan;
	if (PhxIsInfinite(y))
	{
		double ax = x < 0 ? -x : x;
		if (ax == 1)
			return 1;
		return (ax > 1) == (y > 0) ? PhxInfinity : 0;
	}
	int integral = y == floor(y);
	int odd = integral && fmod(y, 2) != 0;
	if (x == 0)
	{
		if (y > 0)
			return odd ? x : 0;
		return odd ? 1 / x : PhxInfinity;
	}
	if (PhxIsInfinite(x))
	{
		if (x > 0)
			return y > 0 ? PhxInfinity : 0;
		if (y > 0)
			return odd ? -PhxInfinity : PhxInfinity;
		return odd ? -0.0 : 0.0;
	}
	if (x < 0)
	{
		if (!integral)
			return PhxNan;
		double magnitude = PhxPowCore(-x, y);
		return odd ? -magnitude : magnitude;
	}
	return PhxPowCore(x, y);
}

double sinh(double x)
{
	if (PhxIsNan(x) || PhxIsInfinite(x))
		return x;
	double ax = x < 0 ? -x : x;
	if (ax < 1e-4)
		return x * (1 + x * x / 6);
	if (ax > 710)
		return x > 0 ? PhxInfinity : -PhxInfinity;
	double e = exp(ax);
	double result = (e - 1 / e) / 2;
	return x < 0 ? -result : result;
}

double cosh(double x)
{
	if (PhxIsNan(x))
		return x;
	double ax = x < 0 ? -x : x;
	if (ax > 710)
		return PhxInfinity;
	double e = exp(ax);
	return (e + 1 / e) / 2;
}

double tanh(double x)
{
	if (PhxIsNan(x))
		return x;
	double ax = x < 0 ? -x : x;
	if (ax < 1e-4)
		return x * (1 - x * x / 3);
	if (ax > 20)
		return x > 0 ? 1 : -1;
	double e = exp(2 * ax);
	double result = (e - 1) / (e + 1);
	return x < 0 ? -result : result;
}

double asinh(double x)
{
	if (PhxIsNan(x) || PhxIsInfinite(x))
		return x;
	double ax = x < 0 ? -x : x;
	if (ax < 1e-4)
		return x * (1 - x * x / 6);
	double result = log(ax + sqrt(ax * ax + 1));
	return x < 0 ? -result : result;
}

double acosh(double x)
{
	if (PhxIsNan(x) || x < 1)
		return PhxNan;
	if (PhxIsInfinite(x))
		return x;
	return log(x + sqrt(x * x - 1));
}

double atanh(double x)
{
	if (PhxIsNan(x))
		return x;
	double ax = x < 0 ? -x : x;
	if (ax > 1)
		return PhxNan;
	if (ax == 1)
		return x > 0 ? PhxInfinity : -PhxInfinity;
	if (ax < 1e-4)
		return x * (1 + x * x / 3);
	return 0.5 * log((1 + x) / (1 - x));
}

// The cube root from pow, then one Newton step so perfect cubes come out exact
double cbrt(double x)
{
	if (x == 0 || PhxIsNan(x) || PhxIsInfinite(x))
		return x;
	double ax = x < 0 ? -x : x;
	double r = PhxPowCore(ax, 1.0 / 3);
	r = r - (r * r * r - ax) / (3 * r * r);
	return x < 0 ? -r : r;
}

double modf(double x, double* integral)
{
	if (PhxIsNan(x))
	{
		*integral = x;
		return x;
	}
	if (PhxIsInfinite(x))
	{
		*integral = x;
		return x > 0 ? 0.0 : -0.0;
	}
	*integral = x < 0 ? ceil(x) : floor(x);
	return x - *integral;
}
