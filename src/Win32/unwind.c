// RtlVirtualUnwind: the x64 unwind codes (UNWIND_INFO), chained entries, machine frames and epilog detection, as the runtime's stack walker and gs failure reporting expect
#include "win32.h"

#define UNW_FLAG_CHAININFO 4
#define UWOP_PUSH_NONVOL 0
#define UWOP_ALLOC_LARGE 1
#define UWOP_ALLOC_SMALL 2
#define UWOP_SET_FPREG 3
#define UWOP_SAVE_NONVOL 4
#define UWOP_SAVE_NONVOL_FAR 5
#define UWOP_EPILOG 6
#define UWOP_SPARE_CODE 7
#define UWOP_SAVE_XMM128 8
#define UWOP_SAVE_XMM128_FAR 9
#define UWOP_PUSH_MACHFRAME 10

typedef struct
{
	const BYTE* Info;
	BYTE Flags;
	BYTE PrologSize;
	BYTE CodeCount;
	BYTE FrameRegister;
	BYTE FrameOffset;
	const WORD* Codes;
} PhxUnwindInfo;

static void PhxParseInfo(DWORD64 imageBase, const RUNTIME_FUNCTION* function, PhxUnwindInfo* parsed)
{
	const BYTE* info = (const BYTE*)(imageBase + function->UnwindData);
	parsed->Info = info;
	parsed->Flags = info[0] >> 3;
	parsed->PrologSize = info[1];
	parsed->CodeCount = info[2];
	parsed->FrameRegister = info[3] & 0xF;
	parsed->FrameOffset = info[3] >> 4;
	parsed->Codes = (const WORD*)(info + 4);
}

// What follows the codes: the chained entry, or the handler RVA and its data
static const BYTE* PhxAfterCodes(const PhxUnwindInfo* info)
{
	return info->Info + 4 + ((info->CodeCount + 1) & ~1) * 2;
}

static DWORD64* PhxRegister(CONTEXT* context, int number)
{
	return &context->Rax + number;
}

static int PhxSlots(BYTE op, BYTE opInfo)
{
	switch (op)
	{
		case UWOP_ALLOC_LARGE:
			return opInfo == 0 ? 2 : 3;
		case UWOP_SAVE_NONVOL:
		case UWOP_SAVE_XMM128:
		case UWOP_EPILOG:
			return 2;
		case UWOP_SAVE_NONVOL_FAR:
		case UWOP_SAVE_XMM128_FAR:
		case UWOP_SPARE_CODE:
			return 3;
		default:
			return 1;
	}
}

static BOOL PhxIsNonvolatile(int reg)
{
	return reg == 3 || reg == 5 || reg == 6 || reg == 7 || reg >= 12;
}

// If the instruction at rip begins an epilog (stack release, pops of nonvolatile registers, then a return or tail jump), runs the rest of it on the context
static BOOL PhxTryEpilog(CONTEXT* context, DWORD64 begin, DWORD64 end, int frameRegister)
{
	const BYTE* p = (const BYTE*)context->Rip;
	DWORD64 rsp = context->Rsp;
	DWORD64 registers[16];
	memcpy(registers, &context->Rax, sizeof(registers));

	if (p[0] == 0x48 && p[1] == 0x83 && p[2] == 0xC4)
	{
		rsp += (signed char)p[3];
		p += 4;
	}
	else if (p[0] == 0x48 && p[1] == 0x81 && p[2] == 0xC4)
	{
		rsp += *(const int*)(p + 3);
		p += 7;
	}
	else if ((p[0] & 0xFE) == 0x48 && p[1] == 0x8D && (p[2] & 0x38) == 0x20 && ((p[2] >> 6) == 1 || (p[2] >> 6) == 2))
	{
		int reg = (p[2] & 7) | ((p[0] & 1) << 3);
		if (frameRegister == 0 || reg != frameRegister)
			return FALSE;
		const BYTE* q = p + 3;
		if ((p[2] & 7) == 4)
			q++;
		int displacement = (p[2] >> 6) == 1 ? (signed char)*q : *(const int*)q;
		q += (p[2] >> 6) == 1 ? 1 : 4;
		rsp = registers[reg] + (DWORD64)(LONG64)displacement;
		p = q;
	}

	for (;;)
	{
		int reg;
		if (p[0] >= 0x58 && p[0] <= 0x5F)
		{
			reg = p[0] - 0x58;
			p++;
		}
		else if (p[0] == 0x41 && p[1] >= 0x58 && p[1] <= 0x5F)
		{
			reg = 8 + p[1] - 0x58;
			p += 2;
		}
		else
		{
			break;
		}
		if (!PhxIsNonvolatile(reg))
			return FALSE;
		registers[reg] = *(const DWORD64*)rsp;
		rsp += 8;
	}

	DWORD64 returnAdjust = 8;
	if (p[0] == 0xC3)
	{
	}
	else if (p[0] == 0xC2)
	{
		returnAdjust += *(const WORD*)(p + 1);
	}
	else if (p[0] == 0xE9)
	{
		DWORD64 target = (DWORD64)(p + 5) + (DWORD64)(LONG64)*(const int*)(p + 1);
		if (target >= begin && target < end)
			return FALSE;
	}
	else if (p[0] == 0xEB)
	{
		DWORD64 target = (DWORD64)(p + 2) + (DWORD64)(LONG64)(signed char)p[1];
		if (target >= begin && target < end)
			return FALSE;
	}
	else if ((p[0] == 0xFF && (p[1] & 0xF8) == 0xE0) || (p[0] == 0x41 && p[1] == 0xFF && (p[2] & 0xF8) == 0xE0))
	{
	}
	else if ((p[0] == 0xFF && p[1] == 0x25) || (p[0] == 0x48 && p[1] == 0xFF && p[2] == 0x25))
	{
	}
	else
	{
		return FALSE;
	}

	memcpy(&context->Rax, registers, sizeof(registers));
	context->Rip = *(const DWORD64*)rsp;
	context->Rsp = rsp + returnAdjust;
	return TRUE;
}

