# Проверка (WSH-E19): что Windows, игровые контроллеры и G HUB отдают про заряд устройств.
# Ничего не меняет, только читает. Запуск: powershell -ExecutionPolicy Bypass -File scripts\probe-battery.ps1
$ErrorActionPreference = 'Continue'
$out = Join-Path $env:TEMP 'battery-probe.txt'
$lines = New-Object System.Collections.Generic.List[string]
function Say($s) { $lines.Add([string]$s); Write-Host $s }

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$type) { $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(-1) | Out-Null; $t.Result }

# 1. Свойство заряда у устройств PnP (так его показывают «Параметры → Bluetooth»).
Say '== PnP: {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2'
$battKey = '{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2'
$keys = @($battKey, 'DEVPKEY_Device_ContainerId', '{83DA6326-97A6-4088-9453-A1923F573B29} 15', 'DEVPKEY_Device_IsPresent', 'DEVPKEY_Device_Parent')
$devs = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match '^(BTH|HID|USB|SWD)' }
$containers = @{}
foreach ($d in $devs) {
    $p = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName $battKey -ErrorAction SilentlyContinue
    if ($p -and $null -ne $p.Data) {
        $all = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName $keys -ErrorAction SilentlyContinue
        $v = @{}; foreach ($x in $all) { $v[$x.KeyName] = $x.Data }
        $containers[[string]$v['DEVPKEY_Device_ContainerId']] = 1
        Say ("{0}% | {1} | status={2} | present={3} | conn15={4} | container={5}" -f $p.Data, $d.FriendlyName, $d.Status, $v['DEVPKEY_Device_IsPresent'], $v['{83DA6326-97A6-4088-9453-A1923F573B29} 15'], $v['DEVPKEY_Device_ContainerId'])
        Say ("    id={0} | parent={1}" -f $d.InstanceId, $v['DEVPKEY_Device_Parent'])
    }
}
Say '== PnP: all devices in those containers'
foreach ($d in $devs) {
    $c = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_ContainerId' -ErrorAction SilentlyContinue).Data
    if ($c -and $containers.ContainsKey([string]$c)) { Say ("  {0} | {1} | {2} | {3}" -f $c, $d.Status, $d.FriendlyName, $d.InstanceId) }
}

# 2. Сопряжённые Bluetooth-устройства (имя, подключено ли).
Say '== Bluetooth AEP (paired)'
try {
    $null = [Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType = WindowsRuntime]
    $props = [string[]]@('System.Devices.Aep.IsConnected', 'System.Devices.Aep.IsPaired', 'System.Devices.Aep.ContainerId', 'System.Devices.Aep.Category', 'System.Devices.Aep.ProtocolId')
    $q = 'System.Devices.Aep.ProtocolId:="{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}" OR System.Devices.Aep.ProtocolId:="{bb7bb05e-5972-42b5-94fc-76eaa7084d49}"'
    $list = Await ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync($q, $props, [Windows.Devices.Enumeration.DeviceInformationKind]::AssociationEndpoint)) ([Windows.Devices.Enumeration.DeviceInformationCollection])
    foreach ($i in $list) {
        if (-not $i.Properties['System.Devices.Aep.IsPaired']) { continue }
        Say ("  {0} | connected={1} | container={2} | category={3} | proto={4}" -f $i.Name, $i.Properties['System.Devices.Aep.IsConnected'], $i.Properties['System.Devices.Aep.ContainerId'], ($i.Properties['System.Devices.Aep.Category'] -join ','), $i.Properties['System.Devices.Aep.ProtocolId'])
    }
} catch { Say "  ошибка: $_" }

