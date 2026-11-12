# Builds unwindcheck.exe: the harness against windows.h, and src/Win32/unwind.c with its entry point renamed so both unwinders can be linked in
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
$vs = & "$installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$out = Join-Path $root 'build\unwindcheck'
New-Item -ItemType Directory -Force $out | Out-Null
$win32 = Join-Path $root 'src\Win32'
$commands = @(
	"set PATH=%PATH%;$installer",
	"call `"$vcvars`" >nul 2>nul",
	"cl /nologo /c /O1 /W3 /DRtlVirtualUnwind=PhxRtlVirtualUnwind /DRtlLookupFunctionEntry=PhxRtlLookupFunctionEntry /I`"$win32`" /Fo`"$out\unwind.obj`" `"$win32\unwind.c`"",
	"cl /nologo /c /O1 /W3 /Fo`"$out\unwindcheck.obj`" `"$here\unwindcheck.c`"",
	"link /nologo /OUT:`"$out\unwindcheck.exe`" `"$out\unwindcheck.obj`" `"$out\unwind.obj`" kernel32.lib"
) -join ' && '
$ErrorActionPreference = 'Continue'
cmd /c "$commands 2>&1"
if ($LASTEXITCODE -ne 0) { throw 'unwindcheck: build failed' }
"built $out\unwindcheck.exe"
