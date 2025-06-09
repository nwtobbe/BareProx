# Tools/GenerateVersionInfo.ps1

$now = Get-Date
$build = $now.ToString("yyMM")      # e.g. 2506 → June 2025
$revision = $now.ToString("ddHH")   # e.g. 0117 → 1st day, 17:00

$version = "1.0.$build.$revision"

$assemblyCode = @'
using System.Reflection;

[assembly: AssemblyVersion("{0}")]
[assembly: AssemblyFileVersion("{0}")]
'@ -f $version

# Output directory 
$targetPath = "Generated"
New-Item -ItemType Directory -Force -Path $targetPath | Out-Null

# Write the generated file
$assemblyCode | Out-File -Encoding UTF8 -FilePath "$targetPath/VersionInfo.cs"

Write-Host "✅ Generating version: $version"
