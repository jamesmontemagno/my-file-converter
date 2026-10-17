$ErrorActionPreference = 'Stop'
$names = @('PARTNER_CENTER_TENANT_ID', 'PARTNER_CENTER_SELLER_ID', 'PARTNER_CENTER_CLIENT_ID', 'PARTNER_CENTER_CLIENT_SECRET', 'MICROSOFT_STORE_PRODUCT_ID')
$original = @{}
foreach ($name in $names) { $original[$name] = [Environment]::GetEnvironmentVariable($name) }
$flightId = '7bfc11d5-f710-47c5-8a98-e04bb5aad310'
$global:LocalMorphPartnerCenterScenario = ''
$global:LocalMorphPartnerCenterCalls = @()
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
        if ($Method -ne 'Get' -or $Headers.Authorization -ne 'Bearer local-test-access-token') { throw 'Unexpected Partner Center method or authorization.' }
        if ($Uri -like '*/listflights?*') {
            $flight = @{ flightId = '7bfc11d5-f710-47c5-8a98-e04bb5aad310'; friendlyName = 'Internal Testing' }
            switch ($global:LocalMorphPartnerCenterScenario) {
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
    foreach ($scenario in (@('valid', 'paginated', 'diagnose', 'numeric-id') + @($failures.Keys))) {
        $global:LocalMorphPartnerCenterScenario = $scenario
        $global:LocalMorphPartnerCenterCalls = @()
        $failure = $null
        $result = $null
        try {
            if ($scenario -eq 'diagnose') {
                $result = & (Join-Path $PSScriptRoot 'Test-PartnerCenter.ps1')
            }
            else {
                $result = & (Join-Path $PSScriptRoot 'Test-PartnerCenter.ps1') -FlightName 'Internal Testing'
            }
        }
        catch { $failure = $_.Exception.Message }
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
        }
        Write-Host "Partner Center read-only scenario passed: $scenario"
    }
    Write-Host "All $(4 + $failures.Count) Partner Center scenarios passed without network access or submissions."
}
finally {
    Remove-Item Function:\Invoke-RestMethod
    Remove-Variable -Name LocalMorphPartnerCenterScenario, LocalMorphPartnerCenterCalls -Scope Global
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $original[$name]) }
}
