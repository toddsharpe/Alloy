#include "core_crt/string.h"


//These are compiler intrinsics under /Oi; this file is their implementation, so they must be real functions here
#pragma function(memcmp, strcmp, strlen, strcat, strcpy)

int memcmp(void const* _Buf1, void const* _Buf2, size_t _Size)
{
	uintptr_t* ptr1_64 = (uintptr_t*)_Buf1;
	uintptr_t* ptr2_64 = (uintptr_t*)_Buf2;

	size_t i = 0;
	while (i < _Size / sizeof(uintptr_t))
	{
		if (*ptr1_64 < *ptr2_64)
			return -1;
		else if (*ptr1_64 > * ptr2_64)
			return 1;

		i++;
		ptr1_64++;
		ptr2_64++;
	}

	uint8_t* ptr1_8 = (uint8_t*)ptr1_64;
	uint8_t* ptr2_8 = (uint8_t*)ptr2_64;

	i = 0;
	while (i < _Size % sizeof(uintptr_t))
	{
		if (*ptr1_8 < *ptr2_8)
			return -1;
		else if (*ptr1_8 > * ptr2_8)
			return 1;

		i++;
		ptr1_8++;
		ptr2_8++;
	}

	return 0;
}




int strcmp(const char* str1, const char* str2)
{
	while ((*str1) && (*str1 == *str2))
	{
		str1++;
		str2++;
	}
	return *(unsigned char*)str1 - *(unsigned char*)str2;
}

size_t strlen(const char* str)
{
	size_t length = 0;
	while (*str != '\0')
	{
		length++;
		str++;
	}

	return length;
}

//ctype.h
#define __ascii_toupper(c)   ( (((c) >= 'a') && ((c) <= 'z')) ? ((c) - 'a' + 'A') : (c) )
int stricmp(char const* _String1, char const* _String2)
{
	if (strlen(_String1) != strlen(_String2))
		return -1;

	for (size_t i = 0; i < strlen(_String1); i++) {
		if (__ascii_toupper(_String1[i]) != __ascii_toupper(_String2[i]))
			return _String1[i] - _String2[i];
	}
	return 0;
}

//TODO(tsharpe): Is it possible to tell linker the above function has two names?
int _stricmp(char const* _String1, char const* _String2)
{
	return stricmp(_String1, _String2);
}

int strnicmp(char const* _String1, char const* _String2, size_t _MaxCount)
{
	for (size_t i = 0; i < strlen(_String1) && _MaxCount > 0; i++, _MaxCount--) {
		if (__ascii_toupper(_String1[i]) != __ascii_toupper(_String2[i]))
			return _String1[i] - _String2[i];
	}
	return 0;
}

char* strcat(char* _Destination, char const* _Source)
{
	char* s = _Destination + strlen(_Destination);
	
	while (*_Source != '\0')
		*s++ = *_Source++;
	*s = '\0';

	return _Destination;
}

char* strncat(char* _Destination, char const* _Source, size_t _Count)
{
	char* s = _Destination + strlen(_Destination);

	while (*_Source != '\0' && _Count--)
		*s++ = *_Source++;
	*s = '\0';

	return _Destination;
}

int strncmp(char const* _Str1, char const* _Str2, size_t _MaxCount)
{
	while ((*_Str1) && (*_Str1 == *_Str2) && _MaxCount--)
	{
		_Str1++;
		_Str2++;
	}
	if (_MaxCount)
		return *(unsigned char*)_Str1 - *(unsigned char*)_Str2;
	else
		return 0;
}

size_t strnlen_s(char const* _String, size_t _MaxCount)
{
	if (_String == NULL) {
		return 0;
	}

	//Check the limit before reading, so no byte at or past _MaxCount is touched
	size_t count = 0;

	while (count < _MaxCount && _String[count]) {
		count++;
	}

	return count;
}

char* strncpy(char* _Destination, char const* _Source, size_t _Count)
{
	//Copies up to the source's terminator and pads the rest of _Count with NULs, never reading past the source
	size_t i = 0;
	for (; i < _Count && _Source[i]; i++)
		_Destination[i] = _Source[i];
	for (; i < _Count; i++)
		_Destination[i] = '\0';
	return _Destination;
}

char* strcpy(char* _Destination, char const* _Source)
{
	//+1 so the terminating null is copied too
	return strncpy(_Destination, _Source, strlen(_Source) + 1);
}

char const* strchr(const char* _String, const int _Ch)
{
	const char* s = _String;
	while (*s != (char)_Ch)
		if (!*s++)
			return 0;
	return s;
}

char const* strrchr(char const* const _String, int const _Ch)
{
	const char* s = _String;
	const char* ret = 0;
	do {
		if (*s == (char)_Ch)
			ret = s;
	} while (*s++);
	return ret;
}

const char* strstr(const char* const _String, char const* const _SubString)
{
	size_t n = strlen(_SubString);

	//strncmp never reads past either terminator; the last pass, at the haystack's terminator, only matches an empty substring
	const char* s = _String;
	do
	{
		if (!strncmp(s, _SubString, n))
			return s;
	} while (*s++);

	return 0;
}
