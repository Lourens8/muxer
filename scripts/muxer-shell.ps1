# Resolve psmux bundled with Muxer.Server
$PsmuxPath = "C:\projects\muxer\src\Muxer.Server\psmux.exe"
$ServerUrl = "http://192.168.0.65:5199"

# Clear CLAUDECODE so psmux sessions can run Claude CLI
Remove-Item Env:CLAUDECODE -ErrorAction SilentlyContinue

while ($true) {
    try {
        $sessions = Invoke-RestMethod -Uri "$ServerUrl/api/sessions" -TimeoutSec 3
    } catch {
        Write-Host "  Muxer server is not running." -ForegroundColor Red
        Write-Host "  Dropping to shell." -ForegroundColor DarkGray
        powershell -NoLogo
        exit
    }

    if ($sessions.Count -eq 0) {
        Write-Host "  No active sessions." -ForegroundColor Yellow
        Write-Host "  Dropping to shell." -ForegroundColor DarkGray
        powershell -NoLogo
        exit
    }

    # Show picker (even for 1 session, so user can choose shell)
    Write-Host ""
    Write-Host "  Muxer Sessions" -ForegroundColor Cyan
    Write-Host "  ===============" -ForegroundColor Cyan
    Write-Host "  Detach: Ctrl+B, D" -ForegroundColor DarkGray
    Write-Host ""

    for ($i = 0; $i -lt $sessions.Count; $i++) {
        $s = $sessions[$i]
        $tag = if ($s.status -eq 1) { " [NEEDS APPROVAL]" } else { "" }
        Write-Host "  $($i + 1)) " -NoNewline -ForegroundColor DarkGray
        Write-Host "$($s.projectName)" -NoNewline
        if ($tag) { Write-Host $tag -NoNewline -ForegroundColor Red }
        Write-Host ""
    }

    Write-Host ""
    Write-Host "  Pick #, 's' for shell, 'q' to quit: " -NoNewline -ForegroundColor DarkGray
    $choice = Read-Host

    if ($choice -eq 'q') { exit }
    if ($choice -eq 's') { powershell -NoLogo; continue }

    if ([int]::TryParse($choice, [ref]$null)) {
        $idx = [int]$choice - 1
        if ($idx -ge 0 -and $idx -lt $sessions.Count) {
            $s = $sessions[$idx]
            Write-Host "  Attaching to $($s.projectName)..." -ForegroundColor Cyan
            & $PsmuxPath attach -t $s.psmuxSessionName
            # After detach (Ctrl+B, D), loop back to picker
            continue
        }
    }

    Write-Host "  Invalid selection." -ForegroundColor Red
}
