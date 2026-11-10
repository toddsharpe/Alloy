; Kernel entry and exit paths that C# can't express: descriptor tables, interrupt stubs, context capture, fault probes

EXTERN KiInterruptDispatch:PROC
EXTERN KiSystemCall:PROC

ISTACK_SIZE EQU 2000h

_BSS SEGMENT
ALIGN 16
KiGdt db 64 dup (?)
ALIGN 16
KiTss db 112 dup (?)
ALIGN 16
KiIdt db 4096 dup (?)
ALIGN 16
KiIstStacks db 4 * ISTACK_SIZE dup (?)
_BSS ENDS

; Interrupts with a CPU error code push only the vector; the rest push a zero first so every frame has the same shape
ISR_NOERR MACRO num
KiIsr&num PROC
	push 0
	push num
	jmp KiInterruptCommon
KiIsr&num ENDP
ENDM

ISR_ERR MACRO num
KiIsr&num PROC
	push num
	jmp KiInterruptCommon
KiIsr&num ENDP
ENDM

ISR_ADDRESS MACRO num
	dq KiIsr&num
ENDM

_TEXT SEGMENT

vec = 0
REPT 256
	IF (vec EQ 8) OR ((vec GE 10) AND (vec LE 14)) OR (vec EQ 17) OR (vec EQ 21) OR (vec EQ 29) OR (vec EQ 30)
		ISR_ERR %vec
	ELSE
		ISR_NOERR %vec
	ENDIF
	vec = vec + 1
ENDM

SAVE_FRAME MACRO
	push rax
	push rcx
	push rdx
	push rbx
	push rbp
	push rsi
	push rdi
	push r8
	push r9
	push r10
	push r11
	push r12
	push r13
	push r14
	push r15
	sub rsp, 512
	fxsave64 [rsp]
	cld
ENDM

; Builds the InterruptFrame (Interrupts.cs): GPRs, then the whole fxsave area; from ring 3 it is the thread's user context
KiInterruptCommon PROC
	test byte ptr [rsp + 24], 3
	jz FromKernel
	swapgs
FromKernel:
	SAVE_FRAME
	mov rcx, rsp
	sub rsp, 32
	call KiInterruptDispatch
	add rsp, 32
KiRestoreFrame::
	fxrstor64 [rsp]
	add rsp, 512
	pop r15
	pop r14
	pop r13
	pop r12
	pop r11
	pop r10
	pop r9
	pop r8
	pop rdi
	pop rsi
	pop rbp
	pop rbx
	pop rdx
	pop rcx
	pop rax
	test byte ptr [rsp + 24], 3
	jz ToKernel
	swapgs
ToKernel:
	add rsp, 16
	iretq
KiInterruptCommon ENDP

PhxGdt PROC
	lea rax, KiGdt
	ret
PhxGdt ENDP

PhxTss PROC
	lea rax, KiTss
	ret
PhxTss ENDP

PhxIdt PROC
	lea rax, KiIdt
	ret
PhxIdt ENDP

; uint8_t* PhxIstStacks(size_t* stackSize)
PhxIstStacks PROC
	mov qword ptr [rcx], ISTACK_SIZE
	lea rax, KiIstStacks
	ret
PhxIstStacks ENDP

PhxIsrTable PROC
	lea rax, KiIsrTable
	ret
PhxIsrTable ENDP

; void PhxLoadGdt(void* gdtr): reloads every segment register, using a far return to refresh cs
PhxLoadGdt PROC
	lgdt fword ptr [rcx]
	mov ax, 10h
	mov ds, ax
	mov es, ax
	mov ss, ax
	pop rdx
	push 08h
	push rdx
	db 48h, 0CBh
PhxLoadGdt ENDP

PhxLoadTss PROC
	ltr cx
	ret
PhxLoadTss ENDP

PhxLoadIdt PROC
	lidt fword ptr [rcx]
	ret
PhxLoadIdt ENDP

; Enables interrupts and halts in one step: sti holds off interrupts for one instruction, so a wakeup can't slip in between
PhxHalt PROC
	sti
	hlt
	ret
PhxHalt ENDP

PhxBreakpoint PROC
	int 3
	ret
PhxBreakpoint ENDP

; void PhxCaptureContext(uint64_t* context): rip/rsp of the caller after return, then rbx rbp rsi rdi r12-r15
PhxCaptureContext PROC
	mov rax, [rsp]
	mov [rcx], rax
	lea rax, [rsp + 8]
	mov [rcx + 8], rax
	mov [rcx + 16], rbx
	mov [rcx + 24], rbp
	mov [rcx + 32], rsi
	mov [rcx + 40], rdi
	mov [rcx + 48], r12
	mov [rcx + 56], r13
	mov [rcx + 64], r14
	mov [rcx + 72], r15
	ret
PhxCaptureContext ENDP

