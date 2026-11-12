# Alloy

An x64 operating system written in C#, compiled to native code with NativeAOT, running on Hyper-V Gen2. It follows MetalOS's design: a UEFI loader, a higher-half kernel, and ring-3 servers (window manager, network stack) reached through syscalls. Everything above the kernel is a stock .NET program: the apps use the real BCL and GC, linked against a small Win32 layer that turns the runtime's imports into Alloy syscalls.

![The Alloy desktop: terminal, calculator and the Doom fire effect in framed windows](images/desktop.png)

## Devices

Alloy boots as a Hyper-V Generation 2 guest and talks to the hypervisor's synthetic devices directly, over VMBus, with no emulated PC hardware in between.

| Device | What Alloy does with it |
|---|---|
| UEFI | The loader (`BOOTX64.EFI`) self-tests the CoreLib, reads the GOP framebuffer, maps `kernel.exe` and hands it a `BootInfo` |
| x64 CPU | The kernel installs its own descriptor tables, page tables and heap, turns on WP/NX/SMEP/SMAP, and runs ring-3 processes in their own address spaces with syscalls; a fault produces a symbolized bugcheck |
| Synthetic timer | Preemptive kernel threads and the scheduler's clock |
| Synthetic video (synthvid) | The framebuffer at the largest mode the host offers, 1920x1080, that the user-mode window manager composites into |
| Synthetic keyboard and mouse | Key and pointer events, including the wheel, routed through the kernel's input ring to the window manager and on to the focused app |
| Synthetic SCSI (storvsc) | The boot disk: the kernel reads its GPT, caches sectors and mounts the FAT32 Alloy partition as the root, where programs read, write, create, rename, delete and list files |
| Synthetic NIC (netvsc) | NVSP 2 with RNDIS inside; the kernel owns the frame rings and hands raw Ethernet frames to the user-mode network stack |
| Integration components | Shutdown, heartbeat, time sync and key-value pairs, so Hyper-V Manager can shut the VM down or restart it cleanly, see that it responds, keep its clock on the host's, and show its name and IP address |
| CMOS RTC | Wall-clock time for `time`, cookies and scripts |
| 16550 UART | COM1, the kernel log, read from the host through a named pipe |

## Apps

`init` starts the window manager, the network stack and a terminal. The terminal runs any program on the disk with its output piped into its window, and has `cd`, `pwd`, `ls`, `cat`, `write`, `append`, `mkdir`, `rm` and `mv` built in; each process inherits the current directory of the one that started it.

| App | What it is |
|---|---|
| `wmsvr` | The window manager: compositing, frames, focus, drag, z-order, taskbar, start menu, Alt+Tab and input routing |
| `netsvr` | The network stack: ARP, IPv4, ICMP, UDP and TCP over the kernel's frame rings, serving sockets to other processes |
| `term` | The command shell |
| `browser` | A web browser: address bar, back, forward and reload, history, a status line, page loads on their own thread, forms (GET and POST), images, https, and the page's JavaScript running against a DOM |
| `view <url>` | A page rendered into a window, scrolled with the keys and wheel, links followed by clicking |
| `doom` | Ultimate Doom, as doomgeneric compiled as a ring-3 C program, fullscreen; `build.ps1` copies `DOOM.WAD` from a local Steam install when it finds one |
| `calc` | A calculator |
| `fire` | The PSX Doom fire effect |
| `fonttest` | Sample text in the shipped TrueType faces at several sizes |
| `dhcp` | A DHCP client that configures interface 0 |
| `dns <name>` | A name's A records from the configured server |
| `ping <host>` | ICMP echo over a raw socket |
| `tcp <host> <port>` | A stream socket from the terminal: send text or a GET, print what comes back |
| `nettest` | A UDP datagram to the gateway |
| `http <url> [file]` | A fetch: status, headers and the start of the body, or the body saved to a file |
| `html <url>` | An outline of a page: title, headings, links |
| `css <url>` | What the cascade decided for a page's first elements |
| `js [file|url|-e source]` | Runs JavaScript; with no argument, the engine's self-test |
| `ps`, `time`, `hello` | One line per process, the RTC time, the obligatory greeting |
| `crash`, `ipctest`, `ziptest`, `gcstress` | Fault handling, port and shared-memory IPC, the BCL's compression, and the GC under interlocked, large-object, syscall and thread-churn load, each proven in a small program |

