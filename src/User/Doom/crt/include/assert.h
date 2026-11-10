#pragma once

// Doom only uses assert for sanity checks; Alloy builds it without them, as MetalOS does in release
#define assert(expression) ((void)0)
