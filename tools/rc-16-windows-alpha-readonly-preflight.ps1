# RC-16 Windows LAB Alpha bot membership READ-ONLY preflight.
# Uses DPAPI ciphertext created by RC-15 and never prints the token.
# Does not call getUpdates, acknowledge offsets, or read group messages.
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedBot = 'residentchat_od_lab_bot'
$expectedAlphaId = [long]-5512397750
$folder = Join-Path (Join-Path $env:LOCALAPPDATA 'ResidentChat') 'Secrets'
$tokenFile = Join-Path $folder 'telegram-lab-token.dpapi'
$alphaFile = Join-Path $folder 'alpha-chat-id.txt'
$pointer = [IntPtr]::Zero
$secure = $null
$token = $null
try {
    if ($env:OS -ne 'Windows_NT' -or
        -not (Test-Path -LiteralPath $tokenFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $alphaFile -PathType Leaf)) {
        throw 'LOCAL_VAULT_ABSENT'
    }
    $chatId = [long]::Parse(
        ([System.IO.File]::ReadAllText($alphaFile)).Trim(),
        [System.Globalization.CultureInfo]::InvariantCulture)
    if ($chatId -ne $expectedAlphaId) {
        throw 'ALPHA_ID_MISMATCH'
    }
    $secure = ConvertTo-SecureString -String ([System.IO.File]::ReadAllText($tokenFile))
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ($token -notmatch '^[0-9]{6,16}:[A-Za-z0-9_-]{20,}$') {
        throw 'INVALID_BOT_TOKEN_FORMAT'
    }
    # Bot API requires a token in the URL. Suppress all exception details
    # because they may contain this URL and its credential.
    $base = 'https://api.telegram.org/bot' + $token

    $me = Invoke-RestMethod -Method Post -Uri ($base + '/getMe') -TimeoutSec 15
    if (-not $me.ok -or -not $me.result.is_bot -or
        $me.result.username -cne $expectedBot) {
        throw 'WRONG_BOT'
    }
    Write-Host 'RC16_BOT_IDENTITY_OK' -ForegroundColor Green

    $hook = Invoke-RestMethod -Method Post -Uri ($base + '/getWebhookInfo') -TimeoutSec 15
    if (-not $hook.ok -or -not [string]::IsNullOrEmpty($hook.result.url)) {
        throw 'WEBHOOK_CONFLICT'
    }
    Write-Host 'RC16_WEBHOOK=NONE' -ForegroundColor Green

    if ($me.result.can_read_all_group_messages -ne $true) {
        throw 'BOT_PRIVACY_DISABLED_REQUIRED'
    }
    Write-Host 'RC16_GROUP_PRIVACY_DISABLED=YES' -ForegroundColor Green

    $chat = Invoke-RestMethod -Method Post -Uri ($base + '/getChat') -Body @{chat_id=$chatId} -TimeoutSec 15
    if (-not $chat.ok -or [long]$chat.result.id -ne $expectedAlphaId -or
        $chat.result.type -notin @('group','supergroup')) {
        throw 'ALPHA_CHAT_NOT_FOUND'
    }
    Write-Host 'RC16_ALPHA_CHAT_ID_MATCH=YES' -ForegroundColor Green
    Write-Host ('RC16_ALPHA_CHAT_TYPE=' + $chat.result.type)

    # Self-membership check; no administrator privileges required.
    $member = Invoke-RestMethod -Method Post -Uri ($base + '/getChatMember') -Body @{chat_id=$chatId;user_id=[long]$me.result.id} -TimeoutSec 15
    if (-not $member.ok) {
        throw 'MEMBERSHIP_API_FAILED'
    }
    $status = [string]$member.result.status
    if ($status -notin @('member','administrator','creator','restricted') -or
        ($status -eq 'restricted' -and $member.result.is_member -ne $true)) {
        throw 'BOT_NOT_MEMBER'
    }
    Write-Host ('RC16_ALPHA_BOT_MEMBER_STATUS=' + $status) -ForegroundColor Green
    if ($status -in @('administrator','creator')) {
        Write-Host 'RC16_ADMIN_RIGHTS_PRESENT=YES' -ForegroundColor Yellow
    } else {
        Write-Host 'RC16_ADMIN_RIGHTS_PRESENT=NO'
    }
    Write-Host 'RC16_TELEGRAM_ALPHA_READONLY_PASS' -ForegroundColor Green
    Write-Host 'RC16_UPDATES_ACKED=NO'
    Write-Host 'RC16_MESSAGES_READ=NO'
}
catch {
    Write-Host 'RC16_TELEGRAM_ALPHA_READONLY_FAIL' -ForegroundColor Red
    throw 'RC16_READONLY_PREFLIGHT_FAILED'
}
finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    Remove-Variable token, secure, base, me, hook, chat, member, status -ErrorAction SilentlyContinue
}
