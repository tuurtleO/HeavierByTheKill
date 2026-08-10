param([int]$ProcessId = 0, [int]$WatchSeconds = 0, [float]$TestSpeed = 0, [float]$TestStaminaMultiplier = 0)
$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HBKMemory {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
'@

$process = if ($ProcessId) { Get-Process -Id $ProcessId } else { Get-Process -Name DarkSoulsRemastered | Select-Object -First 1 }
$module = $process.MainModule
$supportedSizes=@(0x3817800,0x319B000)
if ($module.ModuleMemorySize -notin $supportedSizes) { throw ('Unsupported module size: 0x{0:X}' -f $module.ModuleMemorySize) }
$access=if($TestSpeed -gt 0 -or $TestStaminaMultiplier -gt 0){0x0438}else{0x0410} # add VM_OPERATION | VM_WRITE only for explicit tests
$handle = [HBKMemory]::OpenProcess($access, $false, $process.Id)
if ($handle -eq [IntPtr]::Zero) { throw "OpenProcess failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }

function Read-Bytes([long]$address, [int]$count) {
  $buffer = [byte[]]::new($count); $read=[IntPtr]::Zero
  if (-not [HBKMemory]::ReadProcessMemory($handle,[IntPtr]$address,$buffer,$count,[ref]$read) -or $read.ToInt64() -ne $count) { throw ('Read failed at 0x{0:X}' -f $address) }
  return $buffer
}
function Read-U64([long]$address) { [BitConverter]::ToUInt64((Read-Bytes $address 8),0) }
function Read-I32([long]$address) { [BitConverter]::ToInt32((Read-Bytes $address 4),0) }
function Read-F32([long]$address) { [BitConverter]::ToSingle((Read-Bytes $address 4),0) }
function Write-F32([long]$address,[float]$value) { $written=[IntPtr]::Zero; $bytes=[BitConverter]::GetBytes($value); if(-not [HBKMemory]::WriteProcessMemory($handle,[IntPtr]$address,$bytes,4,[ref]$written) -or $written.ToInt64() -ne 4){ throw ('Write failed at 0x{0:X}' -f $address) } }
function Write-I32([long]$address,[int]$value) { $written=[IntPtr]::Zero; $bytes=[BitConverter]::GetBytes($value); if(-not [HBKMemory]::WriteProcessMemory($handle,[IntPtr]$address,$bytes,4,[ref]$written) -or $written.ToInt64() -ne 4){ throw ('Write failed at 0x{0:X}' -f $address) } }
function Find-Pattern([byte[]]$data,[string]$pattern) {
  $tokens=$pattern.Split(' '); for($i=0;$i -le $data.Length-$tokens.Length;$i++){ $ok=$true; for($j=0;$j -lt $tokens.Length;$j++){ if($tokens[$j] -ne '?' -and $data[$i+$j] -ne [Convert]::ToByte($tokens[$j],16)){ $ok=$false; break } }; if($ok){ return $i } }; return -1
}

try {
  $base=$module.BaseAddress.ToInt64(); $image=Read-Bytes $base $module.ModuleMemorySize
  $pattern='48 8B 05 ? ? ? ? 48 8B 48 68 48 85 C9 0F 84 ? ? ? ? 48 39 5E 10 0F 84 ? ? ? ? 48'
  $match=Find-Pattern $image $pattern; if($match -lt 0){ throw 'WorldChrBase signature not found' }
  $disp=[BitConverter]::ToInt32($image,$match+3); $global=$base+$match+7+$disp
  $world=Read-U64 $global; $chr=Read-U64 ($world+0x68)
  $hp=Read-I32 ($chr+0x3E8); $maxHp=Read-I32 ($chr+0x3EC); $stamina=Read-I32 ($chr+0x3F8); $maxStamina=Read-I32 ($chr+0x3FC)
  $classPattern='48 8B 05 ? ? ? ? 48 85 C0 ? ? F3 0F 58 80 AC 00 00 00'
  $classMatch=Find-Pattern $image $classPattern; if($classMatch -lt 0){ throw 'ChrClassBase signature not found' }
  $classDisp=[BitConverter]::ToInt32($image,$classMatch+3); $classGlobal=$base+$classMatch+7+$classDisp
  $classBase=Read-U64 $classGlobal; $chrData2=Read-U64 ($classBase+0x10)
  $souls=Read-I32 ($chrData2+0x94)
  [pscustomobject]@{ ProcessId=$process.Id; ModuleBase=('0x{0:X}' -f $base); Signature=('0x{0:X}' -f ($base+$match)); World=('0x{0:X}' -f $world); Character=('0x{0:X}' -f $chr); CharacterStats=('0x{0:X}' -f $chrData2); Health=$hp; MaxHealth=$maxHp; Stamina=$stamina; MaxStamina=$maxStamina; Souls=$souls }
  foreach($region in @(@('Character',$chr,0x2000),@('ClassBase',$classBase,0x4000),@('CharacterStats',$chrData2,0x1000))) {
    try { $bytes=Read-Bytes $region[1] $region[2]; for($i=0;$i -le $bytes.Length-4;$i+=4){ $v=[BitConverter]::ToInt32($bytes,$i); if($v -in @(310000,310004,101000)){ 'EQUIP_CANDIDATE region={0} address=0x{1:X} offset=0x{2:X} value={3}' -f $region[0],($region[1]+$i),$i,$v } } } catch {}
  }
  if($TestSpeed -gt 0) {
    $map=Read-U64 ($chr+0x68); $animData=Read-U64 ($map+0x18); $speedAddress=$animData+0xA8; $original=Read-F32 $speedAddress
    try { Write-F32 $speedAddress $TestSpeed; "SPEED_TEST_ACTIVE address=0x$($speedAddress.ToString('X')) original=$original test=$TestSpeed"; Start-Sleep -Seconds 15 }
    finally { Write-F32 $speedAddress $original; "SPEED_TEST_RESTORED value=$original" }
  }
  if($TestStaminaMultiplier -gt 0) {
    $currentAnimRoot=Read-U64 ($chr+0x68); $currentAnim=Read-U64 ($currentAnimRoot+0x48); $last=Read-I32 ($chr+0x3F8); $timer=[Diagnostics.Stopwatch]::StartNew()
    "STAMINA_TEST_WATCHING multiplier=$TestStaminaMultiplier"
    while($timer.Elapsed.TotalSeconds -lt 60) {
      $now=Read-I32 ($chr+0x3F8); $anim=Read-I32 ($currentAnim+0x80)
      if($now -lt $last) {
        $baseCost=$last-$now; $extra=[Math]::Round($baseCost*($TestStaminaMultiplier-1)); $adjusted=[Math]::Max(0,$now-$extra); Write-I32 ($chr+0x3F8) $adjusted
        "STAMINA_TEST_APPLIED anim=$anim before=$last vanillaAfter=$now baseCost=$baseCost extra=$extra adjustedAfter=$adjusted"; break
      }
      $last=$now; Start-Sleep -Milliseconds 5
    }
    if($timer.Elapsed.TotalSeconds -ge 60){ 'STAMINA_TEST_TIMEOUT' }
  }
  if($WatchSeconds -gt 0) {
    $currentAnimRoot=Read-U64 ($chr+0x68); $currentAnim=Read-U64 ($currentAnimRoot+0x48)
    $lastStamina=$stamina; $lastAnim=Read-I32 ($currentAnim+0x80); $lastSouls=$souls; $timer=[Diagnostics.Stopwatch]::StartNew()
    'WATCHING'
    while($timer.Elapsed.TotalSeconds -lt $WatchSeconds) {
      $nowStamina=Read-I32 ($chr+0x3F8); $nowAnim=Read-I32 ($currentAnim+0x80); $nowSouls=Read-I32 ($chrData2+0x94)
      if($nowStamina -ne $lastStamina -or $nowAnim -ne $lastAnim -or $nowSouls -ne $lastSouls) { '{0,8:F3}s stamina={1,4} anim={2} souls={3}' -f $timer.Elapsed.TotalSeconds,$nowStamina,$nowAnim,$nowSouls; $lastStamina=$nowStamina; $lastAnim=$nowAnim; $lastSouls=$nowSouls }
      Start-Sleep -Milliseconds 10
    }
  }
} finally { [void][HBKMemory]::CloseHandle($handle) }
