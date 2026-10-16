$ErrorActionPreference = 'Stop'
$tenant = $env:PARTNER_CENTER_TENANT_ID
$client = $env:PARTNER_CENTER_CLIENT_ID
foreach ($name in @('PARTNER_CENTER_TENANT_ID', 'PARTNER_CENTER_SELLER_ID', 'PARTNER_CENTER_CLIENT_ID', 'PARTNER_CENTER_CLIENT_SECRET', 'MICROSOFT_STORE_PRODUCT_ID')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Missing Store configuration: $name" }
}
if ($tenant -notmatch '\A[0-9a-fA-F-]{36}\z' -or $client -notmatch '\A[0-9a-fA-F-]{36}\z' -or $env:PARTNER_CENTER_SELLER_ID -notmatch '\A\d+\z' -or $env:MICROSOFT_STORE_PRODUCT_ID -notmatch '\A[a-zA-Z0-9]+\z') {
    throw 'Invalid Store tenant/client/seller/product identifier format.'
}
try {
    $token = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/token" -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type = 'client_credentials'; client_id = $client; client_secret = $env:PARTNER_CENTER_CLIENT_SECRET
        resource = 'https://manage.devcenter.microsoft.com'
    }
}
catch { throw 'Microsoft Entra authentication failed. Check the tenant, client ID, secret value and expiry. No credentials or token response were logged.' }
if (-not $token.access_token) { throw 'Microsoft Entra returned no access token.' }
try {
    Invoke-RestMethod -Method Get -Uri "https://manage.devcenter.microsoft.com/v1.0/my/applications/$env:MICROSOFT_STORE_PRODUCT_ID" -Headers @{ Authorization = "Bearer $($token.access_token)" } | Out-Null
}
catch { throw 'Partner Center could not read this application. Check the Store product ID and the Entra application association/role in Partner Center. No submission was created.' }
finally { $token = $null }
Write-Host 'Authentication and read-only access to the Store application verified. No draft or submission was changed.'
