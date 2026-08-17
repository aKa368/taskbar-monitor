[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ArtifactPath,

    [string]$SignToolPath,

    # Prefer a certificate in the Windows certificate store or a hardware-backed
    # provider. Use PFX only when it is supplied by the CI secret store.
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$CertificateThumbprint,

    [string]$TimestampUrl,
    [string]$ExpectedPublisher,
    [string]$ExpectedThumbprint,
    [string]$HashOutputPath,
    [string]$SummaryOutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-SignTool {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = Resolve-Path -LiteralPath $RequestedPath -ErrorAction Stop
        if (-not (Test-Path -LiteralPath $resolved.ProviderPath -PathType Leaf)) {
            throw "SignTool was not found at '$RequestedPath'."
        }
        return $resolved.ProviderPath
    }

    $candidatePaths = [System.Collections.Generic.List[string]]::new()
    $sdkRoots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $env:ProgramFiles 'Windows Kits\10\bin')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($sdkRoot in $sdkRoots) {
        if (Test-Path -LiteralPath $sdkRoot) {
            Get-ChildItem -Path $sdkRoot -Filter 'signtool.exe' -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
                ForEach-Object { $candidatePaths.Add($_.FullName) }
        }
    }

    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
        $candidatePaths.Add($command.Source)
    }

    $selected = $candidatePaths |
        Sort-Object -Unique |
        ForEach-Object {
            $item = Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue
            if ($null -ne $item) {
                [pscustomobject]@{
                    Path    = $item.FullName
                    Version = $item.VersionInfo.FileVersion
                }
            }
        } |
        Sort-Object Version, Path -Descending |
        Select-Object -First 1

    if ($null -eq $selected) {
        throw 'SignTool.exe was not found. Install the Windows SDK or pass -SignToolPath explicitly.'
    }

    return $selected.Path
}

function Invoke-SignTool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ToolPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $ToolPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "SignTool failed with exit code $exitCode."
    }
}

if ([string]::IsNullOrWhiteSpace($PfxPath)) {
    $PfxPath = $env:CODESIGN_PFX_PATH
}
if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
    $PfxPassword = $env:CODESIGN_PFX_PASSWORD
}
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $CertificateThumbprint = $env:CODESIGN_CERT_THUMBPRINT
}
if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $TimestampUrl = $env:CODESIGN_TIMESTAMP_URL
}
if ([string]::IsNullOrWhiteSpace($ExpectedPublisher)) {
    $ExpectedPublisher = $env:CODESIGN_EXPECTED_PUBLISHER
}
if ([string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
    $ExpectedThumbprint = $env:CODESIGN_EXPECTED_THUMBPRINT
}
if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $TimestampUrl = 'http://timestamp.digicert.com'
}

$hasPfx = -not [string]::IsNullOrWhiteSpace($PfxPath)
$hasStoreCertificate = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
if ($hasPfx -eq $hasStoreCertificate) {
    throw 'Provide exactly one signing source: -PfxPath or -CertificateThumbprint.'
}
if ($hasPfx -and [string]::IsNullOrWhiteSpace($PfxPassword)) {
    throw 'A PFX signing path was provided, but no PFX password was supplied.'
}
if ($hasPfx -and -not (Test-Path -LiteralPath $PfxPath -PathType Leaf)) {
    throw "PFX file was not found: $PfxPath"
}

$artifact = (Resolve-Path -LiteralPath $ArtifactPath -ErrorAction Stop).ProviderPath
if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
    throw "Artifact was not found: $ArtifactPath"
}

$signTool = Resolve-SignTool -RequestedPath $SignToolPath
Write-Host "Signing artifact: $artifact"
Write-Host "Using SignTool: $signTool"
Write-Host "Timestamp URL: $TimestampUrl"

$signArguments = [System.Collections.Generic.List[string]]::new()
$signArguments.Add('sign')
$signArguments.Add('/fd')
$signArguments.Add('SHA256')
$signArguments.Add('/tr')
$signArguments.Add($TimestampUrl)
$signArguments.Add('/td')
$signArguments.Add('SHA256')
$signArguments.Add('/d')
$signArguments.Add('TaskbarMonitor Windows 11 taskbar monitor')
$signArguments.Add('/du')
$signArguments.Add('https://github.com/aKa368/taskbar-monitor')