The apps are built on `Alloy.Sdk`, which holds the windowing and drawing code, the system clipboard (kept by the window server; Ctrl+C and Ctrl+V in the browser and terminal), sockets, an HTTP/1.1 client with keep-alive, a TLS 1.3 client written in C# (X25519, AES-GCM and ChaCha20-Poly1305, SHA-2, HKDF, RSA and ECDSA verification, X.509 chains against the Mozilla root bundle in `/certs`), a cookie jar, an HTML parser, a CSS engine (selectors, the cascade, inheritance, custom properties, media queries), a layout engine (block and inline formatting, floats, tables, flexbox, grid, positioning) with a painter, PNG, GIF, JPEG and WebP decoders, a TrueType rasterizer, a JavaScript engine (a tree-walking interpreter with ES2015+ syntax, modules, generators, async/await, typed arrays, Proxy and the standard library), and the DOM, events, timers, fetch and storage that pages' scripts expect.

## Screenshots

The terminal running `ps`, `dhcp` and `ping` over the Hyper-V synthetic NIC:

![Terminal](images/terminal.png)

The start menu, taskbar and network status in the window manager:

![Start menu](images/start-menu.png)

Ultimate Doom running fullscreen as a ring-3 process:

![Doom](images/doom.png)

## Requirements

- Windows with Hyper-V; being in the Hyper-V Administrators group is enough, no elevation
- .NET 10 SDK (see [global.json](global.json))
- Visual Studio 2022 with the C++ x64 tools (for `ml64` and `link`)

## Build and run

```powershell
scripts\build.ps1                                       # images, then build\Alloy.vhdx: an EFI system partition and the Alloy partition
scripts\new_vm.ps1                                      # once: Gen2 VM 'Alloy', COM1 on \\.\pipe\alloy_com1
scripts\boot_test.ps1 -Until 'Alloy Kernel: ready'  # boot, check COM1, power off
dotnet test tests\Alloy.Tests                         # host tests
```

`scripts\stop_vm.ps1` asks the guest to shut down and turns the VM off if it doesn't. `scripts\read_com1.ps1` dumps raw serial output and `scripts\screenshot.ps1` saves the VM's screen, both useful when a boot hangs before printing a full line. `scripts\send_input.ps1 -Keys Ctrl+Esc,Enter -Click 960,540 -Drag 1200,511,500,200` types, clicks and drags in the running VM. `dotnet run --project tools\PageProbe -- <url>` loads a page on the host with the SDK's parser, cascade, scripts and layout and reports script errors and timings.

## Layout

| Path | Contents |
|---|---|
| `src/CoreLib` | The system module compiled into the loader and kernel: `System.*` types, runtime helpers ILC calls, platform layers |
| `src/Arch/x64` | Privileged CPU instructions (MASM) for the loader and kernel |
| `src/Abi` | Contracts shared between the images, the SDK and host tools: syscalls, boot info, PE and symbol formats |
| `src/Shared` | Device code shared by the loader and kernel |
| `src/Boot` | The UEFI loader, `BOOTX64.EFI` |
| `src/Kernel` | The kernel, `kernel.exe`; `Platform` holds the Hyper-V drivers, `Io` the GPT reader, sector cache and FAT32, which the host tests compile too |
| `src/Win32` | The freestanding C and MASM layer that gives NativeAOT's runtime the Win32 imports it expects, on top of syscalls |
| `src/Apps` | The ring-3 programs, stock .NET published with NativeAOT, and `Alloy.Sdk` they share |
| `src/User` | Doom (doomgeneric linked into a C# user image with its own small C runtime), the user-mode runtime pieces, and a kernel test image |
| `src/Graphics` | Surfaces, the bitmap font, icons and the theme shared by the kernel console and the apps |
| `src/Root` | Files that go at the Alloy partition's root; `build.ps1` adds `bin` and Doom's data |
| `external` | MetalOS.Doom, the doomgeneric port |
| `tools/BuildImage` | Builds the VHDX: a FAT32 EFI system partition from `build\esp` and a FAT32 Alloy partition from `build\os` |
| `tools/PageProbe` | Loads real pages on the host through the SDK's web stack |
| `tests/Alloy.Tests` | Host tests over code shared with the images |
