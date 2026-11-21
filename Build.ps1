param([string]$OutputPath = (Join-Path $PSScriptRoot 'Tingye.exe'))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The .NET Framework 4.x compiler could not be found.'
}

$references = @(
    'System.Core.dll', 'Microsoft.VisualBasic.dll', 'System.Windows.Forms.dll', 'System.Xml.dll', 'System.Xml.Linq.dll', 'System.Xaml.dll',
    'System.Net.Http.dll', 'System.Security.dll', 'System.Web.Extensions.dll',
    'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'WPF\WindowsBase.dll',
    'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'WPF\System.Speech.dll'
)
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001')
foreach ($reference in $references) {
    $path = Join-Path $framework $reference
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing .NET Framework assembly $path"
    }
    $arguments += "/reference:$path"
}
$arguments += '/out:' + $OutputPath
$arguments += '/resource:' + (Join-Path $PSScriptRoot 'MainWindow.xaml') + ',Tingye.MainWindow.xaml'
$icon = Join-Path $PSScriptRoot 'assets/Tingye.ico'
$arguments += '/win32icon:' + $icon
$arguments += '/resource:' + $icon + ',Tingye.Icon.ico'
$arguments += Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
