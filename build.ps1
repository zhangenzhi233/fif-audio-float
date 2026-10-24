$ErrorActionPreference = 'Stop'
$taskCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskSource = Join-Path $PSScriptRoot 'src'
$taskLibrary = Join-Path $PSScriptRoot 'lib\NAudio.dll'
$taskOut = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $taskOut -Force | Out-Null
$taskFiles = @(Get-ChildItem -LiteralPath $taskSource -Filter '*.cs' | Select-Object -ExpandProperty FullName)
& $taskCompiler '/nologo' '/target:winexe' '/platform:x64' '/optimize+' '/utf8output' '/codepage:65001' ('/win32manifest:' + (Join-Path $taskSource 'app.manifest')) ('/out:' + (Join-Path $taskOut 'FifFloat.exe')) ('/reference:' + $taskLibrary) '/reference:System.dll' '/reference:System.Core.dll' '/reference:System.Windows.Forms.dll' '/reference:System.Drawing.dll' '/reference:System.Web.Extensions.dll' @taskFiles
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath $taskLibrary -Destination $taskOut -Force
Copy-Item -LiteralPath (Join-Path $taskSource 'FifFloat.exe.config') -Destination $taskOut -Force
Write-Output (Join-Path $taskOut 'FifFloat.exe')
