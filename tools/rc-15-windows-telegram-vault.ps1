# RC-15: persist only the LAB bot token as a Windows DPAPI user-bound
# ciphertext in LOCALAPPDATA. Never print it or commit it. No external AI.
# Windows PowerShell 5.1 compatible. Run only on the owner's Windows PC.
param(
    [ValidateSet('Save', 'Verify', 'Status')]
    [string]$Mode = 'Save'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedBot = 'residentchat_od_lab_bot'
$vaultDirectory = Join-Path (Join-Path $env:LOCALAPPDATA 'ResidentChat') 'Secrets'
$vaultFile = Join-Path $vaultDirectory 'telegram-lab-token.dpapi'
$alphaFile = Join-Path $vaultDirectory 'alpha-chat-id.txt'
$alphaId = '-5512397750'
$secure = $null
$plain = $null
$pointer = [IntPtr]::Zero
$tempFile = $null

try {
    if ($env:OS -ne 'Windows_NT') {
        throw 'WINDOWS_ONLY'
    }

    if ($Mode -eq 'Status') {
        if (Test-Path -LiteralPath $vaultFile -PathType Leaf) {
            Write-Host 'RC15_TOKEN_STORED_ENCRYPTED=YES' -ForegroundColor Green
            Write-Host 'RC15_STORAGE=WINDOWS_USER_DPAPI'
        } else {
            Write-Host 'RC15_TOKEN_STORED_ENCRYPTED=NO' -ForegroundColor Yellow
        }
        return
    }

    if ($Mode -eq 'Save') {
        # Secure prompt never transmits the token to GitHub or ChatGPT.
        $secure = Read-Host 'Telegram LAB bot token (hidden)' -AsSecureString
    } else {
        if (-not (Test-Path -LiteralPath $vaultFile -PathType Leaf)) {
            throw 'VAULT_FILE_NOT_FOUND'
        }
        $ciphertext = [System.IO.File]::ReadAllText($vaultFile)
        $secure = ConvertTo-SecureString -String $ciphertext
    }

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ([string]::IsNullOrWhiteSpace($plain) -or
        $plain -notmatch '^[0-9]{6,16}:[A-Za-z0-9_-]{20,}$') {
        throw 'INVALID_TOKEN_FORMAT'
    }

    # Bot API inherently requires a bot token in the request URL.
    # Neither URL, HTTP exceptions, token nor raw API response is printed.
    $api = 'https://api.telegram.org/bot' + $plain
    $me = Invoke-RestMethod -Method Post -Uri ($api + '/getMe') -TimeoutSec 15
    if (-not $me.ok -or -not $me.result.is_bot -or
        $me.result.username -cne $expectedBot) {
        throw 'BOT_IDENTITY_MISMATCH'
    }
    $hook = Invoke-RestMethod -Method Post -Uri ($api + '/getWebhookInfo') -TimeoutSec 15
    if (-not $hook.ok) { throw 'WEBHOOK_INFO_UNAVAILABLE' }

    # Fail closed: never store a token if it points to a different bot or
    # if existing webhook would prevent exclusive local long polling.
    if (-not [string]::IsNullOrEmpty($hook.result.url)) {
        throw 'WEBHOOK_ALREADY_CONFIGURED'
    }

    if ($Mode -eq 'Save') {
        # On Windows, ConvertFrom-SecureString without -Key uses current
        # Windows user's DPAPI credentials. The token file is NOT plaintext.
        [void][System.IO.Directory]::CreateDirectory($vaultDirectory)
        $ciphertext = ConvertFrom-SecureString -SecureString $secure
        $tempFile = Join-Path $vaultDirectory ([guid]::NewGuid().ToString('N') + '.tmp')
        [System.IO.File]::WriteAllText(
            $tempFile, $ciphertext, [System.Text.Encoding]::ASCII)
        Move-Item -LiteralPath $tempFile -Destination $vaultFile -Force
        $tempFile = $null
        [System.IO.File]::WriteAllText(
            $alphaFile, $alphaId, [System.Text.Encoding]::ASCII)
        Write-Host 'RC15_TOKEN_STORED_ENCRYPTED=YES' -ForegroundColor Green
        Write-Host 'RC15_ALPHA_CHAT_ID_STORED_LOCALLY=YES'
    } else {
        Write-Host 'RC15_SAVED_TOKEN_REUSED=YES' -ForegroundColor Green
    }
    Write-Host 'RC15_BOT_IDENTITY_OK' -ForegroundColor Green
    Write-Host ('RC15_GROUP_PRIVACY_DISABLED=' + $(if ($me.result.can_read_all_group_messages) {'YES'} else {'NO'}))
    Write-Host 'RC15_WEBHOOK=NONE'
    Write-Host 'RC15_PREFLIGHT_PASS' -ForegroundColor Green
}
catch {
    # Intentionally do not print exception Message/URI/stack: Bot API URLs
    # contain credentials; errors may expose them.
    Write-Host 'RC15_VAULT_OR_PREFLIGHT_FAILED' -ForegroundColor Red
    throw 'RC15_CHECK_FAILED'
}
finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    if ($null -ne $tempFile -and (Test-Path -LiteralPath $tempFile)) {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
    Remove-Variable secure, plain, api, me, hook, ciphertext -ErrorAction SilentlyContinue
}
