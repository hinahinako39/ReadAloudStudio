param([ValidateSet('phones','asr','voices','all')][string]$Group = 'all')
$ErrorActionPreference = 'Stop'
$base = Join-Path (Split-Path $PSScriptRoot) 'engine/models/hub'
$manifest = Get-Content -Raw (Join-Path $PSScriptRoot 'models.json') | ConvertFrom-Json
foreach ($m in $manifest) {
    if ($Group -ne 'all' -and $Group -ne $m.group) { continue }
    $cache = Join-Path $base ('models--' + $m.id.Replace('/','--'))
    $snapshot = Join-Path $cache ('snapshots/' + $m.revision)
    [void](New-Item -Type Directory -Path $snapshot -Force)
    foreach ($file in $m.files) {
        $dest = Join-Path $snapshot $file.name
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force)
        if (Test-Path -LiteralPath $dest) {
            $ok = (Get-Item -LiteralPath $dest).Length -eq $file.bytes
            if ($ok -and $file.sha256) { $ok = (Get-FileHash -Algorithm SHA256 -LiteralPath $dest).Hash -eq $file.sha256 }
            if ($ok) { continue }
        }
        $url = 'https://huggingface.co/' + $m.id + '/resolve/' + $m.revision + '/' + $file.name
        $part = $dest + '.part'
        Write-Host ('Downloading ' + $m.id + '/' + $file.name)
        & curl.exe --fail --silent --show-error --location --retry 3 --connect-timeout 15 --output $part $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
        if ((Get-Item -LiteralPath $part).Length -ne $file.bytes) { throw "Incomplete: $url" }
        if ($file.sha256 -and (Get-FileHash -Algorithm SHA256 -LiteralPath $part).Hash -ne $file.sha256) { throw "Checksum failed: $url" }
        Move-Item -Force -LiteralPath $part -Destination $dest
    }
    [void](New-Item -Type Directory -Force -Path (Join-Path $cache 'refs'))
    [IO.File]::WriteAllText((Join-Path $cache 'refs/main'), $m.revision)
}
