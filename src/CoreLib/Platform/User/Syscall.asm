; long PhxSyscall(number, a0..a5): Win64 args into the kernel's convention (Abi/Syscalls.cs); a4/a5 stay at [rsp+0x30]/[rsp+0x38]

_TEXT SEGMENT

PhxSyscall PROC
	mov rax, rcx
	mov r10, rdx
	mov rdx, r8
	mov r8, r9
	mov r9, [rsp + 28h]
	syscall
	ret
PhxSyscall ENDP

; void* PhxTeb(): this thread's TEB, which the kernel's swapgs leaves behind gs (NtTib.Self at 0x30)
PhxTeb PROC
	mov rax, gs:[30h]
	ret
PhxTeb ENDP

; void PhxDebugBreak(): a breakpoint trap, which ends a user process
PhxDebugBreak PROC
	int 3
	ret
PhxDebugBreak ENDP

_TEXT ENDS

END