if ($hasPfx) {
    $signArguments.Add('/f')
    $signArguments.Add((Resolve-Path -LiteralPath $PfxPath -ErrorAction Stop).ProviderPath)
    $signArguments.Add('/p')
    # Do not log $PfxPassword. The CI secret store must mask this value.
    $signArguments.Add($PfxPassword)
}
else {
    $normalizedThumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
    $signArguments.Add('/sha1')
    $signArguments.Add($normalizedThumbprint)
}

Invoke-SignTool -ToolPath $signTool -Arguments ($signArguments.ToArray() + $artifact)

Write-Host 'Verifying Authenticode signature with the Default Authentication policy.'
Invoke-SignTool -ToolPath $signTool -Arguments @('verify', '/pa', '/v', $artifact)

$signature = Get-AuthenticodeSignature -FilePath $artifact
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "PowerShell signature validation failed: $($signature.Status) - $($signature.StatusMessage)"
}

if ($signature.PSObject.Properties.Name -contains 'TimeStamperCertificate') {
    if ($null -eq $signature.TimeStamperCertificate) {
        throw 'The Authenticode signature is valid but has no timestamp certificate.'
    }
}
else {
    throw 'PowerShell could not inspect the timestamp certificate.'
}

$signerCertificate = $signature.SignerCertificate
if ($null -eq $signerCertificate) {
    throw 'The signature has no signer certificate.'
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisher)) {
    $publisherText = @(
        $signerCertificate.Subject,
        $signerCertificate.GetNameInfo(
            [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
            $false)
    ) -join ' | '

    if ($publisherText.IndexOf($ExpectedPublisher, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Unexpected signer publisher. Expected '$ExpectedPublisher'; actual '$publisherText'."
    }
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
    $expected = ($ExpectedThumbprint -replace '\s', '').ToUpperInvariant()
    $actual = ($signerCertificate.Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($actual -ne $expected) {
        throw "Unexpected signer thumbprint. Expected '$expected'; actual '$actual'."
    }
}

if ([string]::IsNullOrWhiteSpace($HashOutputPath)) {
    $HashOutputPath = "$artifact.sha256"
}
else {
    $hashDirectory = Split-Path -Parent $HashOutputPath
    if (-not [string]::IsNullOrWhiteSpace($hashDirectory)) {
        New-Item -ItemType Directory -Path $hashDirectory -Force | Out-Null
    }
}

$hash = Get-FileHash -LiteralPath $artifact -Algorithm SHA256
$hashLine = '{0} *{1}' -f $hash.Hash.ToLowerInvariant(), (Split-Path -Leaf $artifact)
Set-Content -LiteralPath $HashOutputPath -Value $hashLine -Encoding ASCII -NoNewline

$summary = [ordered]@{
    Artifact             = $artifact
    SignerSubject        = $signerCertificate.Subject
    SignerThumbprint     = $signerCertificate.Thumbprint
    Timestamped          = $null -ne $signature.TimeStamperCertificate
    TimestampSigner      = if ($null -ne $signature.TimeStamperCertificate) {
        $signature.TimeStamperCertificate.Subject
    } else {
        $null
    }
    SHA256               = $hash.Hash.ToLowerInvariant()
    HashFile             = (Resolve-Path -LiteralPath $HashOutputPath).ProviderPath
    VerifiedAtUtc        = (Get-Date).ToUniversalTime().ToString('o')
}

if (-not [string]::IsNullOrWhiteSpace($SummaryOutputPath)) {
    $summaryDirectory = Split-Path -Parent $SummaryOutputPath
    if (-not [string]::IsNullOrWhiteSpace($summaryDirectory)) {
        New-Item -ItemType Directory -Path $summaryDirectory -Force | Out-Null
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $SummaryOutputPath -Encoding UTF8
}

$summary | ConvertTo-Json -Depth 4
Write-Host "Signature verification succeeded. SHA-256 written to: $HashOutputPath"
