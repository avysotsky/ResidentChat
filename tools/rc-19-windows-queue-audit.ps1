# RC-19 - Telegram pending queue READ ONLY audit on Windows.
# Uses DPAPI token; never prints token, message text, chat ID or update ID.
# Does not ACK Telegram updates or run WSL, database, local AI or bot sends.
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedBot = 'residentchat_od_lab_bot'
$alpha = [long]-5512397750
$probe = 'RC_ALPHA_PROBE_20261023'
$water = -join ([char[]]@(0x423, 0x20, 0x43f, 0x456, 0x434, 0x432, 0x430, 0x43b, 0x456, 0x20, 0x442, 0x435, 0x447, 0x435, 0x20, 0x432, 0x43e, 0x434, 0x430, 0x20, 0x437, 0x20, 0x442, 0x440, 0x443, 0x431, 0x438))
$folder = Join-Path (Join-Path $env:LOCALAPPDATA 'ResidentChat') 'Secrets'
$tokenPath = Join-Path $folder 'telegram-lab-token.dpapi'
$idPath = Join-Path $folder 'alpha-chat-id.txt'
$bstr = [IntPtr]::Zero
$secure = $null
$token = $null
try {
    if ($env:OS -ne 'Windows_NT' -or
        -not (Test-Path -LiteralPath $tokenPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $idPath -PathType Leaf)) {
        throw 'WINDOWS_USER_VAULT_ABSENT'
    }
    $saved = [long]::Parse(([IO.File]::ReadAllText($idPath)).Trim(),
        [Globalization.CultureInfo]::InvariantCulture)
    if ($saved -ne $alpha) { throw 'ALPHA_ID_MISMATCH' }
    $secure = ConvertTo-SecureString -String ([IO.File]::ReadAllText($tokenPath))
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    if ($token -notmatch '^[0-9]{6,16}:[A-Za-z0-9_-]{20,}$') {
        throw 'TOKEN_INVALID'
    }
    # Token is part of Telegram API URL. Never print URI/HTTP exception.
    $base = 'https://api.telegram.org/bot' + $token
    $me = Invoke-RestMethod -Method Post -Uri ($base + '/getMe') -TimeoutSec 15
    if (-not $me.ok -or -not $me.result.is_bot -or
        $me.result.username -cne $expectedBot -or
        $me.result.can_read_all_group_messages -ne $true) {
        throw 'BOT_IDENTITY_OR_PRIVACY_INVALID'
    }
    $hook = Invoke-RestMethod -Method Post -Uri ($base + '/getWebhookInfo') -TimeoutSec 15
    if (-not $hook.ok -or -not [string]::IsNullOrEmpty($hook.result.url)) {
        throw 'WEBHOOK_CONFLICT'
    }
    $member = Invoke-RestMethod -Method Post -Uri ($base + '/getChatMember') -Body @{chat_id=$alpha;user_id=[long]$me.result.id} -TimeoutSec 15
    if (-not $member.ok -or
        $member.result.status -notin @('member','administrator','creator','restricted') -or
        ($member.result.status -eq 'restricted' -and $member.result.is_member -ne $true)) {
        throw 'ALPHA_MEMBERSHIP_INVALID'
    }
    Write-Host 'RC19_BOT_AND_ALPHA_ACCESS_PASS' -ForegroundColor Green
    Write-Host 'RC19_WEBHOOK=NONE'
    # No offset => no acknowledgement. No allowed_updates => no changing
    # Telegram's persisted filter for future update deliveries.
    $response = Invoke-RestMethod -Method Post -Uri ($base + '/getUpdates') -ContentType 'application/json' -Body '{"limit":100,"timeout":0}' -TimeoutSec 20
    if (-not $response.ok -or $null -eq $response.result) {
        throw 'TELEGRAM_RESPONSE_INVALID'
    }
    $items = @($response.result)
    $counts = @{
        scanned=0; probe=0; water=0; alpha_other=0;
        foreign=0; unsupported=0; malformed=0; duplicate=0
    }
    $seen = [Collections.Generic.HashSet[long]]::new()
    foreach ($u in $items) {
        $counts['scanned']++
        if ($null -eq $u) { $counts['malformed']++; continue }
        $idProperty = $u.PSObject.Properties['update_id']
        $uid = [long]0
        if ($null -eq $idProperty -or
            -not [long]::TryParse([string]$idProperty.Value, [ref]$uid) -or
            $uid -le 0 -or $uid -eq [long]::MaxValue) {
            $counts['malformed']++
            continue
        }
        if (-not $seen.Add($uid)) { $counts['duplicate']++; continue }
        $msgProperty = $u.PSObject.Properties['message']
        if ($null -eq $msgProperty -or $null -eq $msgProperty.Value) {
            $counts['unsupported']++
            continue
        }
        $msg = $msgProperty.Value
        $chatProperty = $msg.PSObject.Properties['chat']
        if ($null -eq $chatProperty -or $null -eq $chatProperty.Value) {
            $counts['malformed']++
            continue
        }
        $chat = $chatProperty.Value
        $chatIdProperty = $chat.PSObject.Properties['id']
        $chatTypeProperty = $chat.PSObject.Properties['type']
        $chatId = [long]0
        if ($null -eq $chatIdProperty -or $null -eq $chatTypeProperty -or
            -not [long]::TryParse([string]$chatIdProperty.Value, [ref]$chatId)) {
            $counts['malformed']++
            continue
        }
        if ($chatId -ne $alpha -or $chatTypeProperty.Value -ne 'group') {
            $counts['foreign']++
            continue
        }
        $textProperty = $msg.PSObject.Properties['text']
        if ($null -eq $textProperty -or $null -eq $textProperty.Value) {
            $counts['alpha_other']++
        }
        elseif ($textProperty.Value -ceq $probe) {
            $counts['probe']++
        }
        elseif ($textProperty.Value -ceq $water) {
            $counts['water']++
        }
        else {
            $counts['alpha_other']++
        }
    }
    $capped = $counts['scanned'] -ge 100
    $blocked = $capped -or $counts['alpha_other'] -ne 0 -or
        $counts['foreign'] -ne 0 -or $counts['unsupported'] -ne 0 -or
        $counts['malformed'] -ne 0 -or $counts['duplicate'] -ne 0
    Write-Host ('RC19_UPDATES_SCANNED=' + $counts['scanned'])
    Write-Host ('RC19_ALPHA_PROBE_MATCHES=' + $counts['probe'])
    Write-Host ('RC19_ALPHA_WATER_MATCHES=' + $counts['water'])
    Write-Host ('RC19_ALPHA_OTHER_MESSAGES=' + $counts['alpha_other'])
    Write-Host ('RC19_FOREIGN_GROUP_UPDATES=' + $counts['foreign'])
    Write-Host ('RC19_UNSUPPORTED_EVENTS=' + $counts['unsupported'])
    Write-Host ('RC19_MALFORMED_UPDATES=' + $counts['malformed'])
    Write-Host ('RC19_DUPLICATE_UPDATE_IDS=' + $counts['duplicate'])
    Write-Host ('RC19_BATCH_CAPPED_100=' + $(if ($capped) {'YES'} else {'NO'}))
    if ($blocked) {
        Write-Host 'RC19_QUEUE_CONTENT_STATUS=UNCLASSIFIED_PRESENT' -ForegroundColor Yellow
    }
    elseif ($counts['scanned'] -eq 0) {
        Write-Host 'RC19_QUEUE_CONTENT_STATUS=EMPTY' -ForegroundColor Yellow
    }
    else {
        Write-Host 'RC19_QUEUE_CONTENT_STATUS=KNOWN_FIXTURES_ONLY' -ForegroundColor Green
    }
    # PASS certifies only the read-only inventory, NOT permission to ACK.
    Write-Host 'RC19_ACK_AUTHORIZED=NO'
    Write-Host 'RC19_UPDATES_ACKED=NO'
    Write-Host 'RC19_MESSAGE_TEXT_OR_CHAT_IDS_LOGGED=NO'
    Write-Host 'RC19_READONLY_QUEUE_AUDIT_PASS' -ForegroundColor Green
}
catch {
    # Never print exception details: they may contain token-bearing URI.
    Write-Host 'RC19_READONLY_QUEUE_AUDIT_FAILED' -ForegroundColor Red
    throw 'RC19_SAFE_AUDIT_ERROR'
}
finally {
    if ($bstr -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    Remove-Variable token, secure, base, me, hook, member, response, items -ErrorAction SilentlyContinue
}
