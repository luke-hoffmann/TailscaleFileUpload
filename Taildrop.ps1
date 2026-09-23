Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public sealed class KillOnCloseJob : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)] struct BASIC_LIMITS {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct EXTENDED_LIMITS {
        public BASIC_LIMITS BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int type, IntPtr info, uint length);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    IntPtr handle;
    public KillOnCloseJob() {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var limits = new EXTENDED_LIMITS();
        limits.BasicLimitInformation.LimitFlags = 0x00002000;
        int size = Marshal.SizeOf(limits);
        IntPtr pointer = Marshal.AllocHGlobal(size);
        try {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!SetInformationJobObject(handle, 9, pointer, (uint)size)) throw new System.ComponentModel.Win32Exception();
        } finally { Marshal.FreeHGlobal(pointer); }
    }
    public void AddProcess(System.Diagnostics.Process process) {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception();
    }
    public void Dispose() {
        if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
        GC.SuppressFinalize(this);
    }
    ~KillOnCloseJob() { Dispose(); }
}
'@

$script:serverProcess = $null
$script:cleanupProcess = $null
$script:processJob = $null
$script:taildropUrl = $null
$script:inboxPath = $null
$script:port = 8787
$script:serverFile = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'server.mjs'))
$script:closing = $false

$createdNew = $false
$script:singleInstance = New-Object Threading.Mutex($true, 'Local\TaildropDesktopReceiver', [ref]$createdNew)
if (-not $createdNew) {
    [Windows.Forms.MessageBox]::Show('Taildrop is already open.', 'Taildrop', 'OK', 'Information') | Out-Null
    exit
}

function Find-Program([string]$name, [string[]]$fallbacks) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in $fallbacks) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    return $null
}

function Get-TailscaleIPv4 {
    $tailscaleFallbacks = @(
        (Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'),
        (Join-Path $env:LOCALAPPDATA 'Tailscale\tailscale.exe')
    )
    $tailscalePath = Find-Program 'tailscale.exe' $tailscaleFallbacks
    if ($tailscalePath) {
        try {
            $ip = (& $tailscalePath ip -4 2>$null | Select-Object -First 1).Trim()
            if ($ip -match '^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.\d{1,3}\.\d{1,3}$') { return $ip }
        } catch {}
    }
    foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($adapter.OperationalStatus -ne [Net.NetworkInformation.OperationalStatus]::Up) { continue }
        foreach ($item in $adapter.GetIPProperties().UnicastAddresses) {
            if ($item.Address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { continue }
            $bytes = $item.Address.GetAddressBytes()
            if ($bytes[0] -eq 100 -and $bytes[1] -ge 64 -and $bytes[1] -le 127) { return $item.Address.ToString() }
        }
    }
    return $null
}

function New-TemporaryInbox {
    $root = [IO.Path]::GetTempPath()
    $path = Join-Path $root ("Taildrop-" + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}

function Remove-TemporaryInbox {
    if (-not $script:inboxPath) { return }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $candidate = [IO.Path]::GetFullPath($script:inboxPath)
    if ($candidate.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        ([IO.Path]::GetFileName($candidate) -like 'Taildrop-*')) {
        try { [IO.Directory]::Delete($candidate, $true) } catch {}
    }
    $script:inboxPath = $null
}

function Start-CleanupWatcher([string]$nodePath) {
    $watcherFile = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'cleanup-worker.mjs'))
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $nodePath
    $startInfo.Arguments = "`"$watcherFile`" $PID `"$($script:inboxPath)`""
    $startInfo.WorkingDirectory = $PSScriptRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $script:cleanupProcess = New-Object Diagnostics.Process
    $script:cleanupProcess.StartInfo = $startInfo
    if (-not $script:cleanupProcess.Start()) { throw 'Could not start the temporary-file cleanup watcher.' }
}

function Remove-StaleServers {
    # Port 8787 belongs exclusively to this app. Older versions launched Node with
    # only "server.mjs", so a full-path process search alone cannot find them.
    Get-NetTCPConnection -LocalPort $script:port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        ForEach-Object {
            $owner = Get-CimInstance Win32_Process -Filter "ProcessId = $_" -ErrorAction SilentlyContinue
            if ($owner.Name -eq 'node.exe' -and $owner.CommandLine -match '(^|[\\/\s\"])(server\.mjs)([\s\"]|$)') {
                Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue
            }
        }
    $needle = [WildcardPattern]::Escape($script:serverFile)
    Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like "*$needle*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 150
}

