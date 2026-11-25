param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Notes = "",
    [switch]$SkipUpload
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$verClean = $Version.TrimStart('v', 'V')
$tag = "v$verClean"
$verCore = ($verClean -split '-')[0]
$verParts = @($verCore -split '\.')
while ($verParts.Count -lt 4) { $verParts += "0" }
$verFour = ($verParts[0..3] -join '.')

Write-Host "=== FluxCord Release Builder ($tag) ===" -ForegroundColor Cyan

# 1. Stamp version into Updater.cs and FluxCord.csproj (strict UTF-8, no mojibake in PS 5.1)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$updPath = Join-Path $root "src\FluxCord\Updater.cs"
$updText = [System.IO.File]::ReadAllText($updPath, $utf8NoBom) -replace 'CurrentVersion = "[^"]+"', "CurrentVersion = `"$verClean`""
[System.IO.File]::WriteAllText($updPath, $updText, $utf8NoBom)

$cspPath = Join-Path $root "src\FluxCord\FluxCord.csproj"
$csp = [System.IO.File]::ReadAllText($cspPath, $utf8NoBom)
$csp = $csp -replace '<Version>[^<]+</Version>', "<Version>$verClean</Version>"
$csp = $csp -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$verFour</FileVersion>"
$csp = $csp -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$verFour</AssemblyVersion>"
[System.IO.File]::WriteAllText($cspPath, $csp, $utf8NoBom)

# 2. Build C++20 native media engine + run C++ unit tests
$vcvars = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { $vcvars = "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" }
$bat = "@echo off`r`ncall `"$vcvars`" >nul`r`ncmake -S `"$root\native`" -B `"$root\native\build`" -G Ninja -DCMAKE_BUILD_TYPE=Release || exit /b 1`r`ncmake --build `"$root\native\build`" -j 8 || exit /b 1`r`n`"$root\native\build\FluxCord.Tests.exe`" || exit /b 1`r`n"
$tmpBat = Join-Path $env:TEMP "fc_rel_build.cmd"
Set-Content $tmpBat -Value $bat -Encoding ASCII
cmd /c "`"$tmpBat`""
if ($LASTEXITCODE -ne 0) { throw "Native C++ build/tests failed" }

# 3. Publish NativeAOT win-x64 into clean release staging folder
$pkgDir = Join-Path $root "dist\release_$verClean"
if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
dotnet publish (Join-Path $root "src\FluxCord\FluxCord.csproj") -c Release -r win-x64 -o $pkgDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $root "native\build\FluxCord.Media.dll") (Join-Path $pkgDir "FluxCord.Media.dll") -Force
Copy-Item (Join-Path $root "native\third_party\libdave\bin\libdave.dll") (Join-Path $pkgDir "libdave.dll") -Force
# Remove giant 37MB PDB from end-user portable ZIP
Get-ChildItem $pkgDir -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue

# Assert all mandatory runtime files & assets exist before packaging!
foreach ($reqRel in @("FluxCord.exe", "FluxCord.Media.dll", "libdave.dll", "Assets\fluxcord.ico", "Assets\fluxcord.png", "Assets\fonts\lucide.ttf")) {
    $reqFull = Join-Path $pkgDir $reqRel
    if (-not (Test-Path $reqFull)) { throw "Missing required release file: $reqRel" }
}

# 4. Pack portable ZIP + compute SHA-256
$zipName = "FluxCord-$verClean-win-x64.zip"
$zipPath = Join-Path $root "dist\$zipName"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$pkgDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Packed: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 2)) MB) SHA256=$sha" -ForegroundColor Green

if ($SkipUpload) { exit 0 }

# 5. Upload GitHub Release atomically (draft=true -> upload ZIP -> PATCH draft=false so clients never see an empty release!)
$psi = New-Object System.Diagnostics.ProcessStartInfo("git", "credential fill")
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$proc = [System.Diagnostics.Process]::Start($psi)
$proc.StandardInput.WriteLine("protocol=https")
$proc.StandardInput.WriteLine("host=github.com")
$proc.StandardInput.WriteLine("")
$proc.StandardInput.Close()
$credRaw = $proc.StandardOutput.ReadToEnd()
$proc.WaitForExit()
if ($credRaw -notmatch "password=([^\r\n]+)") { throw "No GitHub token in Git Credential Manager" }
$tok = $Matches[1].Trim()
$hdrs = @{
    Authorization = "Bearer $tok"
    Accept = "application/vnd.github+json"
    "User-Agent" = "FluxCord-Release"
}

$isPre = $verClean.Contains("-")
$bodyObj = @{
    tag_name = $tag
    name = "FluxCord $tag"
    body = if ([string]::IsNullOrWhiteSpace($Notes)) { "FluxCord $tag — NativeAOT Discord client for Windows x64.`n`n**SHA-256 ($zipName):** ``$sha``" } else { "$Notes`n`n**SHA-256 ($zipName):** ``$sha``" }
    draft = $true
    prerelease = $isPre
} | ConvertTo-Json -Depth 4

$rel = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/So2K/FluxCord/releases" -Headers $hdrs -Body ([System.Text.Encoding]::UTF8.GetBytes($bodyObj)) -ContentType "application/json; charset=utf-8"
$uploadUrl = ($rel.upload_url -replace '\{\?name,label\}', '') + "?name=$zipName"
$uploadHdrs = @{ Authorization = "Bearer $tok"; "User-Agent" = "FluxCord-Release"; "Content-Type" = "application/zip" }
$asset = Invoke-RestMethod -Method Post -Uri $uploadUrl -Headers $uploadHdrs -InFile $zipPath

$pubObj = @{ draft = $false } | ConvertTo-Json
$relPub = Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/So2K/FluxCord/releases/$($rel.id)" -Headers $hdrs -Body ([System.Text.Encoding]::UTF8.GetBytes($pubObj)) -ContentType "application/json; charset=utf-8"
Write-Host "Published GitHub Release: $($relPub.html_url)" -ForegroundColor Green
Write-Host "Asset URL: $($asset.browser_download_url)" -ForegroundColor Green
