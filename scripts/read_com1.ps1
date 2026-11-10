param(
	[string]$PipeName = 'alloy_com1',
	[int]$ConnectSeconds = 0,
	[string]$Log = ''
)

# Prints the VM's COM1 as it arrives, like MetalOS's read_com1: it never starts or stops the VM (start_vm.ps1 and stop_vm.ps1 do that)
# Hyper-V creates the pipe when the VM starts and drops output while nobody reads, so run this first, then start the VM; it ends when the VM stops
$ErrorActionPreference = 'Stop'
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::In, [System.IO.Pipes.PipeOptions]::Asynchronous)
$deadline = if ($ConnectSeconds -gt 0) { (Get-Date).AddSeconds($ConnectSeconds) } else { [DateTime]::MaxValue }
Write-Host "read_com1: waiting for \\.\pipe\$PipeName (Ctrl+C to stop)"
while (-not $pipe.IsConnected)
{
	try { $pipe.Connect(1000) }
	catch [System.TimeoutException] { if ((Get-Date) -gt $deadline) { throw "read_com1: no VM opened \\.\pipe\$PipeName in $ConnectSeconds seconds" } }
}
Write-Host "read_com1: connected"
if ($Log) { Remove-Item $Log -ErrorAction SilentlyContinue }

$reader = New-Object System.IO.StreamReader($pipe)
try
{
	while ($true)
	{
		$line = $reader.ReadLine()
		if ($null -eq $line) { break }
		Write-Host $line
		if ($Log) { Add-Content -Path $Log -Value $line }
	}
}
finally
{
	$reader.Dispose()
	$pipe.Dispose()
}
Write-Host "read_com1: the VM closed the pipe"
