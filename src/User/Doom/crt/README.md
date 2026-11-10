# Doom's C runtime

`core_crt/` and `include/` are copied from MetalOS (`src/core_crt` and the libc headers in `src/user`), the same freestanding C library MetalOS builds doomgeneric against.

Changes from MetalOS:

- `core_crt/string.c` drops `memcpy`, `memmove` and `memset`, which the CoreLib already exports from `src/CoreLib/Runtime.asm`.

`alloy_crt.c` replaces MetalOS-CRT's syscall layer. The variadic and string-parsing functions live in it as C. File I/O, the heap, `exit` and doomgeneric's `DG_*` hooks are C# exports in `../Program.cs`.