# 3. Игровые контроллеры (Windows.Gaming.Input).
Say '== Windows.Gaming.Input'
try {
    $null = [Windows.Gaming.Input.RawGameController, Windows.Gaming.Input, ContentType = WindowsRuntime]
    $null = [Windows.Gaming.Input.Gamepad]::Gamepads.Count
    $null = [Windows.Gaming.Input.RawGameController]::RawGameControllers.Count
    Start-Sleep -Seconds 3
    Say ("  gamepads={0}" -f [Windows.Gaming.Input.Gamepad]::Gamepads.Count)
    foreach ($r in [Windows.Gaming.Input.RawGameController]::RawGameControllers) {
        $b = $r.TryGetBatteryReport()
        $bs = if ($b) { "status={0} remaining={1} full={2} rate={3}" -f $b.Status, $b.RemainingCapacityInMilliwattHours, $b.FullChargeCapacityInMilliwattHours, $b.ChargeRateInMilliwatts } else { 'no report' }
        Say ("  {0} | wireless={1} | vid={2:X4} pid={3:X4} | {4}" -f $r.DisplayName, $r.IsWireless, $r.HardwareVendorId, $r.HardwareProductId, $bs)
        Say ("    nonRoamable={0}" -f $r.NonRoamableId)
    }
} catch { Say "  ошибка: $_" }

# 4. XInput (уровень заряда ступенями).
Say '== XInput'
try {
    Add-Type -Namespace Probe -Name XI -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct BAT { public byte Type; public byte Level; }
[System.Runtime.InteropServices.DllImport("xinput1_4.dll")] public static extern int XInputGetBatteryInformation(int user, byte devType, out BAT info);
'@
    for ($u = 0; $u -lt 4; $u++) {
        $info = New-Object Probe.XI+BAT
        $r = [Probe.XI]::XInputGetBatteryInformation($u, 0, [ref]$info)
        if ($r -eq 0) { Say ("  user {0}: type={1} level={2}" -f $u, $info.Type, $info.Level) }
    }
} catch { Say "  ошибка: $_" }

# 5. G HUB: его окно говорит с агентом по ws://localhost:9010.
Say '== G HUB ws://localhost:9010'
try {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.Options.AddSubProtocol('json')
    try { $ws.Options.SetRequestHeader('Origin', 'file://') } catch { Say "  Origin: $_" }
    $ct = [Threading.CancellationToken]::None
    if (-not $ws.ConnectAsync([Uri]'ws://localhost:9010', $ct).Wait(3000)) { throw 'нет соединения' }
    function Send($o) { $b = [Text.Encoding]::UTF8.GetBytes(($o | ConvertTo-Json -Compress)); $ws.SendAsync([ArraySegment[byte]]::new($b), 'Text', $true, $ct).Wait() }
    $buf = New-Object byte[] 262144
    function Recv($ms) {
        $res = @()
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $ms) {
            $ms2 = New-Object IO.MemoryStream
            do {
                $t = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), $ct)
                if (-not $t.Wait([Math]::Max(100, $ms - $sw.ElapsedMilliseconds))) { return $res }
                $ms2.Write($buf, 0, $t.Result.Count)
            } while (-not $t.Result.EndOfMessage)
            $res += [Text.Encoding]::UTF8.GetString($ms2.ToArray())
        }
        $res
    }
    Send @{ msgId = '1'; verb = 'GET'; path = '/devices/list' }
    Send @{ msgId = '2'; verb = 'SUBSCRIBE'; path = '/battery/state/changed' }
    $msgs = Recv 3000
    $ids = @()
    foreach ($m in $msgs) {
        Say ("  << {0}" -f ($(if ($m.Length -gt 3000) { $m.Substring(0, 3000) + '…' } else { $m })))
        try { $j = $m | ConvertFrom-Json; if ($j.path -eq '/devices/list') { $ids += $j.payload.deviceInfos | ForEach-Object { $_.id } } } catch {}
    }
    foreach ($id in $ids) { Send @{ msgId = "b$id"; verb = 'GET'; path = "/battery/$id/state" } }
    foreach ($m in (Recv 3000)) { Say ("  << {0}" -f $m) }
    $ws.Dispose()
} catch { Say "  ошибка: $_" }

$lines | Out-File -FilePath $out -Encoding utf8
Say "== записано в $out"
