# Alloy

An x64 operating system written in C#, compiled to native code with NativeAOT, running on Hyper-V Gen2. It follows MetalOS's design: a UEFI loader, a higher-half kernel, and ring-3 servers (window manager, network stack) reached through syscalls. The goal is Doom.

![The Alloy desktop: terminal, calculator and the Doom fire effect in framed windows](images/desktop.png)

## Status

Phases 0 to 8 are done, and Doom is playable. On Hyper-V Gen2, the loader self-tests the CoreLib, maps `kernel.exe` and hands it a `BootInfo`. The kernel then installs its own descriptor tables, page tables and heap, turns on WP/NX/SMEP/SMAP, and runs its self-test on a framebuffer console. A fault produces a symbolized bugcheck. Kernel threads run preemptively on Hyper-V's synthetic timer. The boot disk is read and written through Hyper-V's synthetic SCSI controller: the kernel reads its GPT and mounts the FAT32 Alloy partition as the root, where programs read, write, create, rename, delete and list files. `init` starts ring-3 processes from it that talk to the kernel through syscalls and to each other through ports, events and shared memory. A user-mode window manager ported from MetalOS's (start menu, taskbar, Alt+Tab, framed windows at 1920x1080 over synthvid) composites app windows. `init` starts it, the network stack and a terminal that runs `ps`, `time`, `hello`, `calc`, `fire`, `doom`, `dhcp`, `dns`, `http`, `html`, `css`, `view`, `browser`, `js`, `ping`, `nettest`, `tcp`, `fonttest` and `crash`, with their output piped into its window, and has `cd`, `pwd`, `ls`, `cat`, `write`, `append`, `mkdir`, `rm` and `mv` built in, with a current directory each process inherits from the one that started it; doomgeneric runs as a ring-3 C program in a fullscreen window. The user-mode network stack speaks ARP, IPv4, ICMP, UDP and TCP, with a DNS resolver and an HTTP/1.1 client (chunked and sized bodies, gzip, redirects) in the SDK, so `http` fetches pages from the internet, and an HTML parser (a WHATWG-style tokenizer and tree builder with implied tags, tables, raw text and character references) that `html` uses to outline them, plus a CSS engine (tokenizer, parser with @media, selectors with specificity, the cascade over a user-agent sheet, inheritance and computed values) that `css` prints the results of, and a layout engine (block and inline formatting with line boxes, floats, tables, flexbox, grid, lists, positioning) with a painter, so `view` renders pages into a window with scrolling and clickable links, and `browser` (also on the start menu) adds an address bar, back, forward and reload, history, a status line showing the link under the mouse, page loads on their own thread, and forms (text fields, checkboxes, radios, selects, submit buttons, GET and POST); PNG, GIF and JPEG decoders in the SDK put the pictures on the page, and a TLS 1.3 client written in C# (X25519, AES-GCM and ChaCha20-Poly1305, SHA-2, HKDF, RSA and ECDSA verification, X.509 chains checked against the Mozilla root bundle in `/certs`) makes https work. A JavaScript engine in the SDK (a tree-walking interpreter with ES2015+ syntax: classes, arrows, destructuring, template literals, generators, async/await over a job queue, and the standard library from Object and Array to RegExp, JSON, Date, Map, Set and Promise) runs scripts from `js`, and `browser` runs each page's scripts against a DOM (document and element methods, events with capture and bubbling, forms, timers, fetch and XMLHttpRequest, storage, observers), relaying clicks and typing to them and relaying their changes back to the layout. Keyboard, mouse, video, network and the disk are Hyper-V's synthetic devices over VMBus, and the shutdown, heartbeat, time sync and key-value pair integration components let Hyper-V Manager shut the VM down or restart it cleanly, see that it is responding, keep its clock on the host's, and show its name and IP address. The SDK rasterizes TrueType faces itself (quadratic outlines, composites, kerning, anti-aliased coverage), so apps draw proportional text at any size. For Doom, `scripts/build.ps1` copies `DOOM.WAD` from a local Steam install onto the disk when it finds one, and it copies the host's Segoe UI, Times New Roman and Consolas files to `/fonts` the same way.

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

`scripts\stop_vm.ps1` asks the guest to shut down and turns the VM off if it doesn't. `scripts\read_com1.ps1` dumps raw serial output and `scripts\screenshot.ps1` saves the VM's screen, both useful when a boot hangs before printing a full line. `scripts\send_input.ps1 -Keys Ctrl+Esc,Enter -Click 960,540 -Drag 1200,511,500,200` types, clicks and drags in the running VM.

## Layout

| Path | Contents |
|---|---|
| `src/CoreLib` | The system module compiled into every image: `System.*` types, runtime helpers ILC calls, platform layers |
| `src/Arch/x64` | Privileged CPU instructions (MASM) for the loader and kernel |
| `src/Abi` | Contracts shared between the images, the SDK and host tools: syscalls, boot info, PE and symbol formats |
| `src/Shared` | Device code shared by the loader and kernel |
| `src/Boot` | The UEFI loader, `BOOTX64.EFI` |
| `src/Kernel` | The kernel, `kernel.exe`; `Io` holds the GPT reader, sector cache and FAT32, which the host tests compile too |
| `src/Root` | Files that go at the Alloy partition's root; `build.ps1` adds `bin` and Doom's data |
| `tools/BuildImage` | Builds the VHDX: a FAT32 EFI system partition from `build\esp` and a FAT32 Alloy partition from `build\os` |
| `tests/Alloy.Tests` | Host tests over code shared with the images |