function New-UiLabel([string]$text, [float]$size, [Drawing.FontStyle]$style, [Drawing.Color]$color) {
    $label = New-Object Windows.Forms.Label
    $label.Text = $text
    $label.Font = New-Object Drawing.Font('Segoe UI', $size, $style)
    $label.ForeColor = $color
    $label.BackColor = [Drawing.Color]::Transparent
    $label.AutoSize = $true
    return $label
}

function New-UiButton([string]$text, [int]$width, [Drawing.Color]$backColor, [Drawing.Color]$foreColor) {
    $button = New-Object Windows.Forms.Button
    $button.Text = $text
    $button.Size = New-Object Drawing.Size($width, 44)
    $button.FlatStyle = 'Flat'
    $button.FlatAppearance.BorderSize = 0
    $button.BackColor = $backColor
    $button.ForeColor = $foreColor
    $button.Font = New-Object Drawing.Font('Segoe UI Semibold', 10)
    $button.Cursor = [Windows.Forms.Cursors]::Hand
    return $button
}

$background = [Drawing.Color]::FromArgb(246, 247, 244)
$white = [Drawing.Color]::White
$ink = [Drawing.Color]::FromArgb(25, 28, 24)
$muted = [Drawing.Color]::FromArgb(102, 110, 100)
$green = [Drawing.Color]::FromArgb(30, 106, 61)
$greenPale = [Drawing.Color]::FromArgb(224, 244, 231)
$red = [Drawing.Color]::FromArgb(168, 57, 47)

$form = New-Object Windows.Forms.Form
$form.Text = 'Taildrop'
$form.ClientSize = New-Object Drawing.Size(620, 710)
$form.StartPosition = 'CenterScreen'
$form.BackColor = $background
$form.ForeColor = $ink
$form.Font = New-Object Drawing.Font('Segoe UI', 10)
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false

$title = New-UiLabel 'Taildrop' 22 'Bold' $ink
$title.Location = New-Object Drawing.Point(28, 24)
$form.Controls.Add($title)
$subtitle = New-UiLabel 'Phone to this computer, privately over Tailscale' 9.5 'Regular' $muted
$subtitle.Location = New-Object Drawing.Point(31, 62)
$form.Controls.Add($subtitle)

$statusPanel = New-Object Windows.Forms.Panel
$statusPanel.Location = New-Object Drawing.Point(28, 102)
$statusPanel.Size = New-Object Drawing.Size(564, 188)
$statusPanel.BackColor = $white
$form.Controls.Add($statusPanel)

$statusDot = New-Object Windows.Forms.Panel
$statusDot.Location = New-Object Drawing.Point(22, 24)
$statusDot.Size = New-Object Drawing.Size(10, 10)
$statusDot.BackColor = $muted
$statusPanel.Controls.Add($statusDot)
$statusLabel = New-UiLabel 'Starting...' 10 'Bold' $muted
$statusLabel.Location = New-Object Drawing.Point(42, 19)
$statusPanel.Controls.Add($statusLabel)
$promptLabel = New-UiLabel 'Scan the QR code with your phone' 17 'Bold' $ink
$promptLabel.Location = New-Object Drawing.Point(22, 62)
$statusPanel.Controls.Add($promptLabel)
$urlLabel = New-UiLabel 'Finding your Tailscale address...' 10 'Regular' $muted
$urlLabel.Location = New-Object Drawing.Point(24, 99)
$urlLabel.MaximumSize = New-Object Drawing.Size(515, 24)
$statusPanel.Controls.Add($urlLabel)

$qrButton = New-UiButton 'Show QR code' 146 $green $white
$qrButton.Location = New-Object Drawing.Point(22, 130)
$qrButton.Enabled = $false
$statusPanel.Controls.Add($qrButton)
$copyButton = New-UiButton 'Copy link' 110 $greenPale $green
$copyButton.Location = New-Object Drawing.Point(178, 130)
$copyButton.Enabled = $false
$statusPanel.Controls.Add($copyButton)

$inboxCaption = New-UiLabel 'TEMPORARY INBOX' 8 'Bold' $green
$inboxCaption.Location = New-Object Drawing.Point(31, 316)
$form.Controls.Add($inboxCaption)
$inboxHint = New-UiLabel 'Select files, then drag them into File Explorer' 9 'Regular' $muted
$inboxHint.Location = New-Object Drawing.Point(31, 338)
$form.Controls.Add($inboxHint)

