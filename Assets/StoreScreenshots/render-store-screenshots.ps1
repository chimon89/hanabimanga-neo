$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$html = Join-Path $root "store-showcase.html"

$edgeCandidates = @(
    "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "C:\Program Files\Microsoft\Edge\Application\msedge.exe"
)

$edge = $edgeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $edge) {
    throw "Microsoft Edge was not found."
}

$shots = @(
    @{ Shot = "home";      File = "01-home.png" },
    @{ Shot = "detail";    File = "02-detail.png" },
    @{ Shot = "reader";    File = "03-reader.png" },
    @{ Shot = "bookshelf"; File = "04-bookshelf.png" },
    @{ Shot = "tasks";     File = "05-tasks.png" },
    @{ Shot = "hero";      File = "06-super-hero-art.png" }
)

$htmlUri = [System.Uri]::new((Resolve-Path -LiteralPath $html).Path).AbsoluteUri

foreach ($item in $shots) {
    $out = Join-Path $root $item.File
    $url = "${htmlUri}?shot=$($item.Shot)"
    $args = @(
        "--headless=new",
        "--disable-gpu",
        "--hide-scrollbars",
        "--allow-file-access-from-files",
        "--force-device-scale-factor=1",
        "--window-size=1920,1080",
        "--screenshot=$out",
        $url
    )

    & $edge @args | Out-Null

    if (-not (Test-Path -LiteralPath $out)) {
        throw "Failed to render $($item.File)."
    }

    $size = (Get-Item -LiteralPath $out).Length
    if ($size -le 0) {
        throw "Rendered file is empty: $($item.File)."
    }
}

Get-Item -LiteralPath ($shots | ForEach-Object { Join-Path $root $_.File }) |
    Select-Object Name, Length, FullName
