[CmdletBinding()]
param(
    [int]$Pairs = 5,
    [Parameter(Mandatory = $true)][string]$ExeA,
    [Parameter(Mandatory = $true)][string]$ExeB,
    [string]$LabelA = 'A',
    [string]$LabelB = 'B',
    [string]$Teleloc = "0x2E430012 57.895 42.116 16.802 1 0 0 0",
    [int]$MinRenderedBytes = 500000
)

$ErrorActionPreference = 'Stop'
foreach ($exe in @($ExeA, $ExeB)) {
    if (-not (Test-Path $exe)) { throw "Client not found at $exe." }
}

# Never race a session someone is already playing: same account, same server.
if (Get-Process -Name AcDream.App -ErrorAction SilentlyContinue) {
    throw 'AcDream.App is already running. This probe uses the shared test account and must not steal its session.'
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AbProbeSurface {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$root = Join-Path $env:TEMP "claude\blank-world-ab-$([DateTime]::Now.ToString('HHmmss'))"
New-Item -ItemType Directory -Force -Path $root | Out-Null
$results = @()

for ($pair = 1; $pair -le $Pairs; $pair++) {
    foreach ($arm in @(
            @{ Label = $LabelA; Exe = $ExeA },
            @{ Label = $LabelB; Exe = $ExeB })) {

        $dir = Join-Path $root ("pair-{0}-{1}" -f $pair, $arm.Label)
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Set-Content -Encoding utf8 (Join-Path $dir 'probe.txt') -Value @"
wait world-ready 90000
command /teleloc $Teleloc
wait materialized 1 90000
wait world-visible 30000
sleep 15000
screenshot ab-run 30000
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
        $proc = Start-Process -FilePath $arm.Exe -RedirectStandardOutput $log `
            -RedirectStandardError "$log.err" -PassThru

        $shot = Join-Path $dir 'screenshots\ab-run.png'
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
                    [AbProbeSurface]::ShowWindow($h, 9) | Out-Null   # SW_RESTORE
                    [AbProbeSurface]::SetForegroundWindow($h) | Out-Null
                    Start-Sleep -Milliseconds 1200
                    $r = New-Object AbProbeSurface+RECT
                    [AbProbeSurface]::GetClientRect($h, [ref]$r) | Out-Null
                    $p = New-Object AbProbeSurface+POINT
                    [AbProbeSurface]::ClientToScreen($h, [ref]$p) | Out-Null
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
                Write-Host "[ab-probe] desktop grab failed: $_"
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
            Pair = $pair; Arm = $arm.Label; Verdict = $verdict; GrabBytes = $grabSize
            ClientCapture = $clientView; ClientBytes = $shotSize
        }
        Write-Host ("[ab-probe] pair {0}/{1} arm {2}: screen={3} ({4} B)  client={5} ({6} B)" -f `
            $pair, $Pairs, $arm.Label, $verdict, $grabSize, $clientView, $shotSize)

        Start-Sleep -Seconds 10
    }
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String | Write-Host
foreach ($label in @($LabelA, $LabelB)) {
    $arm = @($results | Where-Object Arm -eq $label)
    $bad = @($arm | Where-Object Verdict -ne 'RENDERED')
    Write-Host ("[ab-probe] {0}: {1}/{2} blank" -f $label, $bad.Count, $arm.Count)
}
Write-Host ("[ab-probe] artifacts: {0}" -f $root)