$fileList = New-Object Windows.Forms.ListView
$fileList.Location = New-Object Drawing.Point(28, 370)
$fileList.Size = New-Object Drawing.Size(564, 248)
$fileList.View = 'Details'
$fileList.FullRowSelect = $true
$fileList.MultiSelect = $true
$fileList.HideSelection = $false
$fileList.GridLines = $false
$fileList.BorderStyle = 'FixedSingle'
$fileList.BackColor = $white
$fileList.ForeColor = $ink
$fileList.Font = New-Object Drawing.Font('Segoe UI', 9.5)
[void]$fileList.Columns.Add('Name', 326)
[void]$fileList.Columns.Add('Size', 92)
[void]$fileList.Columns.Add('Received', 122)
$form.Controls.Add($fileList)

$emptyLabel = New-UiLabel 'Files sent from your phone will appear here.' 10 'Regular' $muted
$emptyLabel.Location = New-Object Drawing.Point(169, 472)
$form.Controls.Add($emptyLabel)
$emptyLabel.BringToFront()

$saveSelectedButton = New-UiButton 'Save selected' 136 $green $white
$saveSelectedButton.Location = New-Object Drawing.Point(28, 634)
$saveSelectedButton.Enabled = $false
$form.Controls.Add($saveSelectedButton)
$saveAllButton = New-UiButton 'Save all' 108 $white $ink
$saveAllButton.Location = New-Object Drawing.Point(174, 634)
$saveAllButton.Enabled = $false
$form.Controls.Add($saveAllButton)
$temporaryLabel = New-UiLabel 'Closing Taildrop permanently deletes anything left here.' 8.5 'Regular' $muted
$temporaryLabel.Location = New-Object Drawing.Point(31, 687)
$form.Controls.Add($temporaryLabel)

function Set-ErrorState([string]$message) {
    $statusDot.BackColor = $red
    $statusLabel.Text = 'Not running'
    $statusLabel.ForeColor = $red
    $promptLabel.Text = 'Could not start Taildrop'
    $urlLabel.Text = $message
    $urlLabel.ForeColor = $red
    $qrButton.Enabled = $false
    $copyButton.Enabled = $false
}

