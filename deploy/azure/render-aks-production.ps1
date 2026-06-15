param(
    [Parameter(Mandatory = $true)]
    [string] $BicepOutputJson,

    [Parameter(Mandatory = $false)]
    [string] $ImageTag = "2026.06.14",

    [Parameter(Mandatory = $false)]
    [string] $SourceDir = "deploy/aks/production",

    [Parameter(Mandatory = $false)]
    [string] $OutputDir = "deploy/aks/rendered/paper"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")
$sourcePath = Resolve-Path -LiteralPath (Join-Path $repoRoot $SourceDir)
$outputPath = Join-Path $repoRoot $OutputDir
$outputJsonPath = Resolve-Path -LiteralPath $BicepOutputJson
$outputs = Get-Content -LiteralPath $outputJsonPath | ConvertFrom-Json

function Get-OutputValue([string] $name) {
    $value = $outputs.properties.outputs.$name.value
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Missing Bicep output: $name"
    }

    return $value
}

$acrLoginServer = Get-OutputValue "acrLoginServer"
$keyVaultName = Get-OutputValue "keyVaultName"
$storageAccountName = Get-OutputValue "storageAccountName"
$resourceGroupName = Get-OutputValue "resourceGroupName"
$tenantId = (az account show --query tenantId -o tsv)
$webClientId = Get-OutputValue "webIdentityClientId"
$paperClientId = Get-OutputValue "paperIdentityClientId"
$liveClientId = Get-OutputValue "liveIdentityClientId"

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
Copy-Item -LiteralPath (Join-Path $sourcePath "*") -Destination $outputPath -Recurse -Force

Get-ChildItem -LiteralPath $outputPath -Recurse -File -Include *.yaml,*.yml | ForEach-Object {
    $content = Get-Content -LiteralPath $_.FullName -Raw
    $content = $content.Replace("tradingflowprod.azurecr.io", $acrLoginServer)
    $content = $content.Replace("newTag: 2026.06.14", "newTag: $ImageTag")
    $content = $content.Replace("kv-tradingflow-paper", $keyVaultName)
    $content = $content.Replace("kv-tradingflow-live", $keyVaultName)
    $content = $content.Replace("__TRADINGFLOW_STORAGE_ACCOUNT__", $storageAccountName)
    $content = $content.Replace("__TRADINGFLOW_RESOURCE_GROUP__", $resourceGroupName)
    $content = $content.Replace("00000000-0000-0000-0000-000000000000", $tenantId)
    $content = $content.Replace("00000000-0000-0000-0000-000000000001", $webClientId)
    $content = $content.Replace("00000000-0000-0000-0000-000000000002", $paperClientId)
    $content = $content.Replace("00000000-0000-0000-0000-000000000003", $liveClientId)
    $tempPath = "$($_.FullName).tmp"
    Set-Content -LiteralPath $tempPath -Value $content -NoNewline
    Move-Item -LiteralPath $tempPath -Destination $_.FullName -Force
}

Write-Host "Rendered AKS manifests to $outputPath"
Write-Host "Next: kubectl apply -k $OutputDir"
