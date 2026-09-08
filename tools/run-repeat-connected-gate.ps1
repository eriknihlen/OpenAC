[CmdletBinding()]
param(
    [int]$Runs = 10,
    [string]$ExePath,
    [string]$Teleloc = "0x2E430012 57.895 42.116 16.802 1 0 0 0",
    [int]$MinRenderedBytes = 500000
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) { $ExePath = Join-Path $repo 'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe' }
if (-not (Test-Path $ExePath)) { throw "Client not found at $ExePath." }

# Never race a session someone is already playing: same account, same server.
if (Get-Process -Name AcDream.App -ErrorAction SilentlyContinue) {
    throw 'AcDream.App is already running. This gate uses the shared test account and must not steal its session.'
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RepeatGateSurface {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$root = Join-Path $env:TEMP "claude\repeat-connected-$([DateTime]::Now.ToString('HHmmss'))"
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
screenshot repeat-run 30000
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
    Remove-Item Env:\ACDREAM_RENDER_BACKEND -ErrorAction SilentlyContinue

    $log = Join-Path $dir 'client.log'
    $proc = Start-Process -FilePath $ExePath -RedirectStandardOutput $log `
        -RedirectStandardError "$log.err" -PassThru

    $shot = Join-Path $dir 'screenshots\repeat-run.png'
    $grab = Join-Path $dir 'desktop-grab.png'
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $shot) { break }
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 500
    }

    if (Test-Path $shot) {
        Start-Sleep -Milliseconds 800
        try {
            $proc.Refresh()
            $h = $proc.MainWindowHandle
            if ($h -ne [IntPtr]::Zero) {
                [RepeatGateSurface]::ShowWindow($h, 9) | Out-Null   # SW_RESTORE
                [RepeatGateSurface]::SetForegroundWindow($h) | Out-Null
                Start-Sleep -Milliseconds 1200
                $r = New-Object RepeatGateSurface+RECT
                [RepeatGateSurface]::GetClientRect($h, [ref]$r) | Out-Null
                $p = New-Object RepeatGateSurface+POINT
                [RepeatGateSurface]::ClientToScreen($h, [ref]$p) | Out-Null
                $w = $r.R - $r.L; $ht = $r.B - $r.T
                if ($w -gt 0 -and $ht -gt 0) {
                    $bmp = New-Object System.Drawing.Bitmap $w, $ht
                    $g = [System.Drawing.Graphics]::FromImage($bmp)
                    $g.CopyFromScreen($p.X, $p.Y, 0, 0, (New-Object System.Drawing.Size $w, $ht))
                    $bmp.Save($grab, [System.Drawing.Imaging.ImageFormat]::Png)
                    $g.Dispose(); $bmp.Dispose()
                }
            }
        } catch {
            Write-Host "[repeat-gate] desktop grab failed: $_"
        }
    }

    $app = Get-Process -Name AcDream.App -ErrorAction SilentlyContinue
    if ($app) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(12000)) { $app | Stop-Process -Force }
    }

    $grabSize = if (Test-Path $grab) { (Get-Item $grab).Length } else { 0 }
    $shotSize = if (Test-Path $shot) { (Get-Item $shot).Length } else { 0 }
    $verdict = if ($grabSize -ge $MinRenderedBytes) { 'RENDERED' }
               elseif ($grabSize -gt 0) { 'BLANK' }
               else { 'NO-CAPTURE' }
    $clientView = if ($shotSize -ge $MinRenderedBytes) { 'RENDERED' }
                  elseif ($shotSize -gt 0) { 'BLANK' }
                  else { 'NO-CAPTURE' }
    $results += [pscustomobject]@{
        Run = $i; Verdict = $verdict; GrabBytes = $grabSize
        ClientCapture = $clientView; ClientBytes = $shotSize
    }
    Write-Host ("[repeat-gate] run {0}/{1}: screen={2} ({3} B)  client={4} ({5} B)" -f `
        $i, $Runs, $verdict, $grabSize, $clientView, $shotSize)

    Start-Sleep -Seconds 10
}

Write-Host ''
$blank = @($results | Where-Object Verdict -ne 'RENDERED')
$results | Format-Table -AutoSize | Out-String | Write-Host
$split = @($results | Where-Object { $_.Verdict -ne $_.ClientCapture })
if ($split.Count -gt 0) {
    Write-Host ("[repeat-gate] note: {0}/{1} runs had the two instruments disagree." -f `
        $split.Count, $Runs)
}
if ($blank.Count -gt 0) {
    Write-Host ("[repeat-gate] FAILED: {0}/{1} runs did not render on the desktop witness. Artifacts: {2}" -f $blank.Count, $Runs, $root) -ForegroundColor Red
    exit 1
}
Write-Host ("[repeat-gate] PASS: {0}/{0} runs rendered on the desktop witness. Artifacts: {1}" -f $Runs, $root)
exit 0
