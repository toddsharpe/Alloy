#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <errno.h>

// MSVC references this from any code that uses floating point
int _fltused = 0;

// errno is (*_errno()) in MetalOS's errno.h; Doom is single-threaded, so one shared slot is enough
int* _errno(void)
{
	static int value;
	return &value;
}

// Implemented in C# (Doom/Program.cs): line-buffered output to the kernel log
void PhxWrite(const char* text, size_t length);

extern unsigned int* DG_ScreenBuffer;

unsigned int* PhxDoomScreen(void)
{
	return DG_ScreenBuffer;
}

int vfprintf(FILE* const stream, char const* const format, va_list args)
{
	char buffer[1024];
	int length = vsnprintf(buffer, sizeof(buffer), format, args);
	if (length > 0)
		PhxWrite(buffer, length < (int)sizeof(buffer) ? (size_t)length : sizeof(buffer) - 1);
	return length;
}

int fprintf(FILE* const stream, char const* const format, ...)
{
	va_list args;
	va_start(args, format);
	int length = vfprintf(stream, format, args);
	va_end(args);
	return length;
}

int printf(char const* const format, ...)
{
	va_list args;
	va_start(args, format);
	int length = vfprintf(stdout, format, args);
	va_end(args);
	return length;
}

int puts(const char* text)
{
	PhxWrite(text, strlen(text));
	PhxWrite("\n", 1);
	return 0;
}

int putchar(int character)
{
	char c = (char)character;
	PhxWrite(&c, 1);
	return character;
}

char* strdup(char const* text)
{
	size_t length = strlen(text) + 1;
	char* copy = (char*)malloc(length);
	if (copy)
		memcpy(copy, text, length);
	return copy;
}

void* calloc(size_t count, size_t size)
{
	if (size != 0 && count > (size_t)-1 / size)
		return 0;
	void* block = malloc(count * size);
	if (block)
		memset(block, 0, count * size);
	return block;
}

double atof(const char* text)
{
	double sign = 1.0;
	double value = 0.0;
	double scale = 0.1;
	while (isspace(*text))
		text++;
	if (*text == '-' || *text == '+')
		sign = *text++ == '-' ? -1.0 : 1.0;
	while (isdigit(*text))
		value = value * 10.0 + (*text++ - '0');
	if (*text == '.')
	{
		text++;
		while (isdigit(*text))
		{
			value += (*text++ - '0') * scale;
			scale /= 10.0;
		}
	}
	return sign * value;
}

// Covers what Doom's config and argument parsing use: %d %i %u %x %s %c, with field widths ignored
int vsscanf(const char* text, const char* format, va_list args)
{
	int assigned = 0;
	while (*format)
	{
		if (isspace(*format))
		{
			while (isspace(*text))
				text++;
			format++;
			continue;
		}
		if (*format != '%')
		{
			if (*text++ != *format++)
				return assigned;
			continue;
		}

		format++;
		while (isdigit(*format))
			format++;
		char kind = *format++;
		if (kind != 'c')
		{
			while (isspace(*text))
				text++;
		}
		if (*text == 0)
			return assigned ? assigned : -1;

		if (kind == 'd' || kind == 'i' || kind == 'u' || kind == 'x')
		{
			int negative = 0;
			int base = kind == 'x' ? 16 : 10;
			long value = 0;
			if (*text == '-' || *text == '+')
				negative = *text++ == '-';
			if ((kind == 'i' || kind == 'x') && text[0] == '0' && (text[1] == 'x' || text[1] == 'X'))
			{
				base = 16;
				text += 2;
			}
			const char* start = text;
			for (;; text++)
			{
				int digit = isdigit(*text) ? *text - '0' : (base == 16 && isxdigit(*text)) ? (tolower(*text) - 'a' + 10) : -1;
				if (digit < 0)
					break;
				value = value * base + digit;
			}
			if (text == start)
				return assigned;
			*va_arg(args, int*) = (int)(negative ? -value : value);
			assigned++;
		}
		else if (kind == 's')
		{
			char* out = va_arg(args, char*);
			while (*text && !isspace(*text))
				*out++ = *text++;
			*out = 0;
			assigned++;
		}
		else if (kind == 'c')
		{
			*va_arg(args, char*) = *text++;
			assigned++;
		}
		else
		{
			return assigned;
		}
	}
	return assigned;
}

int sscanf(const char* text, const char* format, ...)
{
	va_list args;
	va_start(args, format);
	int assigned = vsscanf(text, format, args);
	va_end(args);
	return assigned;
}

// MetalOS-RT's debug print, which the MetalOS.Doom fork calls directly
void DebugPrintf(const char* format, ...)
{
	va_list args;
	va_start(args, format);
	vfprintf(stdout, format, args);
	va_end(args);
}
