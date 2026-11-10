; The Win32 layer's assembly: the syscall stub, the context capture and restore that C can't express, and two instructions C has no intrinsic for here

_TEXT SEGMENT

; long long PhxSyscall(number, a0..a5): Win64 arguments into the kernel's convention (src/Abi/Syscalls.cs)
PhxSyscall PROC
	mov rax, rcx
	mov r10, rdx
	mov rdx, r8
	mov r8, r9
	mov r9, [rsp + 28h]
	syscall
	ret
PhxSyscall ENDP

; void RtlCaptureContext(CONTEXT* context): the caller's registers as of its return, with rip and rsp of the return
RtlCaptureContext PROC
	pushfq
	mov [rcx + 78h], rax
	mov [rcx + 80h], rcx
	mov [rcx + 88h], rdx
	mov [rcx + 90h], rbx
	lea rax, [rsp + 10h]
	mov [rcx + 98h], rax
	mov [rcx + 0A0h], rbp
	mov [rcx + 0A8h], rsi
	mov [rcx + 0B0h], rdi
	mov [rcx + 0B8h], r8
	mov [rcx + 0C0h], r9
	mov [rcx + 0C8h], r10
	mov [rcx + 0D0h], r11
	mov [rcx + 0D8h], r12
	mov [rcx + 0E0h], r13
	mov [rcx + 0E8h], r14
	mov [rcx + 0F0h], r15
	mov rax, [rsp + 8]
	mov [rcx + 0F8h], rax
	mov eax, [rsp]
	mov [rcx + 44h], eax
	mov word ptr [rcx + 38h], cs
	mov word ptr [rcx + 42h], ss
	mov word ptr [rcx + 3Ah], ds
	mov word ptr [rcx + 3Ch], es
	mov word ptr [rcx + 3Eh], fs
	mov word ptr [rcx + 40h], gs
	fxsave64 [rcx + 100h]
	stmxcsr dword ptr [rcx + 34h]
	mov dword ptr [rcx + 30h], 10000Bh
	mov rax, [rcx + 78h]
	popfq
	ret
RtlCaptureContext ENDP

; void RtlRestoreContext(CONTEXT* context, EXCEPTION_RECORD* record): resumes the context through an iretq to ring 3
RtlRestoreContext PROC
	movzx eax, word ptr [rcx + 42h]
	push rax
	push qword ptr [rcx + 98h]
	mov eax, dword ptr [rcx + 44h]
	push rax
	movzx eax, word ptr [rcx + 38h]
	push rax
	push qword ptr [rcx + 0F8h]
	fxrstor64 [rcx + 100h]
	mov rax, [rcx + 78h]
	mov rdx, [rcx + 88h]
	mov rbx, [rcx + 90h]
	mov rbp, [rcx + 0A0h]
	mov rsi, [rcx + 0A8h]
	mov rdi, [rcx + 0B0h]
	mov r8, [rcx + 0B8h]
	mov r9, [rcx + 0C0h]
	mov r10, [rcx + 0C8h]
	mov r11, [rcx + 0D0h]
	mov r12, [rcx + 0D8h]
	mov r13, [rcx + 0E0h]
	mov r14, [rcx + 0E8h]
	mov r15, [rcx + 0F0h]
	mov rcx, [rcx + 80h]
	iretq
RtlRestoreContext ENDP

; double log(double x): the natural logarithm the GC imports, as ln2 * log2(x) on the x87
log PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	fldln2
	fld qword ptr [rsp]
	fyl2x
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
log ENDP

; double sqrt(double x), ceil(double x), floor(double x): CoreLib's Math imports these in unoptimized (Debug) builds; one SSE instruction each
sqrt PROC
	sqrtsd xmm0, xmm0
	ret
sqrt ENDP

ceil PROC
	roundsd xmm0, xmm0, 2
	ret
ceil ENDP

floor PROC
	roundsd xmm0, xmm0, 1
	ret
floor ENDP

; BOOL PhxRandom64(ULONG64* value): one rdrand, FALSE when the hardware had nothing ready
PhxRandom64 PROC
	rdrand rax
	jnc Nothing
	mov [rcx], rax
	mov eax, 1
	ret
Nothing:
	xor eax, eax
	ret
PhxRandom64 ENDP

_TEXT ENDS

END
