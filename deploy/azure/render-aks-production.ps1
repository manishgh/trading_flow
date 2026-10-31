param(
    [Parameter(Mandatory = $true)][string] $BicepOutputJson,
    [Parameter(Mandatory = $true)][string] $ImageTag,
    [Parameter(Mandatory = $true)][string] $PublicHostName,
    [Parameter(Mandatory = $true)][string] $IngressClassName,
    [Parameter(Mandatory = $true)][string] $IngressNamespace,
    [Parameter(Mandatory = $true)][string] $IngressProxyCidr,
    [Parameter(Mandatory = $true)][string] $TlsSecretName,
    [string] $OutputDir = 'deploy/aks/rendered/paper'
)
$ErrorActionPreference = 'Stop'

# Read one explicit non-secret Bicep output; never call Azure or print the source document.
function Get-OutputValue([string] $Name) {
    $value = [string]$outputs.properties.outputs.$Name.value
    if ([string]::IsNullOrWhiteSpace($value) -or $value.IndexOfAny([char[]]"`r`n`"") -ge 0) {
        throw "Missing or invalid Bicep output: $Name"
    }
    return $value
}

# Refuse junctions throughout a path before copying templates into its owned namespace.
function Assert-NoPathLinks([string] $Path) {
    $cursor = $Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $entry = Get-Item -LiteralPath $cursor -Force
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Template paths cannot follow links: $cursor" }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sourcePath = [IO.Path]::GetFullPath((Join-Path $repoRoot 'deploy/aks/production'))
$renderedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'deploy/aks/rendered'))
$outputPath = if ([IO.Path]::IsPathRooted($OutputDir)) { [IO.Path]::GetFullPath($OutputDir) }
    else { [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDir)) }
if (!$outputPath.StartsWith($renderedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDir must be a new child of deploy/aks/rendered. No existing directory is deleted or overwritten.'
}
Assert-NoPathLinks $sourcePath
Assert-NoPathLinks $outputPath
if (Test-Path -LiteralPath $outputPath) { throw 'OutputDir already exists. Choose a new directory; the renderer never deletes existing output.' }
if ($ImageTag -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$') { throw 'ImageTag must be an explicit valid container tag.' }
if ($PublicHostName -notmatch '^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$') { throw 'PublicHostName must be a DNS hostname.' }
foreach ($name in @($IngressClassName, $IngressNamespace, $TlsSecretName)) {
    if ($name -notmatch '^[a-z0-9](?:[-a-z0-9.]*[a-z0-9])?$' -or $name.Length -gt 253) { throw 'Ingress class, namespace and TLS secret names must be valid Kubernetes names.' }
}
$proxyNetwork = [Net.IPNetwork]::Parse($IngressProxyCidr)
if ($proxyNetwork.PrefixLength -eq 0) { throw 'IngressProxyCidr must identify the trusted ingress network, not a trust-all range.' }
$outputs = Get-Content -LiteralPath $BicepOutputJson -Raw | ConvertFrom-Json
$replacements = @{
    '__TRADINGFLOW_REGISTRY__' = (Get-OutputValue 'acrLoginServer')
    '__TRADINGFLOW_IMAGE_TAG__' = $ImageTag
    '__TRADINGFLOW_PUBLIC_HOST__' = $PublicHostName
    '__TRADINGFLOW_INGRESS_CLASS__' = $IngressClassName
    '__TRADINGFLOW_INGRESS_NAMESPACE__' = $IngressNamespace
    '__TRADINGFLOW_INGRESS_PROXY_CIDR__' = $IngressProxyCidr
    '__TRADINGFLOW_TLS_SECRET__' = $TlsSecretName
    'kv-tradingflow-paper' = (Get-OutputValue 'keyVaultName')
    'kv-tradingflow-live' = (Get-OutputValue 'keyVaultName')
    '__TRADINGFLOW_STORAGE_ACCOUNT__' = (Get-OutputValue 'storageAccountName')
    '__TRADINGFLOW_RESOURCE_GROUP__' = (Get-OutputValue 'resourceGroupName')
    '00000000-0000-0000-0000-000000000000' = (Get-OutputValue 'tenantId')
    '00000000-0000-0000-0000-000000000001' = (Get-OutputValue 'webIdentityClientId')
    '00000000-0000-0000-0000-000000000002' = (Get-OutputValue 'paperIdentityClientId')
    '00000000-0000-0000-0000-000000000003' = (Get-OutputValue 'liveIdentityClientId')
}
# Validate every rendered document before creating output, so rejected inputs leave nothing to clean up.
$documents = foreach ($file in Get-ChildItem -LiteralPath $sourcePath -File -Recurse | Where-Object Extension -in '.yaml', '.yml') {
    Assert-NoPathLinks $file.FullName
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($key in $replacements.Keys) { $content = $content.Replace($key, $replacements[$key]) }
    if ($content -match '__TRADINGFLOW_[A-Z_]+__') { throw "Unresolved template value in $($file.Name)" }
    [pscustomobject]@{ Relative = [IO.Path]::GetRelativePath($sourcePath, $file.FullName); Content = $content }
}
foreach ($document in $documents) {
    $destination = Join-Path $outputPath $document.Relative
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Set-Content -LiteralPath $destination -Value $document.Content -NoNewline -Encoding utf8
}
Write-Host "Rendered templates to $outputPath. No infrastructure was changed."
Write-Host 'Review the rendered resources and obtain deployment authorization before applying them.'
