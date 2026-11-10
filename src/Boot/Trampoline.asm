; Hand-off from the loader to the kernel; runs from identity-mapped loader memory, which the new tables also map

_TEXT SEGMENT

; void PhxEnterKernel(uint64_t pageTableRoot, uint64_t stackTop, uint64_t entryPoint, uint64_t bootInfo)
PhxEnterKernel PROC
	cli
	mov cr3, rcx
	mov rsp, rdx
	xor ebp, ebp
	mov rcx, r9
	; Shadow space for the callee; stackTop is 16-aligned, so the call leaves the usual rsp % 16 == 8 at entry
	sub rsp, 20h
	call r8
Hang:
	hlt
	jmp Hang
PhxEnterKernel ENDP

_TEXT ENDS

END
