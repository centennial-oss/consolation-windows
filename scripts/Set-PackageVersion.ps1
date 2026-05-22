param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [string] $Version = "1.0.0.0"
)

$document = [xml](Get-Content -LiteralPath $Path -Raw)
$namespaceManager = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
$namespaceManager.AddNamespace("pkg", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")

$identity = $document.SelectSingleNode("/pkg:Package/pkg:Identity", $namespaceManager)
if ($null -eq $identity) {
    throw "Package identity was not found in '$Path'."
}

$identity.Version = $Version
$document.Save((Resolve-Path -LiteralPath $Path))
