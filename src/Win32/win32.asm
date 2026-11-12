; The Win32 layer's assembly: the syscall stub, the context capture and restore that C can't express, two instructions C has no intrinsic for here, and the suspension self-test's spinner and trampoline

EXTERN g_phxTrampolineRegs:QWORD
EXTERN PhxTrampolineResume:PROC

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

; double cos(double x), sin(double x): the JPEG decoder's IDCT tables import these; the x87 does them in one instruction
cos PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	fld qword ptr [rsp]
	fcos
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
cos ENDP

sin PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	fld qword ptr [rsp]
	fsin
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
sin ENDP

; double sqrt(double x), ceil(double x), floor(double x), fmod: CoreLib's Math imports these in unoptimized (Debug) builds; one SSE instruction each
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

; double fmod(double x, double y): x minus the truncated quotient times y, which is exact while the quotient fits a double
fmod PROC
	movapd xmm2, xmm0
	divsd xmm2, xmm1
	roundsd xmm2, xmm2, 3
	mulsd xmm2, xmm1
	subsd xmm0, xmm2
	ret
fmod ENDP

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

; double tan(double x): fptan leaves 1.0 on top of the result
tan PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	fld qword ptr [rsp]
	fptan
	fstp st(0)
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
tan ENDP

; double atan2(double y, double x): fpatan takes st(1)/st(0) with the quadrant from both signs
atan2 PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	movsd qword ptr [rsp+8], xmm1
	fld qword ptr [rsp]
	fld qword ptr [rsp+8]
	fpatan
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
atan2 ENDP

; double atan(double x): atan2(x, 1)
atan PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	fld qword ptr [rsp]
	fld1
	fpatan
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
atan ENDP

; double asin(double x): atan2(x, sqrt(1 - x*x))
asin PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	fld qword ptr [rsp]
	fld st(0)
	fmul st(0), st(0)
	fld1
	fsubrp st(1), st(0)
	fsqrt
	fpatan
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
asin ENDP

; double acos(double x): atan2(sqrt(1 - x*x), x)
acos PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	fld qword ptr [rsp]
	fld st(0)
	fmul st(0), st(0)
	fld1
	fsubrp st(1), st(0)
	fsqrt
	fxch
	fpatan
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
acos ENDP

; double log2(double x) and log10(double x): fyl2x with the matching constant
log2 PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	fld1
	fld qword ptr [rsp]
	fyl2x
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
log2 ENDP

log10 PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	fldlg2
	fld qword ptr [rsp]
	fyl2x
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
log10 ENDP

; 2 to the power on top of the x87 stack, split into an integer part for fscale and a fraction for f2xm1; pops it, leaving the result
Pow2Top MACRO
	fld st(0)
	frndint
	fsub st(1), st(0)
	fxch
	f2xm1
	fld1
	faddp st(1), st(0)
	fscale
	fstp st(1)
ENDM

; double PhxExpCore(double x): e^x as 2^(x*log2e) for finite x in range; crt.c handles the rest
PhxExpCore PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	fld qword ptr [rsp]
	fldl2e
	fmulp st(1), st(0)
	Pow2Top
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
PhxExpCore ENDP

; double PhxPowCore(double x, double y): x^y as 2^(y*log2(x)) for finite positive x, in extended precision throughout
PhxPowCore PROC
	sub rsp, 16
	movsd qword ptr [rsp], xmm0
	movsd qword ptr [rsp+8], xmm1
	fld qword ptr [rsp+8]
	fld qword ptr [rsp]
	fyl2x
	Pow2Top
	fstp qword ptr [rsp]
	movsd xmm0, qword ptr [rsp]
	add rsp, 16
	ret
PhxPowCore ENDP

