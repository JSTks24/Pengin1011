param(
	[Parameter(Mandatory)][string]$Tag,
	[Parameter(Mandatory)][string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$packageId = 'Pengin1011.ModuleSdk'
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -File -Filter '*.nupkg')
if ($packages.Count -ne 1) {
	throw "Expected one NuGet package in '$PackageDirectory', found $($packages.Count)."
}

$package = $packages[0]
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
	$manifest = $archive.GetEntry("$packageId.nuspec")
	if ($null -eq $manifest) { throw "Missing $packageId.nuspec in '$($package.Name)'." }
	$reader = [IO.StreamReader]::new($manifest.Open())
	try {
		$metadata = ([xml]$reader.ReadToEnd()).package.metadata
	} finally {
		$reader.Dispose()
	}
} finally {
	$archive.Dispose()
}

$version = [string]$metadata.version
if (!$version -or $metadata.id -cne $packageId -or $Tag -cne "v$version" -or $package.Name -cne "$packageId.$version.nupkg") {
	throw "Package '$($package.Name)' declares '$($metadata.id)' version '$version'. Expected $packageId for tag '$Tag'."
}
