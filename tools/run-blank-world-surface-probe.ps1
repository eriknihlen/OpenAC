[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$Runs = 6,
    [string]$Teleloc = "0x2E430012 57.895 42.116 16.802 1 0 0 0",
    [int]$MinRenderedBytes = 500000
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) { $ExePath = Join-Path $repo 'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe' }
if (-not (Test-Path $ExePath)) { throw "Client not found at $ExePath." }

if (Get-Process -Name AcDream.App -ErrorAction SilentlyContinue) {
    throw 'AcDream.App is already running. This probe uses the shared test account and must not steal its session.'
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinSurface {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$root = Join-Path $env:TEMP "claude\blank-world-surface-$([DateTime]::Now.ToString('HHmmss'))"
New-Item -ItemType Directory -Force -Path $root | Out-Null
$results = @()

for ($i = 1; $i -le $Runs; $i++) {
    $dir = Join-Path $root "run-$i"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Set-Content -Encoding utf8 (Join-Path $dir 'probe.txt') -Value @"
wait world-ready 90000
command /teleloc $Teleloc
wait materialized 1 90000
wait world-visible 30000
sleep 15000
screenshot surface-run 30000
sleep 20000
"@

    $env:ACDREAM_DAT_DIR = Join-Path $env:USERPROFILE "Documents\Asheron's Call"
    $env:ACDREAM_LIVE = '1'
    $env:ACDREAM_TEST_HOST = '127.0.0.1'
    $env:ACDREAM_TEST_PORT = '9000'
    $env:ACDREAM_TEST_USER = 'testaccount'
    $env:ACDREAM_TEST_PASS = 'testpassword'
    $env:ACDREAM_RETAIL_UI = '1'
    $env:ACDREAM_UI_PROBE_SCRIPT = Join-Path $dir 'probe.txt'
    $env:ACDREAM_AUTOMATION_ARTIFACT_DIR = $dir

    $log = Join-Path $dir 'client.log'
    $proc = Start-Process -FilePath $ExePath -RedirectStandardOutput $log `
        -RedirectStandardError "$log.err" -PassThru

    $shot = Join-Path $dir 'screenshots\surface-run.png'
    $osShot = Join-Path $dir 'os-grab.png'
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $shot) { break }
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 500
    }

    $iconic = $null
    if (Test-Path $shot) {
        Start-Sleep -Milliseconds 800
        try {
            $proc.Refresh()
            $h = $proc.MainWindowHandle
            if ($h -ne [IntPtr]::Zero) {
                $iconic = [WinSurface]::IsIconic($h)
                [WinSurface]::ShowWindow($h, 9) | Out-Null   # SW_RESTORE
                [WinSurface]::SetForegroundWindow($h) | Out-Null
                Start-Sleep -Milliseconds 1200
                $r = New-Object WinSurface+RECT
                [WinSurface]::GetClientRect($h, [ref]$r) | Out-Null
                $p = New-Object WinSurface+POINT
                [WinSurface]::ClientToScreen($h, [ref]$p) | Out-Null
                $w = $r.R - $r.L; $ht = $r.B - $r.T
                if ($w -gt 0 -and $ht -gt 0) {
                    $bmp = New-Object System.Drawing.Bitmap $w, $ht
                    $g = [System.Drawing.Graphics]::FromImage($bmp)
                    $g.CopyFromScreen($p.X, $p.Y, 0, 0, (New-Object System.Drawing.Size $w, $ht))
                    $bmp.Save($osShot, [System.Drawing.Imaging.ImageFormat]::Png)
                    $g.Dispose(); $bmp.Dispose()
                }
            }
        } catch {
            Write-Host "[surface-probe] desktop grab failed: $_"
        }
    }

    $app = Get-Process -Name AcDream.App -ErrorAction SilentlyContinue
    if ($app) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(12000)) { $app | Stop-Process -Force }
    }

    $glSize = if (Test-Path $shot) { (Get-Item $shot).Length } else { 0 }
    $osSize = if (Test-Path $osShot) { (Get-Item $osShot).Length } else { 0 }
    $glVerdict = if ($glSize -ge $MinRenderedBytes) { 'RENDERED' } elseif ($glSize -gt 0) { 'BLANK' } else { 'NO-CAPTURE' }
    $osVerdict = if ($osSize -ge $MinRenderedBytes) { 'RENDERED' } elseif ($osSize -gt 0) { 'BLANK' } else { 'NO-CAPTURE' }
    $results += [pscustomobject]@{
        Run = $i; GlCapture = $glVerdict; GlBytes = $glSize
        ScreenGrab = $osVerdict; ScreenBytes = $osSize; WasIconic = $iconic
    }
    Write-Host ("[surface-probe] run {0}/{1}: gl={2} ({3} B)  screen={4} ({5} B)" -f `
        $i, $Runs, $glVerdict, $glSize, $osVerdict, $osSize)

    Start-Sleep -Seconds 10
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String | Write-Host
$split = @($results | Where-Object { $_.GlCapture -ne 'RENDERED' -and $_.ScreenGrab -eq 'RENDERED' })
Write-Host ("[surface-probe] frames where the GL capture blanked but the screen showed the world: {0}/{1}" -f `
    $split.Count, $results.Count)
Write-Host ("[surface-probe] artifacts: {0}" -f $root)
