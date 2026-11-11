param(
	[ValidateSet('Debug', 'Release')]
	[string]$Configuration = 'Debug',
	[string]$VmName = 'Alloy',
	[string]$KernelArgs = ''
)

# Builds every image, then build\esp, build\os and build\Alloy.vhdx from them (no admin needed)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'build'
$bin = Join-Path $build 'bin'
$esp = Join-Path $build 'esp'
$os = Join-Path $build 'os'
$vhd = Join-Path $build 'Alloy.vhdx'

dotnet build (Join-Path $root 'Alloy.sln') -c $Configuration -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'build: dotnet build failed' }

# The .NET apps (src\Apps) only become native images on publish; each one lands in build\bin
dotnet publish (Join-Path $root 'src\Apps\Apps.slnf') -c $Configuration -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'build: dotnet publish of the apps failed' }

# The EFI system partition holds what the loader loads: \EFI\BOOT\BOOTX64.EFI and \EFI\ALLOY\{kernel.exe, kernel.sym, cmdline.txt}
Remove-Item $esp -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $os -Recurse -Force -ErrorAction SilentlyContinue
$bootDir = New-Item -ItemType Directory -Force (Join-Path $esp 'EFI\BOOT')
$alloyDir = New-Item -ItemType Directory -Force (Join-Path $esp 'EFI\ALLOY')
Copy-Item (Join-Path $bin 'BOOTX64.EFI') $bootDir
Copy-Item (Join-Path $bin 'kernel.exe') $alloyDir

$tool = Join-Path $root "tools\BuildImage\bin\$Configuration\net10.0\BuildImage.dll"
dotnet $tool sym (Join-Path $alloyDir 'kernel.sym') (Join-Path $bin 'kernel.map') 'kernel_'
if ($LASTEXITCODE -ne 0) { throw 'build: kernel.sym failed' }
if ($KernelArgs) { Set-Content -Path (Join-Path $alloyDir 'cmdline.txt') -Value $KernelArgs -NoNewline -Encoding ascii }

# The Alloy partition holds everything else at its root: src\Root's tree, the programs under bin, and Doom's data
Copy-Item (Join-Path $root 'src\Root') $os -Recurse
$osBin = New-Item -ItemType Directory -Force (Join-Path $os 'bin')
Get-ChildItem $bin -Filter '*.exe' | Where-Object { $_.Name -ne 'kernel.exe' } | Copy-Item -Destination $osBin

# Typefaces come from the host's Windows fonts, local only like the game data; apps fall back to the 8x8 font when one is missing
$fontDir = New-Item -ItemType Directory -Force (Join-Path $os 'fonts')
$fonts = @{ 'sans' = 'segoeui.ttf'; 'sans-bold' = 'segoeuib.ttf'; 'serif' = 'times.ttf'; 'serif-bold' = 'timesbd.ttf'; 'mono' = 'consola.ttf' }
foreach ($name in $fonts.Keys)
{
	$source = Join-Path $env:WINDIR ('Fonts' + [IO.Path]::DirectorySeparatorChar + $fonts[$name])
	if (Test-Path $source) { Copy-Item $source (Join-Path $fontDir "$name.ttf") } else { "build: $($fonts[$name]) not found, skipping the $name face" }
}

# Game data is optional and local only, as in MetalOS's build_vhd.cmd: never committed, never in CI
$doomData = 'C:\Program Files (x86)\Steam\steamapps\common\Ultimate Doom\base'
if (Test-Path (Join-Path $doomData 'DOOM.WAD')) { Copy-Item (Join-Path $doomData 'DOOM.WAD') (Join-Path $os 'DOOM.WAD') } else { 'build: DOOM.WAD not found, skipping game data' }
if (Test-Path (Join-Path $doomData 'DEFAULT.CFG')) { Copy-Item (Join-Path $doomData 'DEFAULT.CFG') (Join-Path $os 'default.cfg') }

# A running VM holds the disk open, so the swap below would fail halfway
$vm = if (Get-Command Get-VM -ErrorAction SilentlyContinue) { Get-VM -Name $VmName -ErrorAction SilentlyContinue }
if ($vm -and $vm.State -ne 'Off') { throw "build: VM '$VmName' is running; stop it before rebuilding the disk" }

dotnet $tool vhdx $vhd $esp $os
if ($LASTEXITCODE -ne 0) { throw 'build: vhdx failed' }

# Hyper-V opens the disk as the VM's own account, which needs an explicit grant on every new file
if ($vm) { icacls $vhd /grant "$($vm.VMId):F" | Out-Null }
"build: $vhd"