; void PhxSpinWithRegisters(volatile LONG* stop, volatile ULONG64* counter): every register but rax and rsp holds the pattern selftest.c expects while the loop counts
; PhxSpinLoopAddress and PhxSpinEndAddress bound the loop, so a context read from another thread can be checked
PhxSpinWithRegisters PROC
	push rbx
	push rbp
	push rsi
	push rdi
	push r12
	push r13
	push r14
	push r15
	sub rsp, 192
	movdqu [rsp], xmm6
	movdqu [rsp + 16], xmm7
	movdqu [rsp + 32], xmm8
	movdqu [rsp + 48], xmm9
	movdqu [rsp + 64], xmm10
	movdqu [rsp + 80], xmm11
	movdqu [rsp + 96], xmm12
	movdqu [rsp + 112], xmm13
	movdqu [rsp + 128], xmm14
	movdqu [rsp + 144], xmm15
	mov [rsp + 160], rcx
	mov [rsp + 168], rdx
	mov rax, 0F600000000000000h
	movq xmm6, rax
	mov rax, 0F600000000000001h
	movq xmm7, rax
	mov rax, 0F600000000000002h
	movq xmm8, rax
	mov rax, 0F600000000000003h
	movq xmm9, rax
	mov rax, 0F600000000000004h
	movq xmm10, rax
	mov rax, 0F600000000000005h
	movq xmm11, rax
	mov rax, 0F600000000000006h
	movq xmm12, rax
	mov rax, 0F600000000000007h
	movq xmm13, rax
	mov rax, 0F600000000000008h
	movq xmm14, rax
	mov rax, 0F600000000000009h
	movq xmm15, rax
	mov rcx, 0C0C0C0C0C0C0C0Ch
	mov rdx, 0D0D0D0D0D0D0D0Dh
	mov rbx, 0B0B0B0B0B0B0B0Bh
	mov rbp, 0B9B9B9B9B9B9B9Bh
	mov rsi, 0515151515151515h
	mov rdi, 0D1D1D1D1D1D1D1Dh
	mov r10, 1010101010101010h
	mov r11, 1111111111111111h
	mov r12, 0C12121212121212h
	mov r13, 0C13131313131313h
	mov r14, 0C14141414141414h
	mov r15, 0C15151515151515h
PhxSpinLoop::
	mov r8, 0808080808080808h
	mov r9, 0909090909090909h
	mov rax, [rsp + 168]
	inc qword ptr [rax]
	mov rax, [rsp + 160]
	cmp dword ptr [rax], 0
	je PhxSpinLoop
PhxSpinEnd::
	movdqu xmm6, [rsp]
	movdqu xmm7, [rsp + 16]
	movdqu xmm8, [rsp + 32]
	movdqu xmm9, [rsp + 48]
	movdqu xmm10, [rsp + 64]
	movdqu xmm11, [rsp + 80]
	movdqu xmm12, [rsp + 96]
	movdqu xmm13, [rsp + 112]
	movdqu xmm14, [rsp + 128]
	movdqu xmm15, [rsp + 144]
	add rsp, 192
	pop r15
	pop r14
	pop r13
	pop r12
	pop rdi
	pop rsi
	pop rbp
	pop rbx
	ret
PhxSpinWithRegisters ENDP

PhxSpinLoopAddress PROC
	lea rax, PhxSpinLoop
	ret
PhxSpinLoopAddress ENDP

PhxSpinEndAddress PROC
	lea rax, PhxSpinEnd
	ret
PhxSpinEndAddress ENDP

; Entered by SetThreadContext with only rip changed, as the GC redirects a thread: records every register the thread arrived with, then PhxTrampolineResume restores the saved context
PhxRedirectTrampoline PROC
	push rax
	lea rax, g_phxTrampolineRegs
	mov [rax + 8], rcx
	mov [rax + 16], rdx
	mov [rax + 24], rbx
	lea rcx, [rsp + 8]
	mov [rax + 32], rcx
	mov [rax + 40], rbp
	mov [rax + 48], rsi
	mov [rax + 56], rdi
	mov [rax + 64], r8
	mov [rax + 72], r9
	mov [rax + 80], r10
	mov [rax + 88], r11
	mov [rax + 96], r12
	mov [rax + 104], r13
	mov [rax + 112], r14
	mov [rax + 120], r15
	pop rcx
	mov [rax], rcx
	pushfq
	pop rcx
	mov [rax + 128], rcx
	movdqu [rax + 144], xmm6
	movdqu [rax + 160], xmm7
	movdqu [rax + 176], xmm8
	movdqu [rax + 192], xmm9
	movdqu [rax + 208], xmm10
	movdqu [rax + 224], xmm11
	movdqu [rax + 240], xmm12
	movdqu [rax + 256], xmm13
	movdqu [rax + 272], xmm14
	movdqu [rax + 288], xmm15
	and rsp, -16
	sub rsp, 32
	call PhxTrampolineResume
	int 3
PhxRedirectTrampoline ENDP

_TEXT ENDS

END
