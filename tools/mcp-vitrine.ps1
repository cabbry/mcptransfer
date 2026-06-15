# Drives `mcptx mcp-serve` over stdio JSON-RPC (as an MCP host would), to show
# the MCP tool surface working against live Amoy + Pinata. Receives the file the
# Mac sent earlier (read-only, no gas).
$ErrorActionPreference = 'Stop'
$dll   = "E:\Projects\MCPTransfer\src\MCPTransfer.Agent\bin\Release\net10.0\mcptx.dll"
$alice = Join-Path $env:USERPROFILE ".mcptx\alice-live.json"
$root  = Join-Path $env:TEMP "mcp-vitrine"
New-Item -ItemType Directory -Force $root | Out-Null
$outFile = Join-Path $root "mcp-received.bin"
if (Test-Path $outFile) { Remove-Item $outFile }
$cid = "QmNaVGkmcnupa2rJ2nf9HKQwCPQSFruvdxkeLHo6PNzTnP"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "`"$dll`" mcp-serve --identity `"$alice`""
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute = $false
$psi.EnvironmentVariables["MCPTX_MCP_ROOT"] = $root

$p = [System.Diagnostics.Process]::Start($psi)
$sb = New-Object System.Text.StringBuilder
Register-ObjectEvent -InputObject $p -EventName OutputDataReceived -Action {
  if ($EventArgs.Data) { [void]$Event.MessageData.AppendLine($EventArgs.Data) }
} -MessageData $sb | Out-Null
$p.BeginOutputReadLine()

function Send($obj) {
  $p.StandardInput.WriteLine(($obj | ConvertTo-Json -Compress -Depth 8))
  $p.StandardInput.Flush()
}

Send @{ jsonrpc="2.0"; id=1; method="initialize"; params=@{ protocolVersion="2024-11-05"; capabilities=@{}; clientInfo=@{ name="vitrine"; version="1.0" } } }
Start-Sleep -Milliseconds 2000
Send @{ jsonrpc="2.0"; method="notifications/initialized"; params=@{} }
Start-Sleep -Milliseconds 500
Send @{ jsonrpc="2.0"; id=2; method="tools/list"; params=@{} }
Start-Sleep -Milliseconds 1500
Send @{ jsonrpc="2.0"; id=3; method="tools/call"; params=@{ name="whoami"; arguments=@{} } }
Start-Sleep -Milliseconds 2000
Send @{ jsonrpc="2.0"; id=4; method="tools/call"; params=@{ name="receive_file"; arguments=@{ cid=$cid; outPath=$outFile } } }
Start-Sleep -Seconds 20

# Close stdin FIRST so the stdio server hits EOF and exits; only THEN read
# stderr to completion (reading it before close would deadlock — the stream
# stays open until the process exits, which waits on stdin EOF).
$p.StandardInput.Close()
if (-not $p.WaitForExit(8000)) { $p.Kill() }
Start-Sleep -Milliseconds 800
$stderr = $p.StandardError.ReadToEnd()

Write-Output "===== RAW STDOUT ====="
Write-Output $sb.ToString()
$stderr | Out-File -Encoding utf8 "$env:TEMP\mcp-vitrine-stderr.txt"
Write-Output "===== STDERR exception lines ====="
Write-Output (($stderr -split "`r?`n" | Where-Object { $_ -match "Exception|error|refus|workspace|root|Resolve|not found|InvalidOperation" } | Select-Object -First 20) -join "`n")
Write-Output "====================="

# ── parse + report ──
$lines = $sb.ToString() -split "`r?`n" | Where-Object { $_.Trim().StartsWith("{") }
foreach ($line in $lines) {
  try { $msg = $line | ConvertFrom-Json } catch { continue }
  if ($msg.id -eq 2 -and $msg.result.tools) {
    Write-Output ("TOOLS ({0}): {1}" -f $msg.result.tools.Count, (($msg.result.tools.name) -join ", "))
  }
  elseif ($msg.id -eq 3 -and $msg.result.content) {
    Write-Output "WHOAMI -> $($msg.result.content[0].text)"
  }
  elseif ($msg.id -eq 4 -and $msg.result.content) {
    Write-Output "RECEIVE_FILE -> $($msg.result.content[0].text)"
  }
  elseif ($msg.id -eq 4 -and $msg.error) {
    Write-Output "RECEIVE_FILE ERROR -> $($msg.error.message)"
  }
}
Write-Output "--- received file SHA256 (expect d87d94bd...696a0d) ---"
if (Test-Path $outFile) { (Get-FileHash $outFile -Algorithm SHA256).Hash.ToLower() } else { "NOT WRITTEN" }
Get-EventSubscriber | Unregister-Event
