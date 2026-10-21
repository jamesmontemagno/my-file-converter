[CmdletBinding()]
param(
    [ValidateSet('Internal Testing')][string]$FlightName,
    [switch]$PrepareFlight,
    [switch]$ReplaceFlightDraft
)
$ErrorActionPreference = 'Stop'
if (($PrepareFlight -and -not $FlightName) -or ($ReplaceFlightDraft -and -not $PrepareFlight)) {
    throw 'Flight preparation requires -FlightName; draft replacement also requires -PrepareFlight.'
}
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
$applicationUri = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$env:MICROSOFT_STORE_PRODUCT_ID"
$headers = @{ Authorization = ('Bearer ' + $token.access_token) }
try {
    try {
        Invoke-RestMethod -Method Get -Uri $applicationUri -Headers $headers | Out-Null
    }
    catch { throw 'Partner Center could not read this application. Check the Store product ID and the Entra application association/role in Partner Center. No submission was created.' }
    if ($FlightName) {
        $matchingFlights = @()
        $skip = 0
        do {
            try {
                $page = Invoke-RestMethod -Method Get -Uri "$applicationUri/listflights?top=100&skip=$skip" -Headers $headers
            }
            catch { throw 'Partner Center could not list package flights. Check application access and the package-flight configuration. No submission was changed.' }
            if ($null -eq $page.value) { throw 'Partner Center returned no package-flight list. No submission was changed.' }
            $matchingFlights += @($page.value | Where-Object { $_.friendlyName -ceq $FlightName })
            $count = @($page.value).Count
            if ($page.'@nextLink' -and $count -eq 0) { throw 'Partner Center returned an empty package-flight page with a continuation. No submission was changed.' }
            $skip += $count
        } while ($page.'@nextLink')
        if ($matchingFlights.Count -eq 0 -and $PrepareFlight) {
            $groupIds = @($env:MICROSOFT_STORE_FLIGHT_GROUP_IDS -split ',' | ForEach-Object { $_.Trim() } | Select-Object -Unique)
            if ($groupIds.Count -eq 0 -or @($groupIds | Where-Object { $_ -notmatch '\A[a-zA-Z0-9][a-zA-Z0-9-]*\z' }).Count) {
                throw 'Internal Testing is missing. Set MICROSOFT_STORE_FLIGHT_GROUP_IDS to comma-separated Partner Center tester group IDs so the approved publish job can create it through the API. Do not create a draft in the portal.'
            }
            try {
                $created = Invoke-RestMethod -Method Post -Uri "$applicationUri/flights" -Headers $headers -ContentType 'application/json' -Body (@{
                    friendlyName = $FlightName; groupIds = $groupIds
                } | ConvertTo-Json -Depth 5 -Compress)
            }
            catch { throw 'Partner Center could not create Internal Testing. Check tester group IDs and inspect flights before retrying; the request may have reached the service.' }
            if ($created.friendlyName -cne $FlightName) { throw 'Partner Center returned an unexpected flight after creation. Inspect flights before retrying.' }
            $matchingFlights = @($created)
            Write-Host 'Created Internal Testing through the Store API with the configured tester groups.'
        }
        if ($matchingFlights.Count -ne 1) { throw "Expected exactly one package flight named '$FlightName'; found $($matchingFlights.Count). No submission was changed. If missing, use an approved publish with MICROSOFT_STORE_FLIGHT_GROUP_IDS configured; do not create a portal draft." }
        $flightId = [string]$matchingFlights[0].flightId
        if ($flightId -notmatch '\A[a-zA-Z0-9][a-zA-Z0-9-]*\z') { throw 'Partner Center returned an invalid package-flight ID. Inspect flights before retrying.' }
        $flightUri = "$applicationUri/flights/$flightId"
        try { $flight = Invoke-RestMethod -Method Get -Uri $flightUri -Headers $headers }
        catch { throw 'Partner Center could not read Internal Testing. No draft was removed.' }
        if ($flight.flightId -cne $flightId -or $flight.friendlyName -cne $FlightName) { throw 'Partner Center returned an unexpected package flight. No draft was removed.' }
        if ($flight.pendingFlightSubmission) {
            $submissionId = [string]$flight.pendingFlightSubmission.id
            if ($submissionId -notmatch '\A[a-zA-Z0-9][a-zA-Z0-9-]*\z') { throw 'Partner Center returned an invalid pending flight submission ID. No draft was removed.' }
            $submissionUri = "$flightUri/submissions/$submissionId"
            try { $submission = Invoke-RestMethod -Method Get -Uri $submissionUri -Headers $headers }
            catch { throw 'Partner Center could not read the pending flight submission. No draft was removed.' }
            if ($submission.id -cne $submissionId -or $submission.flightId -cne $flightId) { throw 'Partner Center returned an unexpected flight submission. No draft was removed.' }
            if ($submission.status -notin @('PendingCommit', 'CommitFailed')) {
                throw 'Internal Testing has a submission that is not an editable draft. Wait for processing or resolve it in Partner Center before publishing. No draft was removed.'
            }
            if ([string]::IsNullOrWhiteSpace($submission.fileUploadUrl)) {
                if (-not $ReplaceFlightDraft -or $submission.status -ne 'PendingCommit') {
                    throw 'Internal Testing has a Partner Center-created draft without an API upload URL. Preserve the flight and tester groups: discard only its unsubmitted draft, or manually dispatch publish with replace_flight_draft enabled to discard a PendingCommit draft. No draft was removed.'
                }
                try { Invoke-RestMethod -Method Delete -Uri $submissionUri -Headers $headers | Out-Null }
                catch { throw 'Partner Center could not delete the approved Internal Testing draft. Inspect its state before retrying. The flight and production listing were not targeted for deletion.' }
                Write-Host 'Discarded only the approved Internal Testing portal draft; the flight and tester groups are preserved. msstore publish will create an API submission.'
            }
        }
    }
}
finally { $token = $null; $headers = $null }
if ($PrepareFlight) { Write-Host 'Internal Testing is ready for API publishing. No package was uploaded or committed by this check.' }
else { Write-Host 'Authentication and read-only access to the Store application verified. No draft or submission was changed.' }
if ($FlightName) {
    Write-Host "Resolved the '$FlightName' package flight."
    $flightId
}
