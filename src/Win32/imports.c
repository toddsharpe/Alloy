// The import address table Windows would have filled: a __imp_ pointer for every function, and the same list as GetProcAddress's name table
#include "win32.h"

#define PHX_IMPORT(name) const void* __imp_##name = (const void*)&name;
PHX_WIN32_FUNCTIONS(PHX_IMPORT)

typedef struct { const char* Name; const void* Address; } PhxExport;

#define PHX_EXPORT(name) { #name, (const void*)&name },
static const PhxExport s_exports[] = { PHX_WIN32_FUNCTIONS(PHX_EXPORT) };

PVOID PhxFindExport(const char* name)
{
	for (size_t i = 0; i < sizeof(s_exports) / sizeof(s_exports[0]); i++)
	{
		if (strcmp(s_exports[i].Name, name) == 0)
			return (PVOID)s_exports[i].Address;
	}
	return NULL;
}
