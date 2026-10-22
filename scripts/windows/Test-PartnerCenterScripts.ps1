$ErrorActionPreference = 'Stop'
$names = @('PARTNER_CENTER_TENANT_ID', 'PARTNER_CENTER_SELLER_ID', 'PARTNER_CENTER_CLIENT_ID', 'PARTNER_CENTER_CLIENT_SECRET', 'MICROSOFT_STORE_PRODUCT_ID', 'MICROSOFT_STORE_FLIGHT_GROUP_IDS')
$original = @{}
foreach ($name in $names) { $original[$name] = [Environment]::GetEnvironmentVariable($name) }
$flightId = '7bfc11d5-f710-47c5-8a98-e04bb5aad310'
$global:LocalMorphPartnerCenterScenario = ''
$global:LocalMorphPartnerCenterCalls = @()
$global:LocalMorphPartnerCenterMutations = @()
$global:LocalMorphPartnerCenterCreated = $false
$global:LocalMorphPartnerCenterDeleted = $false
try {
    $env:PARTNER_CENTER_TENANT_ID = '11111111-1111-1111-1111-111111111111'
    $env:PARTNER_CENTER_CLIENT_ID = '22222222-2222-2222-2222-222222222222'
    $env:PARTNER_CENTER_CLIENT_SECRET = 'local-test-not-a-secret'
    $env:PARTNER_CENTER_SELLER_ID = '123456'
    $env:MICROSOFT_STORE_PRODUCT_ID = '9TESTPRODUCT'
    function Invoke-RestMethod {
        param($Method, $Uri, $ContentType, $Body, $Headers)
        $global:LocalMorphPartnerCenterCalls += [string]$Uri
        if ($Uri -like 'https://login.microsoftonline.com/*') {
            if ($Method -ne 'Post' -or $Body.resource -ne 'https://manage.devcenter.microsoft.com') { throw 'Unexpected authentication request.' }
            if ($global:LocalMorphPartnerCenterScenario -eq 'authentication-error') { throw 'Mock authentication error.' }
            if ($global:LocalMorphPartnerCenterScenario -eq 'missing-token') { return @{} }
            return @{ access_token = 'local-test-access-token' }
        }
        if ($Headers.Authorization -ne ('Bearer ' + 'local-test-access-token')) { throw 'Unexpected authorization.' }
        $scenario = $global:LocalMorphPartnerCenterScenario
        $appUri = 'https://manage.devcenter.microsoft.com/v1.0/my/applications/9TESTPRODUCT'
        $id = if ($scenario -eq 'numeric-id') { '1152921504621249830' } else { '7bfc11d5-f710-47c5-8a98-e04bb5aad310' }
        if ($Method -ne 'Get') {
            $global:LocalMorphPartnerCenterMutations += "$Method $Uri"
            if ($Method -eq 'Post' -and $Uri -eq "$appUri/flights") {
                $request = $Body | ConvertFrom-Json
                if ($ContentType -ne 'application/json' -or $request.friendlyName -cne 'Internal Testing' -or
                    $request.groupIds -isnot [array] -or ($request.groupIds -join ',') -ne '123,456') { throw 'Wrong flight creation payload.' }
                if ($scenario -eq 'create-error') { throw 'sensitive-response' }
                $global:LocalMorphPartnerCenterCreated = $true
                $created = @{ flightId = $id; friendlyName = 'Internal Testing' }
                if ($scenario -eq 'create-invalid-id') { $created.flightId = '../production' }
                if ($scenario -eq 'create-wrong-name') { $created.friendlyName = 'Other' }
                return $created
            }
            if ($Method -eq 'Delete' -and $Uri -eq "$appUri/flights/$id/submissions/draft-1") {
                if ($scenario -eq 'delete-error') { throw 'sensitive-response' }
                $global:LocalMorphPartnerCenterDeleted = $true
                return
            }
            throw 'Unexpected mutation: production and flight deletion must never be targeted.'
        }
        if ($Method -ne 'Get' -or $Headers.Authorization -ne 'Bearer local-test-access-token') { throw 'Unexpected Partner Center method or authorization.' }
        if ($Uri -like '*/listflights?*') {
            $flight = @{ flightId = '7bfc11d5-f710-47c5-8a98-e04bb5aad310'; friendlyName = 'Internal Testing' }
            switch ($global:LocalMorphPartnerCenterScenario) {
                { $_ -like 'create-*' -and -not $global:LocalMorphPartnerCenterCreated } { return @{ value = @() } }
                'flight-api-error' { throw 'Mock flight API error.' }
                'missing-list' { return @{} }
                'no-flights' { return @{ value = @() } }
                'wrong-name' { $flight.friendlyName = 'Another flight' }
                'wrong-case' { $flight.friendlyName = 'internal testing' }
                'numeric-id' { $flight.flightId = '1152921504621249830' }
                'invalid-id' { $flight.flightId = '--appId' }
                'missing-id' { $flight.Remove('flightId') }
                'duplicate' { return @{ value = @($flight, $flight) } }
                'empty-page' { return @{ value = @(); '@nextLink' = 'applications/9TESTPRODUCT/listflights?skip=1' } }
                { $_ -in @('paginated', 'duplicate-across-pages') } {
                    if ($Uri -match 'skip=0$') {
                        $first = if ($_ -eq 'duplicate-across-pages') { $flight } else { @{ friendlyName = 'Other'; flightId = 'other' } }
                        return @{ value = @($first); '@nextLink' = 'applications/9TESTPRODUCT/listflights?skip=1' }
                    }
                    if ($Uri -notmatch 'skip=1$') { throw 'Unexpected pagination offset.' }
                }
            }
            return @{ value = @($flight) }
        }
        if ($Uri -eq "$appUri/flights/$id") {
            if ($scenario -eq 'flight-read-error') { throw 'sensitive-response' }
            $flight = @{ flightId = $id; friendlyName = 'Internal Testing'; groupIds = @('existing-group') }
            if ($scenario -eq 'flight-mismatch') { $flight.friendlyName = 'Other' }
            if ($scenario -in @('api-draft', 'api-commit-failed', 'portal-draft', 'portal-published', 'portal-commit-failed', 'replace-draft',
                    'delete-error', 'in-progress', 'unknown-status', 'invalid-submission-id', 'submission-mismatch', 'submission-read-error') -and
                -not $global:LocalMorphPartnerCenterDeleted) {
                $flight.pendingFlightSubmission = @{ id = 'draft-1' }
                if ($scenario -eq 'portal-published') { $flight.lastPublishedFlightSubmission = @{ id = 'published-1' } }
                if ($scenario -eq 'invalid-submission-id') { $flight.pendingFlightSubmission.id = '../production' }
            }
            return $flight
        }
        if ($Uri -eq "$appUri/flights/$id/submissions/draft-1") {
            if ($scenario -eq 'submission-read-error') { throw 'sensitive-response' }
            $submission = @{ id = 'draft-1'; flightId = $id; status = 'PendingCommit' }
            if ($scenario -like 'api-*') { $submission.fileUploadUrl = 'https://upload.invalid/package?sig=sensitive-response' }
            if ($scenario -in @('api-commit-failed', 'portal-commit-failed')) { $submission.status = 'CommitFailed' }
            if ($scenario -eq 'in-progress') { $submission.status = 'Certification' }
            if ($scenario -eq 'unknown-status') { $submission.Remove('status') }
            if ($scenario -eq 'submission-mismatch') { $submission.flightId = 'other-flight' }
            return $submission
        }
        if ($Uri -ne 'https://manage.devcenter.microsoft.com/v1.0/my/applications/9TESTPRODUCT') { throw 'Unexpected Partner Center URL.' }
        if ($global:LocalMorphPartnerCenterScenario -eq 'application-error') { throw 'Mock application API error.' }
        return @{ id = '9TESTPRODUCT' }
    }
    $failures = [ordered]@{
        'authentication-error' = '*Entra authentication failed*'
        'missing-token' = '*no access token*'
        'application-error' = '*could not read this application*'
        'flight-api-error' = '*could not list package flights*'
        'missing-list' = '*returned no package-flight list*'
        'no-flights' = '*found 0*'
        'wrong-name' = '*found 0*'
        'wrong-case' = '*found 0*'
        'invalid-id' = '*invalid package-flight ID*'
        'missing-id' = '*invalid package-flight ID*'
        'duplicate' = '*found 2*'
        'duplicate-across-pages' = '*found 2*'
        'empty-page' = '*empty package-flight page*'
    }
    $prepareScenarios = @('create-valid', 'create-missing-groups', 'create-invalid-groups', 'create-error', 'create-invalid-id', 'create-wrong-name',
        'replace-draft', 'delete-error', 'in-progress', 'unknown-status', 'portal-commit-failed')
    $failures += [ordered]@{
        'create-missing-groups' = '*Set MICROSOFT_STORE_FLIGHT_GROUP_IDS*'
        'create-invalid-groups' = '*Set MICROSOFT_STORE_FLIGHT_GROUP_IDS*'
        'create-error' = '*could not create Internal Testing*'
        'create-invalid-id' = '*invalid package-flight ID*'
        'create-wrong-name' = '*unexpected flight after creation*'
        'portal-draft' = '*Partner Center-created draft without an API upload URL*'
        'portal-published' = '*Partner Center-created draft without an API upload URL*'
        'portal-commit-failed' = '*Partner Center-created draft without an API upload URL*'
        'delete-error' = '*could not delete the approved Internal Testing draft*'
        'in-progress' = '*not an editable draft*'
        'unknown-status' = '*not an editable draft*'
        'invalid-submission-id' = '*invalid pending flight submission ID*'
        'submission-mismatch' = '*unexpected flight submission*'
        'flight-mismatch' = '*unexpected package flight*'
        'flight-read-error' = '*could not read Internal Testing*'
        'submission-read-error' = '*could not read the pending flight submission*'
    }
    $successes = @('valid', 'paginated', 'diagnose', 'numeric-id', 'create-valid', 'replace-draft', 'api-draft', 'api-commit-failed')
    foreach ($scenario in ($successes + @($failures.Keys))) {
        $global:LocalMorphPartnerCenterScenario = $scenario
        $global:LocalMorphPartnerCenterCalls = @()
        $global:LocalMorphPartnerCenterMutations = @()
        $global:LocalMorphPartnerCenterCreated = $false
        $global:LocalMorphPartnerCenterDeleted = $false
        $env:MICROSOFT_STORE_FLIGHT_GROUP_IDS = '123, 456,123'
        if ($scenario -eq 'create-missing-groups') { $env:MICROSOFT_STORE_FLIGHT_GROUP_IDS = $null }
        if ($scenario -eq 'create-invalid-groups') { $env:MICROSOFT_STORE_FLIGHT_GROUP_IDS = '123,../invalid' }
        $failure = $null
        $result = $null
        try {
            if ($scenario -eq 'diagnose') {
                $result = & (Join-Path $PSScriptRoot 'Test-PartnerCenter.ps1')
            }
            else {
                $parameters = @{ FlightName = 'Internal Testing' }
                if ($scenario -in $prepareScenarios) { $parameters.PrepareFlight = $true }
                if ($scenario -in @('replace-draft', 'delete-error', 'in-progress', 'unknown-status', 'portal-commit-failed')) { $parameters.ReplaceFlightDraft = $true }
                $result = & (Join-Path $PSScriptRoot 'Test-PartnerCenter.ps1') @parameters
            }
        }
        catch { $failure = $_.Exception.Message }
        if ($failure -like '*sensitive-response*') { throw "Sensitive response leaked in $scenario." }
        $expectedMutations = @()
        if ($scenario -in @('create-valid', 'create-error', 'create-invalid-id', 'create-wrong-name')) {
            $expectedMutations = @('Post https://manage.devcenter.microsoft.com/v1.0/my/applications/9TESTPRODUCT/flights')
        }
        if ($scenario -in @('replace-draft', 'delete-error')) {
            $expectedMutations = @("Delete https://manage.devcenter.microsoft.com/v1.0/my/applications/9TESTPRODUCT/flights/$flightId/submissions/draft-1")
        }
        if (($global:LocalMorphPartnerCenterMutations -join ',') -cne ($expectedMutations -join ',')) { throw "Unexpected mutations for $scenario." }
        if ($failures.Contains($scenario)) {
            if ($failure -notlike $failures[$scenario]) { throw "Unexpected $scenario result: $failure" }
        }
        else {
            if ($failure) { throw "Valid $scenario request rejected: $failure" }
            if ($scenario -eq 'diagnose') {
                if ($null -ne $result -or @($global:LocalMorphPartnerCenterCalls | Where-Object { $_ -like '*/listflights?*' }).Count -ne 0) { throw 'Diagnostics must not resolve flights or emit pipeline output.' }
            }
            else {
                $expected = if ($scenario -eq 'numeric-id') { '1152921504621249830' } else { $flightId }
                if ($result -ne $expected) { throw "Wrong resolved flight for $scenario." }
            }
            if ($scenario -eq 'paginated' -and @($global:LocalMorphPartnerCenterCalls | Where-Object { $_ -like '*/listflights?*' }).Count -ne 2) { throw 'Flight discovery did not check both pages.' }
            if ($scenario -in @('create-valid', 'replace-draft')) {
                $result = & (Join-Path $PSScriptRoot 'Test-PartnerCenter.ps1') -FlightName 'Internal Testing' -PrepareFlight -ReplaceFlightDraft
                if ($result -ne $flightId -or $global:LocalMorphPartnerCenterMutations.Count -ne 1) { throw "Retry mutated $scenario again." }
            }
        }
        Write-Host "Partner Center scenario passed: $scenario"
    }
    foreach ($parameters in @(@{ PrepareFlight = $true }, @{ FlightName = 'Internal Testing'; ReplaceFlightDraft = $true })) {
        $global:LocalMorphPartnerCenterCalls = @()
        $failure = $null
        try { & (Join-Path $PSScriptRoot 'Test-PartnerCenter.ps1') @parameters }
        catch { $failure = $_.Exception.Message }
        if ($failure -notlike '*Flight preparation requires*' -or $global:LocalMorphPartnerCenterCalls.Count) { throw 'Invalid mutation switches were accepted.' }
    }
    Write-Host "All $($successes.Count + $failures.Count + 2) Partner Center scenarios passed without network access or real submissions."
}
finally {
    Remove-Item Function:\Invoke-RestMethod
    Remove-Variable -Name LocalMorphPartnerCenterScenario, LocalMorphPartnerCenterCalls, LocalMorphPartnerCenterMutations, LocalMorphPartnerCenterCreated, LocalMorphPartnerCenterDeleted -Scope Global
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $original[$name]) }
}
