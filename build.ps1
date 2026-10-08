# Builds Clawd.exe with the C# compiler that ships with Windows (.NET Framework 4).
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$meta = Join-Path $env:WINDIR 'System32\WinMetadata'
$src = Join-Path $PSScriptRoot 'Clawd.cs'
$out = Join-Path $PSScriptRoot 'Clawd.exe'

# The .winmd files describe the Windows media-session API (what is playing now);
# UIAutomation finds interface components in other apps.
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$out" "/lib:$fw\WPF" `
    /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll `
    /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:System.Runtime.dll `
    "/r:$meta\Windows.Media.winmd" "/r:$meta\Windows.Foundation.winmd" $src
exit $LASTEXITCODE