PEXCEPTION_ROUTINE RtlVirtualUnwind(DWORD handlerType, DWORD64 imageBase, DWORD64 pc, RUNTIME_FUNCTION* function, CONTEXT* context, PVOID* handlerData, DWORD64* establisherFrame, KNONVOLATILE_CONTEXT_POINTERS* pointers)
{
	PhxUnwindInfo info;
	PhxParseInfo(imageBase, function, &info);
	DWORD64 functionStart = imageBase + function->BeginAddress;
	DWORD64 prologOffset = pc - functionStart;
	BOOL inBody = prologOffset >= info.PrologSize;

	// The frame pointer only stands once the prolog has set it; before that the frame is the stack pointer
	BOOL established = inBody;
	if (!established && info.FrameRegister != 0)
	{
		for (int i = 0; i < info.CodeCount;)
		{
			BYTE codeOffset = info.Codes[i] & 0xFF;
			BYTE op = (info.Codes[i] >> 8) & 0xF;
			BYTE opInfo = info.Codes[i] >> 12;
			if (op == UWOP_SET_FPREG && codeOffset <= prologOffset)
				established = TRUE;
			i += PhxSlots(op, opInfo);
		}
	}
	DWORD64 frame = info.FrameRegister != 0 && established ? *PhxRegister(context, info.FrameRegister) - (DWORD64)info.FrameOffset * 16 : context->Rsp;
	*establisherFrame = frame;

	if (inBody && PhxTryEpilog(context, functionStart, imageBase + function->EndAddress, info.FrameRegister))
		return NULL;

	BOOL machineFrame = FALSE;
	for (;;)
	{
		for (int i = 0; i < info.CodeCount;)
		{
			BYTE codeOffset = info.Codes[i] & 0xFF;
			BYTE op = (info.Codes[i] >> 8) & 0xF;
			BYTE opInfo = info.Codes[i] >> 12;
			int slots = PhxSlots(op, opInfo);
			if (codeOffset > prologOffset)
			{
				i += slots;
				continue;
			}
			switch (op)
			{
				case UWOP_PUSH_NONVOL:
					*PhxRegister(context, opInfo) = *(DWORD64*)context->Rsp;
					if (pointers != NULL)
						pointers->IntegerContext[opInfo] = (DWORD64*)context->Rsp;
					context->Rsp += 8;
					break;
				case UWOP_ALLOC_LARGE:
					context->Rsp += opInfo == 0 ? (DWORD64)info.Codes[i + 1] * 8 : (DWORD64)info.Codes[i + 1] | ((DWORD64)info.Codes[i + 2] << 16);
					break;
				case UWOP_ALLOC_SMALL:
					context->Rsp += (DWORD64)opInfo * 8 + 8;
					break;
				case UWOP_SET_FPREG:
					context->Rsp = *PhxRegister(context, info.FrameRegister) - (DWORD64)info.FrameOffset * 16;
					break;
				case UWOP_SAVE_NONVOL:
				case UWOP_SAVE_NONVOL_FAR:
				{
					DWORD64 offset = op == UWOP_SAVE_NONVOL ? (DWORD64)info.Codes[i + 1] * 8 : (DWORD64)info.Codes[i + 1] | ((DWORD64)info.Codes[i + 2] << 16);
					DWORD64* slot = (DWORD64*)(frame + offset);
					*PhxRegister(context, opInfo) = *slot;
					if (pointers != NULL)
						pointers->IntegerContext[opInfo] = slot;
					break;
				}
				case UWOP_SAVE_XMM128:
				case UWOP_SAVE_XMM128_FAR:
				{
					DWORD64 offset = op == UWOP_SAVE_XMM128 ? (DWORD64)info.Codes[i + 1] * 16 : (DWORD64)info.Codes[i + 1] | ((DWORD64)info.Codes[i + 2] << 16);
					M128A* slot = (M128A*)(frame + offset);
					context->Fx.Xmm[opInfo] = *slot;
					if (pointers != NULL)
						pointers->FloatingContext[opInfo] = slot;
					break;
				}
				case UWOP_PUSH_MACHFRAME:
				{
					DWORD64 base = context->Rsp + (opInfo != 0 ? 8 : 0);
					context->Rip = *(DWORD64*)base;
					context->Rsp = *(DWORD64*)(base + 24);
					machineFrame = TRUE;
					break;
				}
				default:
					break;
			}
			i += slots;
		}

		if ((info.Flags & UNW_FLAG_CHAININFO) == 0)
			break;
		// A chained entry describes a fragment; the parent's codes all apply, as its prolog has run
		function = (RUNTIME_FUNCTION*)PhxAfterCodes(&info);
		PhxParseInfo(imageBase, function, &info);
		prologOffset = (DWORD64)-1;
	}

	if (!machineFrame)
	{
		context->Rip = *(DWORD64*)context->Rsp;
		context->Rsp += 8;
	}

	if (handlerType != 0 && inBody && (info.Flags & handlerType) != 0)
	{
		const BYTE* handler = PhxAfterCodes(&info);
		*handlerData = (PVOID)(handler + 4);
		return (PEXCEPTION_ROUTINE)(imageBase + *(const DWORD*)handler);
	}
	return NULL;
}
