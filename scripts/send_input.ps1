param(
	[string]$VmName = 'Alloy',
	[string[]]$Keys = @(),
	[string]$Text = '',
	[int[]]$Click = @(),
	[int[]]$Drag = @(),
	[int]$DelayMs = 150
)

# Drives the running VM's keyboard and mouse over Hyper-V WMI: -Keys Enter,Up:800,Ctrl+Esc (name or code, :hold ms), -Click x,y[,x,y...], -Drag x1,y1,x2,y2
$ErrorActionPreference = 'Stop'
$names = @{ Back = 8; Tab = 9; Enter = 13; Shift = 16; Ctrl = 17; Alt = 18; Esc = 27; Space = 32; Left = 37; Up = 38; Right = 39; Down = 40; Win = 91; F1 = 112 }

$vm = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "ElementName='$VmName'"
$keyboard = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_Keyboard
$mouse = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_SyntheticMouse

function KeyCode([string]$name)
{
	if ($names.ContainsKey($name)) { return [uint32]$names[$name] }
	if ($name.Length -eq 1) { return [uint32][char]$name.ToUpper() }
	return [uint32]$name
}

foreach ($entry in $Keys)
{
	$parts = $entry.Split(':')
	$hold = if ($parts.Length -gt 1) { [int]$parts[1] } else { 60 }
	$combo = @($parts[0].Split('+') | ForEach-Object { KeyCode $_ })
	foreach ($code in $combo) { Invoke-CimMethod -InputObject $keyboard -MethodName PressKey -Arguments @{ keyCode = $code } | Out-Null }
	Start-Sleep -Milliseconds $hold
	[array]::Reverse($combo)
	foreach ($code in $combo) { Invoke-CimMethod -InputObject $keyboard -MethodName ReleaseKey -Arguments @{ keyCode = $code } | Out-Null }
	Start-Sleep -Milliseconds $DelayMs
}

if ($Text) { Invoke-CimMethod -InputObject $keyboard -MethodName TypeText -Arguments @{ asciiText = $Text } | Out-Null }

for ($i = 0; $i + 1 -lt $Click.Length; $i += 2)
{
	$x = $Click[$i]
	$y = $Click[$i + 1]
	Invoke-CimMethod -InputObject $mouse -MethodName SetAbsolutePosition -Arguments @{ HorizontalPosition = $x; VerticalPosition = $y } | Out-Null
	Start-Sleep -Milliseconds 50
	Invoke-CimMethod -InputObject $mouse -MethodName ClickButton -Arguments @{ ButtonIndex = [uint32]1 } | Out-Null
	Start-Sleep -Milliseconds $DelayMs
}

if ($Drag.Length -eq 4)
{
	Invoke-CimMethod -InputObject $mouse -MethodName SetAbsolutePosition -Arguments @{ HorizontalPosition = $Drag[0]; VerticalPosition = $Drag[1] } | Out-Null
	Start-Sleep -Milliseconds 50
	Invoke-CimMethod -InputObject $mouse -MethodName SetButtonState -Arguments @{ ButtonIndex = [uint32]1; IsDown = $true } | Out-Null
	for ($step = 1; $step -le 10; $step++)
	{
		$x = [int32]($Drag[0] + ($Drag[2] - $Drag[0]) * $step / 10)
		$y = [int32]($Drag[1] + ($Drag[3] - $Drag[1]) * $step / 10)
		Invoke-CimMethod -InputObject $mouse -MethodName SetAbsolutePosition -Arguments @{ HorizontalPosition = $x; VerticalPosition = $y } | Out-Null
		Start-Sleep -Milliseconds 30
	}
	Invoke-CimMethod -InputObject $mouse -MethodName SetButtonState -Arguments @{ ButtonIndex = [uint32]1; IsDown = $false } | Out-Null
}
