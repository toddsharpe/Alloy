param(
	[ValidateSet('Debug', 'Release')]
	[string]$Configuration = 'Debug',
	[string]$VmName = 'Alloy',
	[string]$KernelArgs = ''
)

# Builds every image, then build\esp and build\Alloy.vhdx from it (no admin needed)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'build'
$bin = Join-Path $build 'bin'
$esp = Join-Path $build 'esp'
$vhd = Join-Path $build 'Alloy.vhdx'

dotnet build (Join-Path $root 'Alloy.sln') -c $Configuration -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'build: dotnet build failed' }

# The .NET apps (src\Apps) only become native images on publish; each one lands in build\bin
dotnet publish (Join-Path $root 'src\Apps\Apps.slnf') -c $Configuration -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'build: dotnet publish of the apps failed' }

# The ESP layout the loader expects: \EFI\BOOT\BOOTX64.EFI plus \EFI\ALLOY\{kernel.exe, initrd.img}
Remove-Item $esp -Recurse -Force -ErrorAction SilentlyContinue
$bootDir = New-Item -ItemType Directory -Force (Join-Path $esp 'EFI\BOOT')
$alloyDir = New-Item -ItemType Directory -Force (Join-Path $esp 'EFI\ALLOY')
Copy-Item (Join-Path $bin 'BOOTX64.EFI') $bootDir
Copy-Item (Join-Path $bin 'kernel.exe') $alloyDir

$tool = Join-Path $root "tools\BuildImage\bin\$Configuration\net10.0\BuildImage.dll"
dotnet $tool sym (Join-Path $alloyDir 'kernel.sym') (Join-Path $bin 'kernel.map') 'kernel_'
if ($LASTEXITCODE -ne 0) { throw 'build: kernel.sym failed' }
if ($KernelArgs) { Set-Content -Path (Join-Path $alloyDir 'cmdline.txt') -Value $KernelArgs -NoNewline -Encoding ascii }

$initrdRoot = (Resolve-Path (Join-Path $root 'src\Initrd')).Path
$initrdFiles = @(Get-ChildItem $initrdRoot -Recurse -File | ForEach-Object { "$($_.FullName.Substring($initrdRoot.Length + 1).Replace('\', '/'))=$($_.FullName)" })
$initrdFiles += @(Get-ChildItem $bin -Filter '*.exe' | Where-Object { $_.Name -ne 'kernel.exe' } | ForEach-Object { "bin/$($_.Name)=$($_.FullName)" })

# Game data is optional and local only, as in MetalOS's build_vhd.cmd: never committed, never in CI
$doomData = 'C:\Program Files (x86)\Steam\steamapps\common\Ultimate Doom\base'
if (Test-Path (Join-Path $doomData 'DOOM.WAD')) { $initrdFiles += "DOOM.WAD=$(Join-Path $doomData 'DOOM.WAD')" } else { 'build: DOOM.WAD not found, skipping game data' }
if (Test-Path (Join-Path $doomData 'DEFAULT.CFG')) { $initrdFiles += "default.cfg=$(Join-Path $doomData 'DEFAULT.CFG')" }
dotnet $tool initrd (Join-Path $alloyDir 'initrd.img') @initrdFiles
if ($LASTEXITCODE -ne 0) { throw 'build: initrd failed' }

# A running VM holds the disk open, so the swap below would fail halfway
$vm = if (Get-Command Get-VM -ErrorAction SilentlyContinue) { Get-VM -Name $VmName -ErrorAction SilentlyContinue }
if ($vm -and $vm.State -ne 'Off') { throw "build: VM '$VmName' is running; stop it before rebuilding the disk" }

dotnet $tool vhdx $vhd $esp
if ($LASTEXITCODE -ne 0) { throw 'build: vhdx failed' }

# Hyper-V opens the disk as the VM's own account, which needs an explicit grant on every new file
if ($vm) { icacls $vhd /grant "$($vm.VMId):F" | Out-Null }
"build: $vhd"
