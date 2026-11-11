param(
	[string]$VmName = 'Alloy',
	[int]$Seconds = 20
)

# Asks the guest to shut down through the shutdown integration component, and turns the VM off if it isn't off within -Seconds; build.ps1 needs it off to replace the disk
$ErrorActionPreference = 'Stop'
if ((Get-VM -Name $VmName).State -eq 'Off')
{
	"stop_vm: $VmName is already off"
	exit 0
}

# Stop-VM blocks on the guest, so it runs in a job while the VM's state is watched here; Hyper-V reports Off a few seconds after the guest resets
$started = Get-Date
$job = Start-Job -ScriptBlock { param($name) Stop-VM -Name $name -Force } -ArgumentList $VmName
while ((Get-VM -Name $VmName).State -ne 'Off' -and ((Get-Date) - $started).TotalSeconds -lt $Seconds)
{
	Start-Sleep -Milliseconds 250
}
Stop-Job $job -ErrorAction SilentlyContinue
Remove-Job $job -Force -ErrorAction SilentlyContinue

if ((Get-VM -Name $VmName).State -eq 'Off')
{
	"stop_vm: $VmName shut down in $([int]((Get-Date) - $started).TotalSeconds)s"
	exit 0
}

Stop-VM -Name $VmName -TurnOff -Force
"stop_vm: $VmName did not shut down in ${Seconds}s, turned off"
