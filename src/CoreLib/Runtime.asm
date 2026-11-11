; Native CoreLib pieces every image links (Win64 ABI: rcx, rdx, r8, r9; rdi/rsi are callee-saved)

; Every ILC object puts a pointer to its ReadyToRun header in .modules$I; the linker sorts $A < $I < $Z
modsA SEGMENT READONLY ALIGN(8) ALIAS(".modules$A") 'DATA'
__modules_a dq 0
modsA ENDS

modsZ SEGMENT READONLY ALIGN(8) ALIAS(".modules$Z") 'DATA'
__modules_z dq 0
modsZ ENDS

_DATA SEGMENT
; GS cookie the compiler checks in frames with stackalloc or fixed buffers
PUBLIC __security_cookie
__security_cookie dq 00002B992DDFA232h

; Non-zero asks managed code to call RhpGcPoll at safepoints; nothing ever sets it
PUBLIC RhpTrapThreads
RhpTrapThreads dd 0
_DATA ENDS

_TEXT SEGMENT

; void** PhxModulesStart() / PhxModulesEnd()
PhxModulesStart PROC
	lea rax, __modules_a
	ret
PhxModulesStart ENDP

PhxModulesEnd PROC
	lea rax, __modules_z
	add rax, 8
	ret
PhxModulesEnd ENDP

; void RhpAssignRef(void** dst, void* ref): a plain store until there is a GC that needs card marking
RhpAssignRef PROC
	mov [rcx], rdx
	ret
RhpAssignRef ENDP

RhpCheckedAssignRef PROC
	mov [rcx], rdx
	ret
RhpCheckedAssignRef ENDP

; Custom convention: copies [rsi] to [rdi] and advances both by 8; may only trash rax, rcx, rsi, rdi
RhpByRefAssignRef PROC
	mov rcx, [rsi]
	mov [rdi], rcx
	add rdi, 8
	add rsi, 8
	ret
RhpByRefAssignRef ENDP

; Touches each page down to r11 (lowest address of the new frame) so a guard page is never skipped; trashes rax
RhpStackProbe PROC
	mov rax, rsp
	and rax, -1000h
ProbeLoop:
	sub rax, 1000h
	test dword ptr [rax], eax
	cmp rax, r11
	jg ProbeLoop
	ret
RhpStackProbe ENDP

RhDebugBreak PROC
	int 3
	ret
RhDebugBreak ENDP

; void* memset(void* dst, int value, size_t count)
memset PROC
	mov r9, rdi
	mov r10, rcx
	mov rdi, rcx
	movzx eax, dl
	mov rcx, r8
	rep stosb
	mov rdi, r9
	mov rax, r10
	ret
memset ENDP

; void* memmove(void* dst, const void* src, size_t count); overlap-safe
memmove PROC
	mov r9, rdi
	mov r10, rsi
	mov rax, rcx
	mov rdi, rcx
	mov rsi, rdx
	mov rcx, r8
	cmp rdi, rsi
	jbe Forward
	lea rdx, [rsi + rcx]
	cmp rdi, rdx
	jae Forward
	; Destination overlaps the tail of the source: copy backwards
	lea rsi, [rsi + rcx - 1]
	lea rdi, [rdi + rcx - 1]
	std
	rep movsb
	cld
	jmp Done
Forward:
	rep movsb
Done:
	mov rdi, r9
	mov rsi, r10
	ret
memmove ENDP

; void* memcpy(void* dst, const void* src, size_t count); C code may call this directly
memcpy PROC
	jmp memmove
memcpy ENDP

; Interface dispatch for calls ILC couldn't devirtualize: r11 holds the call site's cell (stub pointer, then a cache or the interface), rcx the object
; The cache object (System.Runtime.InterfaceDispatch.Cache) keeps (MethodTable, target) pairs at 24 + 16n; misses resolve in managed code
EXTERN PhxResolveInterfaceDispatch : PROC

RhpInitialInterfaceDispatch PROC
	mov r10, [r11 + 8]
	test r10b, 3
	jnz Resolve
	cmp r10, 1000h
	jb Resolve
	mov rax, [rcx]
	cmp rax, [r10 + 24]
	jne Entry1
	jmp qword ptr [r10 + 32]
Entry1:
	cmp rax, [r10 + 40]
	jne Entry2
	jmp qword ptr [r10 + 48]
Entry2:
	cmp rax, [r10 + 56]
	jne Entry3
	jmp qword ptr [r10 + 64]
Entry3:
	cmp rax, [r10 + 72]
	jne Resolve
	jmp qword ptr [r10 + 80]
Resolve:
	; Keep the call's arguments across the managed resolver, then tail-jump to the implementation it found
	sub rsp, 88h
	mov [rsp + 20h], rcx
	mov [rsp + 28h], rdx
	mov [rsp + 30h], r8
	mov [rsp + 38h], r9
	movdqa xmmword ptr [rsp + 40h], xmm0
	movdqa xmmword ptr [rsp + 50h], xmm1
	movdqa xmmword ptr [rsp + 60h], xmm2
	movdqa xmmword ptr [rsp + 70h], xmm3
	mov rdx, r11
	call PhxResolveInterfaceDispatch
	mov rcx, [rsp + 20h]
	mov rdx, [rsp + 28h]
	mov r8, [rsp + 30h]
	mov r9, [rsp + 38h]
	movdqa xmm0, xmmword ptr [rsp + 40h]
	movdqa xmm1, xmmword ptr [rsp + 50h]
	movdqa xmm2, xmmword ptr [rsp + 60h]
	movdqa xmm3, xmmword ptr [rsp + 70h]
	add rsp, 88h
	jmp rax
RhpInitialInterfaceDispatch ENDP

RhpInitialDynamicInterfaceDispatch PROC
	jmp RhpInitialInterfaceDispatch
RhpInitialDynamicInterfaceDispatch ENDP

_TEXT ENDS

END
