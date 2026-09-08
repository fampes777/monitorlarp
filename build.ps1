$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $root 'packages\compilers\tasks\net472\csc.exe'
$trace = Join-Path $root 'packages\traceevent-2.0.77\lib\net45'
$out = Join-Path $root 'MonitorLarp.exe'

if (-not (Test-Path -LiteralPath $csc)) {
    throw "Compiler not found: $csc"
}
if (-not (Test-Path -LiteralPath (Join-Path $trace 'Microsoft.Diagnostics.Tracing.TraceEvent.dll'))) {
    throw "TraceEvent library not found: $trace"
}

$args = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    "/win32manifest:$root\MonitorLarp.manifest",
    "/out:$out",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    "/reference:$trace\Microsoft.Diagnostics.Tracing.TraceEvent.dll",
    "/reference:$trace\Microsoft.Diagnostics.FastSerialization.dll",
    "/reference:$trace\OSExtensions.dll",
    "$root\MonitorLarp.cs"
)

& $csc @args
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $trace 'Microsoft.Diagnostics.Tracing.TraceEvent.dll') -Destination $root -Force
Copy-Item -LiteralPath (Join-Path $trace 'Microsoft.Diagnostics.FastSerialization.dll') -Destination $root -Force
Copy-Item -LiteralPath (Join-Path $trace 'OSExtensions.dll') -Destination $root -Force
Copy-Item -LiteralPath (Join-Path $trace 'TraceReloggerLib.dll') -Destination $root -Force
Copy-Item -LiteralPath (Join-Path $trace 'Dia2Lib.dll') -Destination $root -Force

$deps = Join-Path $root 'packages\runtime-deps'
Copy-Item -LiteralPath (Join-Path $deps 'system.collections.immutable-1.2.0\lib\portable-net45%2Bwin8%2Bwp8%2Bwpa81\System.Collections.Immutable.dll') -Destination $root -Force
Copy-Item -LiteralPath (Join-Path $deps 'system.reflection.metadata-1.4.2\lib\portable-net45%2Bwin8\System.Reflection.Metadata.dll') -Destination $root -Force
Copy-Item -LiteralPath (Join-Path $deps 'system.runtime.compilerservices.unsafe-4.5.2\lib\netstandard1.0\System.Runtime.CompilerServices.Unsafe.dll') -Destination $root -Force

Write-Host "Done: $out"
