; Privileged x64 instructions with no C# equivalent, for the loader and kernel. Win64 ABI: rcx, rdx, r8, r9.

_TEXT SEGMENT

; uint8_t PhxInByte(uint16_t port)
PhxInByte PROC
	mov edx, ecx
	in al, dx
	movzx eax, al
	ret
PhxInByte ENDP

; void PhxOutByte(uint16_t port, uint8_t value)
PhxOutByte PROC
	mov eax, edx
	mov edx, ecx
	out dx, al
	ret
PhxOutByte ENDP

PhxInWord PROC
	mov edx, ecx
	in ax, dx
	movzx eax, ax
	ret
PhxInWord ENDP

PhxOutWord PROC
	mov eax, edx
	mov edx, ecx
	out dx, ax
	ret
PhxOutWord ENDP

PhxInDword PROC
	mov edx, ecx
	in eax, dx
	ret
PhxInDword ENDP

PhxOutDword PROC
	mov eax, edx
	mov edx, ecx
	out dx, eax
	ret
PhxOutDword ENDP

; uint64_t PhxReadMsr(uint32_t msr)
PhxReadMsr PROC
	rdmsr
	shl rdx, 32
	or rax, rdx
	ret
PhxReadMsr ENDP

; void PhxWriteMsr(uint32_t msr, uint64_t value)
PhxWriteMsr PROC
	mov rax, rdx
	shr rdx, 32
	wrmsr
	ret
PhxWriteMsr ENDP

PhxReadCr0 PROC
	mov rax, cr0
	ret
PhxReadCr0 ENDP

PhxReadCr2 PROC
	mov rax, cr2
	ret
PhxReadCr2 ENDP

PhxReadCr3 PROC
	mov rax, cr3
	ret
PhxReadCr3 ENDP

PhxReadCr4 PROC
	mov rax, cr4
	ret
PhxReadCr4 ENDP

PhxWriteCr0 PROC
	mov cr0, rcx
	ret
PhxWriteCr0 ENDP

PhxWriteCr3 PROC
	mov cr3, rcx
	ret
PhxWriteCr3 ENDP

PhxWriteCr4 PROC
	mov cr4, rcx
	ret
PhxWriteCr4 ENDP

PhxReadFlags PROC
	pushfq
	pop rax
	ret
PhxReadFlags ENDP

PhxDisableInterrupts PROC
	cli
	ret
PhxDisableInterrupts ENDP

PhxEnableInterrupts PROC
	sti
	ret
PhxEnableInterrupts ENDP

PhxPause PROC
	pause
	ret
PhxPause ENDP

PhxInvalidatePage PROC
	invlpg [rcx]
	ret
PhxInvalidatePage ENDP

; uint64_t PhxReadTsc()
PhxReadTsc PROC
	rdtsc
	shl rdx, 32
	or rax, rdx
	ret
PhxReadTsc ENDP

; void PhxCpuId(uint32_t leaf, uint32_t subleaf, uint32_t* result /* eax, ebx, ecx, edx */)
PhxCpuId PROC
	mov r9, rbx
	mov r10, r8
	mov eax, ecx
	mov ecx, edx
	cpuid
	mov [r10], eax
	mov [r10 + 4], ebx
	mov [r10 + 8], ecx
	mov [r10 + 12], edx
	mov rbx, r9
	ret
PhxCpuId ENDP

; Stops this processor for good: interrupts off, then halt (looping in case an NMI wakes it)
PhxHaltForever PROC
	cli
HaltLoop:
	hlt
	jmp HaltLoop
PhxHaltForever ENDP

_TEXT ENDS

END
