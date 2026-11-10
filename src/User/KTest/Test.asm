; ktest's helpers: instructions that fault on purpose, a spin loop, and a CONTEXT restore as RtlRestoreContext does it

_TEXT SEGMENT

; void PhxTestBreak(): a breakpoint, then the return the dispatcher resumes at
PhxTestBreak PROC
	int 3
	ret
PhxTestBreak ENDP

PhxTestBreakAddress PROC
	lea rax, PhxTestBreak
	ret
PhxTestBreakAddress ENDP

; void PhxTestWrite(int* address): a store that faults on a bad address
PhxTestWrite PROC
	mov dword ptr [rcx], 1
	ret
PhxTestWrite ENDP

; uint PhxTestDivide(uint divisor): 100 / divisor
PhxTestDivide PROC
	mov eax, 100
	xor edx, edx
	div ecx
	ret
PhxTestDivide ENDP

; A thread entry that increments [rcx] forever; only a redirected context gets it out
PhxTestSpin PROC
Again:
	lock inc qword ptr [rcx]
	jmp Again
PhxTestSpin ENDP

PhxTestSpinAddress PROC
	lea rax, PhxTestSpin
	ret
PhxTestSpinAddress ENDP

; void PhxTestRestore(CONTEXT* context): resumes a Windows CONTEXT through an iretq to ring 3
PhxTestRestore PROC
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
PhxTestRestore ENDP

_TEXT ENDS

END
