[CmdletBinding()]
param([ValidateSet('Internal Testing')][string]$FlightName)
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
$flightId = $null
if ($FlightName) {
    try {
        $matchingFlights = @()
        $skip = 0
        do {
            try {
                $page = Invoke-RestMethod -Method Get -Uri "https://manage.devcenter.microsoft.com/v1.0/my/applications/$env:MICROSOFT_STORE_PRODUCT_ID/listflights?top=100&skip=$skip" -Headers @{ Authorization = "Bearer $($token.access_token)" }
            }
            catch { throw 'Partner Center could not list package flights. Check application access and the package-flight configuration. No submission was changed.' }
            if ($null -eq $page.value) { throw 'Partner Center returned no package-flight list. No submission was changed.' }
            $matchingFlights += @($page.value | Where-Object { $_.friendlyName -ceq $FlightName })
            $count = @($page.value).Count
            if ($page.'@nextLink' -and $count -eq 0) { throw 'Partner Center returned an empty package-flight page with a continuation. No submission was changed.' }
            $skip += $count
        } while ($page.'@nextLink')
        if ($matchingFlights.Count -ne 1) { throw "Expected exactly one package flight named '$FlightName'; found $($matchingFlights.Count). No submission was changed." }
        $flightId = [string]$matchingFlights[0].flightId
        if ($flightId -notmatch '\A[a-zA-Z0-9][a-zA-Z0-9-]*\z') { throw 'Partner Center returned an invalid package-flight ID. No submission was changed.' }
    }
    catch {
        $token = $null
        throw
    }
}
try {
    Invoke-RestMethod -Method Get -Uri "https://manage.devcenter.microsoft.com/v1.0/my/applications/$env:MICROSOFT_STORE_PRODUCT_ID" -Headers @{ Authorization = "Bearer $($token.access_token)" } | Out-Null
}
catch { throw 'Partner Center could not read this application. Check the Store product ID and the Entra application association/role in Partner Center. No submission was created.' }
finally { $token = $null }
Write-Host 'Authentication and read-only access to the Store application verified. No draft or submission was changed.'
if ($FlightName) {
    Write-Host "Resolved the '$FlightName' package flight."
    $flightId
}
