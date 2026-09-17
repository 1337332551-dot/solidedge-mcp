# Generate Solid Edge interop assemblies with TlbImp (ASCII-safe, run with cwd = target project dir)
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File gen_interop.ps1 [-Dir .]
# Output: SolidEdgeFramework.dll / SolidEdgeConstants.dll / SolidEdgePart.dll / SolidEdgeFrameworkSupport.dll
param([string]$Dir = '.')

$ErrorActionPreference = 'Stop'
$tlbimp = 'C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\x64\TlbImp.exe'
if (-not (Test-Path $tlbimp)) {
    $tlbimp = 'C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\TlbImp.exe'
}

# Typelib GUIDs (SE2022, verified from HKCR\TypeLib)
$maps = [ordered]@{
  'SolidEdgeFramework'        = '8A7EFA3A-F000-11D1-BDFC-080036B4D502'
  'SolidEdgeConstants'        = 'C467A6F5-27ED-11D2-BE30-080036B4D502'
  'SolidEdgePart'             = '8A7EFA42-F000-11D1-BDFC-080036B4D502'
  'SolidEdgeFrameworkSupport' = '943AC5C6-F4DB-11D1-BE00-080036B4D502'
}

$refs = @()
foreach ($name in $maps.Keys) {
  $g = $maps[$name]
  $path = (Get-ItemProperty ('Registry::HKEY_CLASSES_ROOT\TypeLib\{' + $g + '}\1.0\0\win64')).'(default)'
  Write-Output ('TLB ' + $name + ' = ' + $path)
  $outDll = Join-Path $Dir ($name + '.dll')
  # path with spaces must be quoted
  $argList = @(('"' + $path + '"'), '/out:' + $outDll, '/namespace:' + $name, '/silent')
  foreach ($r in $refs) { $argList += '/reference:' + (Join-Path $Dir $r) }
  $p = Start-Process -FilePath $tlbimp -ArgumentList $argList -NoNewWindow -Wait -PassThru
  Write-Output ('TLBIMP ' + $name + ' exit=' + $p.ExitCode)
  if ($p.ExitCode -eq 0) { $refs += ($name + '.dll') }
}
Get-ChildItem $Dir -Filter 'SolidEdge*.dll' | ForEach-Object { Write-Output ('OK ' + $_.Name) }
