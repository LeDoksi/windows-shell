# Аварийный возврат панели задач Windows. Не зависит от оболочки: запускается сам по себе.
# 1. Показывает панели задач на всех мониторах.
# 2. Возвращает прежнюю настройку автоскрытия, если оболочка её сохранила
#    (%LOCALAPPDATA%\WindowsShell\taskbar-state.txt). Без файла автоскрытие не трогает.
Add-Type @"
using System; using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint cb; public uint edge; public int l, t, r, b; public IntPtr lParam; }
public static class TB {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint m, ref APPBARDATA d);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@
$stateFile = Join-Path $env:LOCALAPPDATA "WindowsShell\taskbar-state.txt"
if (Test-Path $stateFile) {
  $autohide = (Get-Content $stateFile -Raw) -match 'autohide=1'
  $d = New-Object APPBARDATA; $d.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($d)
  $d.hWnd = [TB]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, "Shell_TrayWnd", $null)
  $d.lParam = [IntPtr]([int]$autohide)          # ABS_AUTOHIDE = 1, иначе 0
  [TB]::SHAppBarMessage(10, [ref]$d) | Out-Null   # ABM_SETSTATE
  $tray = (Get-Content $stateFile -Raw) -match 'tray=1'
  Remove-Item $stateFile
  "autohide restored to $([int]$autohide)"
  if ($tray) {
    # Our tray intercepted icons: ask apps to register them with Explorer again.
    [TB]::PostMessage([IntPtr]0xffff, [TB]::RegisterWindowMessage("TaskbarCreated"), [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    "TaskbarCreated broadcast"
  }
}
foreach ($class in "Shell_TrayWnd", "Shell_SecondaryTrayWnd") {
  $h = [IntPtr]::Zero
  while (($h = [TB]::FindWindowEx([IntPtr]::Zero, $h, $class, $null)) -ne [IntPtr]::Zero) {
    [TB]::ShowWindow($h, 5) | Out-Null            # SW_SHOW
  }
}
"taskbar shown"
