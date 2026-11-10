param(
	[string]$VmName = 'Alloy',
	[string]$Path = "$PSScriptRoot\..\build\screen.png",
	[int]$Width = 1024,
	[int]$Height = 768
)

# Saves the VM's current screen as a PNG via the Hyper-V thumbnail API (Hyper-V Administrators is enough)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$service = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_VirtualSystemManagementService
$settings = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_VirtualSystemSettingData | Where-Object { $_.ElementName -eq $VmName -and $_.VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized' }
$result = Invoke-CimMethod -InputObject $service -MethodName GetVirtualSystemThumbnailImage -Arguments @{ TargetSystem = $settings; WidthPixels = [uint16]$Width; HeightPixels = [uint16]$Height }
if ($result.ReturnValue -ne 0) { throw "screenshot: GetVirtualSystemThumbnailImage returned $($result.ReturnValue)" }

# The thumbnail is raw RGB565, copied a row at a time because the bitmap's stride may be padded
[byte[]]$pixels = $result.ImageData
$bitmap = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
$data = $bitmap.LockBits((New-Object System.Drawing.Rectangle(0, 0, $Width, $Height)), [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bitmap.PixelFormat)
$rowBytes = $Width * 2
for ($y = 0; $y -lt $Height -and ($y + 1) * $rowBytes -le $pixels.Length; $y++)
{
	[System.Runtime.InteropServices.Marshal]::Copy($pixels, $y * $rowBytes, [IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $rowBytes)
}
$bitmap.UnlockBits($data)
$full = [System.IO.Path]::GetFullPath($Path)
$bitmap.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
"screenshot: $full"
