param(
	[string]$VmName = 'Alloy',
	[long]$MemoryBytes = 4GB,
	[string]$SwitchName = 'External'
)

# Creates the Gen2 dev VM booting build\Alloy.vhdx, with COM1/COM2 on named pipes (Hyper-V Administrators is enough)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vhd = Join-Path $root 'build\Alloy.vhdx'
$pipe = $VmName.ToLowerInvariant()

if (Get-VM -Name $VmName -ErrorAction SilentlyContinue) { "new_vm: '$VmName' already exists"; exit 0 }
if (-not (Test-Path $vhd)) { throw 'new_vm: build\Alloy.vhdx is missing; run scripts\build.ps1 first' }

$vm = New-VM -Name $VmName -Generation 2 -MemoryStartupBytes $MemoryBytes -NoVHD
Set-VMMemory -VM $vm -DynamicMemoryEnabled $false
Set-VMProcessor -VM $vm -Count 1
Set-VMFirmware -VM $vm -EnableSecureBoot Off
Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter

# One synthetic NIC for NetSvr and dhcp, on the same switch as the MetalOS VM; the disk stays first in boot order so there is no PXE wait
if (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue) { Add-VMNetworkAdapter -VM $vm -SwitchName $SwitchName }
Set-VMComPort -VM $vm -Number 1 -Path "\\.\pipe\${pipe}_com1"
Set-VMComPort -VM $vm -Number 2 -Path "\\.\pipe\${pipe}_com2"
Add-VMHardDiskDrive -VM $vm -Path $vhd
Set-VMFirmware -VM $vm -FirstBootDevice (Get-VMHardDiskDrive -VM $vm)
Set-VM -VM $vm -CheckpointType Disabled -AutomaticStartAction Nothing -AutomaticStopAction TurnOff
icacls $vhd /grant "$($vm.VMId):F" | Out-Null

"new_vm: created '$VmName' (Gen2, Secure Boot off, COM1 \\.\pipe\${pipe}_com1)"