function Stop-Taildrop {
    if ($script:serverProcess) {
        try {
            if (-not $script:serverProcess.HasExited) {
                $script:serverProcess.Kill()
                $script:serverProcess.WaitForExit(2000) | Out-Null
            }
        } catch {}
        try { $script:serverProcess.Dispose() } catch {}
    }
    $script:serverProcess = $null
    $script:taildropUrl = $null
    if ($script:inboxPath -and (Test-Path -LiteralPath $script:inboxPath)) {
        Get-ChildItem -LiteralPath $script:inboxPath -Filter '.taildrop-*.part' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

function Test-ServerReady([string]$url) {
    try {
        $result = Invoke-WebRequest -UseBasicParsing -Uri "$url/api/health" -TimeoutSec 1 -ErrorAction Stop
        return $result.StatusCode -eq 200
    } catch { return $false }
}

function Format-FileSize([long]$bytes) {
    if ($bytes -ge 1GB) { return ('{0:N1} GB' -f ($bytes / 1GB)) }
    if ($bytes -ge 1MB) { return ('{0:N1} MB' -f ($bytes / 1MB)) }
    if ($bytes -ge 1KB) { return ('{0:N0} KB' -f ($bytes / 1KB)) }
    return "$bytes B"
}

function Refresh-TemporaryInbox {
    if (-not $script:inboxPath -or -not (Test-Path -LiteralPath $script:inboxPath)) { return }
    $selected = @($fileList.SelectedItems | ForEach-Object { $_.Text })
    $files = @(Get-ChildItem -LiteralPath $script:inboxPath -File -ErrorAction SilentlyContinue |
        Where-Object { -not ($_.Name.StartsWith('.taildrop-') -and $_.Name.EndsWith('.part')) } |
        Sort-Object LastWriteTime -Descending)
    $currentSignature = @($fileList.Items | ForEach-Object { "$($_.Text)|$($_.SubItems[1].Text)|$($_.SubItems[2].Text)" }) -join "`n"
    $newSignature = @($files | ForEach-Object { "$($_.Name)|$(Format-FileSize $_.Length)|$($_.LastWriteTime.ToString('h:mm:ss tt'))" }) -join "`n"
    if ($currentSignature -ne $newSignature) {
        $fileList.BeginUpdate()
        $fileList.Items.Clear()
        foreach ($file in $files) {
            $item = New-Object Windows.Forms.ListViewItem($file.Name)
            [void]$item.SubItems.Add((Format-FileSize $file.Length))
            [void]$item.SubItems.Add($file.LastWriteTime.ToString('h:mm:ss tt'))
            $item.Tag = $file.FullName
            if ($selected -contains $file.Name) { $item.Selected = $true }
            [void]$fileList.Items.Add($item)
        }
        $fileList.EndUpdate()
    }
    $hasFiles = $files.Count -gt 0
    $emptyLabel.Visible = -not $hasFiles
    $saveAllButton.Enabled = $hasFiles
    $saveSelectedButton.Enabled = $fileList.SelectedItems.Count -gt 0
}

function Get-AvailableSavePath([string]$folder, [string]$name) {
    $candidate = Join-Path $folder $name
    if (-not [IO.File]::Exists($candidate)) { return $candidate }
    $extension = [IO.Path]::GetExtension($name)
    $stem = [IO.Path]::GetFileNameWithoutExtension($name)
    for ($i = 1; $i -lt 10000; $i++) {
        $candidate = Join-Path $folder ("$stem ($i)$extension")
        if (-not [IO.File]::Exists($candidate)) { return $candidate }
    }
    return Join-Path $folder ("$stem-$([guid]::NewGuid().ToString('N'))$extension")
}

function Save-InboxItems([Windows.Forms.ListViewItem[]]$items) {
    if (-not $items -or $items.Count -eq 0) { return }
    $picker = New-Object Windows.Forms.FolderBrowserDialog
    $picker.Description = 'Choose where these files should be saved'
    $picker.SelectedPath = [Environment]::GetFolderPath('MyDocuments')
    $picker.ShowNewFolderButton = $true
    if ($picker.ShowDialog($form) -eq 'OK') {
        $saved = 0
        foreach ($item in $items) {
            if ([IO.File]::Exists([string]$item.Tag)) {
                $destination = Get-AvailableSavePath $picker.SelectedPath $item.Text
                [IO.File]::Copy([string]$item.Tag, $destination, $false)
                $saved++
            }
        }
        [Windows.Forms.MessageBox]::Show("Saved $saved file$(if ($saved -eq 1) { '' } else { 's' }).", 'Taildrop', 'OK', 'Information') | Out-Null
    }
    $picker.Dispose()
}

function Start-Taildrop {
    Stop-Taildrop
    Remove-StaleServers
    $nodeFallbacks = @((Join-Path $env:ProgramFiles 'nodejs\node.exe'))
    if (${env:ProgramFiles(x86)}) { $nodeFallbacks += (Join-Path ${env:ProgramFiles(x86)} 'nodejs\node.exe') }
    $nodePath = Find-Program 'node.exe' $nodeFallbacks
    if (-not $nodePath) { Set-ErrorState 'Install Node.js 18 or newer, then reopen this app.'; return }
    $tailscaleIp = Get-TailscaleIPv4
    if (-not $tailscaleIp) { Set-ErrorState 'Connect Tailscale on this computer, then reopen this app.'; return }

    $script:taildropUrl = "http://${tailscaleIp}:$($script:port)"
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $nodePath
    $escapedServer = $script:serverFile.Replace('"', '\"')
    $startInfo.Arguments = "`"$escapedServer`""
    $startInfo.WorkingDirectory = $PSScriptRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.EnvironmentVariables['HOST'] = $tailscaleIp
    $startInfo.EnvironmentVariables['PORT'] = [string]$script:port
    $startInfo.EnvironmentVariables['INBOX_DIR'] = $script:inboxPath
    try {
        $script:serverProcess = New-Object Diagnostics.Process
        $script:serverProcess.StartInfo = $startInfo
        if (-not $script:serverProcess.Start()) { throw 'Node.js did not start.' }
        $script:processJob.AddProcess($script:serverProcess)
        Start-CleanupWatcher $nodePath
        $ready = $false
        for ($i = 0; $i -lt 30; $i++) {
            if ($script:serverProcess.HasExited) { break }
            if (Test-ServerReady $script:taildropUrl) { $ready = $true; break }
            Start-Sleep -Milliseconds 100
        }
        if (-not $ready) {
            if ($script:serverProcess.HasExited) { throw 'Port 8787 is busy or the receiver could not start.' }
            throw 'The receiver did not become ready in time.'
        }
    } catch {
        Stop-Taildrop
        Set-ErrorState $_.Exception.Message
        return
    }

    $statusDot.BackColor = $green
    $statusLabel.Text = 'Ready - keep this window open'
    $statusLabel.ForeColor = $green
    $promptLabel.Text = 'Scan the QR code with your phone'
    $urlLabel.Text = $script:taildropUrl
    $urlLabel.ForeColor = $muted
    $qrButton.Enabled = $true
    $copyButton.Enabled = $true
}

$copyTimer = New-Object Windows.Forms.Timer
$copyTimer.Interval = 1400
$copyTimer.Add_Tick({ $copyButton.Text = 'Copy link'; $copyTimer.Stop() })
$copyButton.Add_Click({
    if ($script:taildropUrl) {
        [Windows.Forms.Clipboard]::SetText($script:taildropUrl)
        $copyButton.Text = 'Copied'
        $copyTimer.Start()
    }
})

$qrButton.Add_Click({
    if (-not $script:taildropUrl) { return }
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "$($script:taildropUrl)/api/qr" -TimeoutSec 5 -ErrorAction Stop
        $data = $response.Content | ConvertFrom-Json
        [byte[]]$bytes = [Convert]::FromBase64String(($data.qr -replace '^data:image/png;base64,', ''))
        $stream = New-Object IO.MemoryStream(,$bytes)
        $sourceImage = [Drawing.Image]::FromStream($stream)
        $bitmap = New-Object Drawing.Bitmap($sourceImage)
        $sourceImage.Dispose()
        $stream.Dispose()

        $qrForm = New-Object Windows.Forms.Form
        $qrForm.Text = 'Scan to connect'
        $qrForm.ClientSize = New-Object Drawing.Size(430, 500)
        $qrForm.StartPosition = 'CenterParent'
        $qrForm.BackColor = $white
        $qrForm.FormBorderStyle = 'FixedDialog'
        $qrForm.MaximizeBox = $false
        $qrForm.MinimizeBox = $false
        $qrTitle = New-UiLabel 'Scan with your phone' 18 'Bold' $ink
        $qrTitle.Location = New-Object Drawing.Point(104, 20)
        $qrForm.Controls.Add($qrTitle)
        $picture = New-Object Windows.Forms.PictureBox
        $picture.Image = $bitmap
        $picture.SizeMode = 'Zoom'
        $picture.Size = New-Object Drawing.Size(360, 360)
        $picture.Location = New-Object Drawing.Point(35, 70)
        $qrForm.Controls.Add($picture)
        $hint = New-UiLabel 'Tailscale must be connected on your phone.' 9 'Regular' $muted
        $hint.Location = New-Object Drawing.Point(88, 453)
        $qrForm.Controls.Add($hint)
        $qrForm.Add_FormClosed({ $picture.Image.Dispose() })
        [void]$qrForm.ShowDialog($form)
        $qrForm.Dispose()
    } catch {
        [Windows.Forms.MessageBox]::Show("Could not create the QR code.`r`n$($_.Exception.Message)", 'Taildrop', 'OK', 'Error') | Out-Null
    }
})

$fileList.Add_SelectedIndexChanged({
    $saveSelectedButton.Enabled = $fileList.SelectedItems.Count -gt 0
})
$fileList.Add_ItemDrag({
    $paths = New-Object Collections.Specialized.StringCollection
    foreach ($item in $fileList.SelectedItems) {
        if ([IO.File]::Exists([string]$item.Tag)) { [void]$paths.Add([string]$item.Tag) }
    }
    if ($paths.Count -gt 0) {
        $data = New-Object Windows.Forms.DataObject
        $data.SetFileDropList($paths)
        [void]$fileList.DoDragDrop($data, [Windows.Forms.DragDropEffects]::Copy)
    }
})
$saveSelectedButton.Add_Click({ Save-InboxItems @($fileList.SelectedItems) })
$saveAllButton.Add_Click({ Save-InboxItems @($fileList.Items) })

$inboxTimer = New-Object Windows.Forms.Timer
$inboxTimer.Interval = 400
$inboxTimer.Add_Tick({ Refresh-TemporaryInbox })

$form.Add_Shown({
    try {
        $script:processJob = New-Object KillOnCloseJob
        $script:inboxPath = New-TemporaryInbox
        Start-Taildrop
        Refresh-TemporaryInbox
        $inboxTimer.Start()
    } catch { Set-ErrorState $_.Exception.Message }
})

$form.Add_FormClosing({
    if ($script:closing) { return }
    $script:closing = $true
    $inboxTimer.Stop()
    Stop-Taildrop
    Remove-StaleServers
    if ($script:processJob) { $script:processJob.Dispose(); $script:processJob = $null }
    Remove-TemporaryInbox
})

try { [void]$form.ShowDialog() }
finally {
    $inboxTimer.Stop()
    Stop-Taildrop
    if ($script:processJob) { $script:processJob.Dispose() }
    Remove-TemporaryInbox
    if ($script:cleanupProcess) { $script:cleanupProcess.Dispose(); $script:cleanupProcess = $null }
    if ($script:singleInstance) { $script:singleInstance.ReleaseMutex(); $script:singleInstance.Dispose() }
    $form.Dispose()
}
