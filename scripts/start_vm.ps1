param(
	[string]$VmName = 'Alloy',
	[switch]$Restart
)

# Starts the VM (a running one is left alone unless -Restart turns it off first); start read_com1.ps1 before this to see the boot
$ErrorActionPreference = 'Stop'
$vm = Get-VM -Name $VmName
if ($vm.State -ne 'Off' -and $Restart)
{
	Stop-VM -Name $VmName -TurnOff -Force
	$vm = Get-VM -Name $VmName
}
if ($vm.State -eq 'Off')
{
	Start-VM -Name $VmName
	"start_vm: $VmName started"
}
else
{
	"start_vm: $VmName is already $($vm.State)"
}
