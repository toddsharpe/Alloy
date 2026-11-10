param(
	[int]$Seconds = 60,
	[string]$Until = '',
	[string]$Log = "$PSScriptRoot\..\build\boot.log",
	[string]$VmName = 'Alloy',
	[string]$PipeName = 'alloy_com1'
)

# Boots the VM, logs COM1 until -Until matches or -Seconds pass, then powers it off; exits 1 on a failure line or a missing -Until
$ErrorActionPreference = 'Stop'
$Failure = 'FAILFAST|BUGCHECK|SELFTEST FAILED'

if ((Get-VM $VmName).State -ne 'Off') { Stop-VM $VmName -TurnOff -Force }
Remove-Item $Log -ErrorAction SilentlyContinue

# COM1 output is dropped while no reader is connected, so attach as soon as the VM starts
Start-VM $VmName
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::In, [System.IO.Pipes.PipeOptions]::Asynchronous)
$pipe.Connect(15000)
$reader = New-Object System.IO.StreamReader($pipe)

$start = Get-Date
$deadline = $start.AddSeconds($Seconds)
$task = $null
$count = 0
$matched = $false
$failed = $null
try
{
	while ((Get-Date) -lt $deadline)
	{
		if ($null -eq $task) { $task = $reader.ReadLineAsync() }
		if (-not $task.Wait(250)) { continue }

		$line = $task.Result
		$task = $null
		if ($null -eq $line) { break }

		$count++
		$stamp = '{0,6:F1}' -f ((Get-Date) - $start).TotalSeconds
		Add-Content -Path $Log -Value "[$stamp] $line"
		Write-Host "[$stamp] $line"

		# After a failure keep reading for a moment so whatever it printed next lands in the log too
		if (-not $failed -and $line -match $Failure) { $failed = $line; $deadline = (Get-Date).AddSeconds(3) }
		if ($Until -and -not $failed -and $line -match $Until) { $matched = $true; break }
	}
}
finally
{
	if ((Get-VM $VmName).State -ne 'Off') { Stop-VM $VmName -TurnOff -Force }
	$reader.Dispose()
	$pipe.Dispose()
}

$elapsed = [int]((Get-Date) - $start).TotalSeconds
"boot_test: $count lines in ${elapsed}s, log at $([System.IO.Path]::GetFullPath($Log))"
if ($count -eq 0) { 'boot_test: FAIL, nothing on COM1'; exit 1 }
if ($failed) { "boot_test: FAIL, $failed"; exit 1 }
if ($Until -and -not $matched) { "boot_test: FAIL, '$Until' never appeared"; exit 1 }
'boot_test: PASS'
exit 0
