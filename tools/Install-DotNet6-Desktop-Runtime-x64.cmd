@echo off
setlocal
set "GAMUSIC_RUNTIME_INSTALL_SCRIPT=%~f0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -Command "$lines = [IO.File]::ReadAllLines($env:GAMUSIC_RUNTIME_INSTALL_SCRIPT); $marker = [Array]::IndexOf($lines, ':POWERSHELL'); if ($marker -lt 0) { exit 1 }; & ([scriptblock]::Create(($lines[($marker + 1)..($lines.Length - 1)] -join [Environment]::NewLine)))"
set "INSTALL_RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %INSTALL_RESULT%

:POWERSHELL
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$version = '6.0.36'
$url = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$version/windowsdesktop-runtime-$version-win-x64.exe"
$expectedSha512 = '86fa63997e7e0dc6f3bf609e00880388dcf8d985c8f6417d07ebbbb1ecc957bf90214c8ff93f559a0e762b5626ba8c56c581f4d506aa4de7555f9792c2da254d'

function Test-DesktopRuntime {
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    $startup = $registry.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
    try {
        $registered = if ($startup) { $startup.GetValue('InstallLocation') }
    } finally {
        if ($startup) { $startup.Dispose() }
        $registry.Dispose()
    }

    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $dotnetRoot = Join-Path $programFiles 'dotnet'
    $architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $defaultRoot = if ($architecture -eq 'ARM64') { Join-Path $dotnetRoot 'x64' } else { $dotnetRoot }
    foreach ($root in @($registered, $defaultRoot) | Where-Object { $_ } | Select-Object -Unique) {
        $frameworks = Join-Path $root 'shared\Microsoft.WindowsDesktop.App'
        foreach ($runtime in @(Get-ChildItem -LiteralPath $frameworks -Directory -Filter '6.0.*' -ErrorAction SilentlyContinue)) {
            if (Test-Path -LiteralPath (Join-Path $runtime.FullName 'Microsoft.WindowsDesktop.App.deps.json')) {
                return $true
            }
        }
    }
    return $false
}

$installer = $null
try {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'GamusicStudio requires 64-bit Windows.' }
    if (Test-DesktopRuntime) {
        Write-Host '.NET 6 Desktop Runtime (x64) is already installed. You can open GamusicStudio.'
        exit 0
    }

    $installer = Join-Path ([IO.Path]::GetTempPath()) ("GamusicStudio-dotnet-$([guid]::NewGuid().ToString('N')).exe")
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Write-Host "Downloading .NET $version Desktop Runtime (x64) from Microsoft..."
    Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing -TimeoutSec 180
    if ((Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash -ne $expectedSha512) {
        throw 'The downloaded installer failed its SHA-512 verification.'
    }

    Write-Host 'Starting the verified Microsoft installer...'
    $result = Start-Process -FilePath $installer -ArgumentList '/install','/passive','/norestart' -Verb RunAs -PassThru -Wait
    if ($result.ExitCode -eq 3010) {
        Write-Host 'Installation completed. Restart Windows before opening GamusicStudio.'
        exit 0
    }
    if ($result.ExitCode -ne 0) { throw "The installer returned exit code $($result.ExitCode)." }
    if (-not (Test-DesktopRuntime)) { throw 'The .NET 6 x64 Desktop Runtime could not be found after installation.' }
    Write-Host 'Installation completed. You can open GamusicStudio.'
    exit 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
} finally {
    if ($installer) { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
}
