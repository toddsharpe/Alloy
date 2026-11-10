; Early kernel heap: a fixed .bss arena handed out by the boot bump allocator (KernelPlatform.cs)

BOOT_ARENA_SIZE EQU 100000h

_BSS SEGMENT
ALIGN 16
BootArena db BOOT_ARENA_SIZE dup (?)
_BSS ENDS

_TEXT SEGMENT

; uint8_t* PhxBootArena(size_t* size)
PhxBootArena PROC
	mov qword ptr [rcx], BOOT_ARENA_SIZE
	lea rax, BootArena
	ret
PhxBootArena ENDP

_TEXT ENDS

END
