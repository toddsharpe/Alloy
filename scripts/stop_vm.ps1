param(
	[string]$VmName = 'Alloy'
)

# Powers the VM off (there is no guest shutdown to ask for); build.ps1 needs it off to replace the disk
$ErrorActionPreference = 'Stop'
if ((Get-VM -Name $VmName).State -eq 'Off')
{
	"stop_vm: $VmName is already off"
}
else
{
	Stop-VM -Name $VmName -TurnOff -Force
	"stop_vm: $VmName turned off"
}