; void PhxSwitchContext(uint64_t* saveRsp, uint64_t loadRsp): saves the Win64 callee-saved state, then resumes the other stack
PhxSwitchContext PROC
	push rbx
	push rbp
	push rdi
	push rsi
	push r12
	push r13
	push r14
	push r15
	sub rsp, 168
	movdqu xmmword ptr [rsp], xmm6
	movdqu xmmword ptr [rsp + 16], xmm7
	movdqu xmmword ptr [rsp + 32], xmm8
	movdqu xmmword ptr [rsp + 48], xmm9
	movdqu xmmword ptr [rsp + 64], xmm10
	movdqu xmmword ptr [rsp + 80], xmm11
	movdqu xmmword ptr [rsp + 96], xmm12
	movdqu xmmword ptr [rsp + 112], xmm13
	movdqu xmmword ptr [rsp + 128], xmm14
	movdqu xmmword ptr [rsp + 144], xmm15
	mov [rcx], rsp
	mov rsp, rdx
	movdqu xmm6, xmmword ptr [rsp]
	movdqu xmm7, xmmword ptr [rsp + 16]
	movdqu xmm8, xmmword ptr [rsp + 32]
	movdqu xmm9, xmmword ptr [rsp + 48]
	movdqu xmm10, xmmword ptr [rsp + 64]
	movdqu xmm11, xmmword ptr [rsp + 80]
	movdqu xmm12, xmmword ptr [rsp + 96]
	movdqu xmm13, xmmword ptr [rsp + 112]
	movdqu xmm14, xmmword ptr [rsp + 128]
	movdqu xmm15, xmmword ptr [rsp + 144]
	add rsp, 168
	pop r15
	pop r14
	pop r13
	pop r12
	pop rsi
	pop rdi
	pop rbp
	pop rbx
	ret
PhxSwitchContext ENDP

; A new thread's first return target (KThread.cs): enables interrupts the switch left off, then calls r13(r12), which never returns
PhxThreadStart PROC
	sti
	mov rcx, r12
	sub rsp, 40
	call r13
	ud2
PhxThreadStart ENDP

PhxThreadStartAddress PROC
	lea rax, PhxThreadStart
	ret
PhxThreadStartAddress ENDP

; uint64_t PhxHypercall(uint64_t control, uint64_t input, uint64_t output, void* page): the TLFS hypercall convention
PhxHypercall PROC
	jmp r9
PhxHypercall ENDP

; syscall from ring 3, saved as the same InterruptFrame an interrupt makes (vector 100h); gs is the CPU block: [8] kernel stack top, [16] scratch
PhxSyscallEntry PROC
	swapgs
	mov gs:[16], rsp
	mov rsp, gs:[8]
	push 23h
	push qword ptr gs:[16]
	push r11
	push 2Bh
	push rcx
	push 0
	push 100h
	SAVE_FRAME
	mov rcx, rsp
	sub rsp, 32
	sti
	call KiSystemCall
	cli
	add rsp, 32
	jmp KiRestoreFrame
PhxSyscallEntry ENDP

PhxSyscallEntryAddress PROC
	lea rax, PhxSyscallEntry
	ret
PhxSyscallEntryAddress ENDP

; void PhxResumeUser(InterruptFrame* frame): enters ring 3 from a frame built at the top of the thread's kernel stack
PhxResumeUser PROC
	cli
	mov rsp, rcx
	jmp KiRestoreFrame
PhxResumeUser ENDP

; int PhxCopyUser(void* destination, const void* source, size_t count): copy with SMAP opened (stac/clac); 0 if it faulted
PhxCopyUser PROC
	mov r9, rdi
	mov r10, rsi
	mov rdi, rcx
	mov rsi, rdx
	mov rcx, r8
	db 0Fh, 01h, 0CBh
CopyUserFault::
	rep movsb
	db 0Fh, 01h, 0CAh
	mov rdi, r9
	mov rsi, r10
	mov eax, 1
	ret
CopyUserResume::
	db 0Fh, 01h, 0CAh
	mov rdi, r9
	mov rsi, r10
	xor eax, eax
	ret
PhxCopyUser ENDP

; int PhxProbeRead(const uint8_t* address, uint8_t* value): 1 and the byte, or 0 if the read faulted
PhxProbeRead PROC
ProbeReadFault::
	mov al, byte ptr [rcx]
	mov byte ptr [rdx], al
	mov eax, 1
	ret
ProbeReadResume::
	xor eax, eax
	ret
PhxProbeRead ENDP

; const uint64_t* PhxFixupTable(size_t* count): (faulting rip, resume rip) pairs the page fault handler honors
PhxFixupTable PROC
	mov qword ptr [rcx], (KiFixupTableEnd - KiFixupTable) / 16
	lea rax, KiFixupTable
	ret
PhxFixupTable ENDP

_TEXT ENDS

_DATA SEGMENT
ALIGN 8
KiIsrTable LABEL QWORD
vec = 0
REPT 256
	ISR_ADDRESS %vec
	vec = vec + 1
ENDM

ALIGN 8
KiFixupTable LABEL QWORD
	dq ProbeReadFault, ProbeReadResume
	dq CopyUserFault, CopyUserResume
KiFixupTableEnd LABEL QWORD
_DATA ENDS

END
