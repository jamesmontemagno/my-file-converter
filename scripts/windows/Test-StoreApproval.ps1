[CmdletBinding()]
param([switch]$AllowSelfReview)
$ErrorActionPreference = 'Stop'
if (-not $env:GITHUB_REPOSITORY -or -not $env:GH_TOKEN) { throw 'Approval protection must be checked using the workflow token.' }
$response = & gh api "repos/$env:GITHUB_REPOSITORY/environments/microsoft-store"
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify microsoft-store environment protection. Refusing to use publishing credentials.' }
$environment = $response | ConvertFrom-Json
$review = @($environment.protection_rules | Where-Object { $_.type -eq 'required_reviewers' })
if ($review.Count -ne 1 -or @($review[0].reviewers).Count -lt 1 -or $environment.can_admins_bypass -ne $false) {
    throw 'Store submission is blocked. Configure required reviewers and disable administrator bypass on microsoft-store. Re-run only after a reviewer can approve the job.'
}
if (-not $AllowSelfReview -and $review[0].prevent_self_review -ne $true) {
    throw 'Store submission requires independent approval unless self-review is explicitly allowed. Enable prevent self-review on microsoft-store.'
}
Write-Host 'Protected Store approval gate verified.'
