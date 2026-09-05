param(
    [string]$ZipPath
)

$ErrorActionPreference = "Stop"

try {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $projectDirectory = Split-Path -Parent $scriptDirectory
    $defaultUpdateDirectory = Join-Path $projectDirectory "wwwroot\updates"

    if ([string]::IsNullOrWhiteSpace($ZipPath)) {
        $package = Get-ChildItem -LiteralPath $defaultUpdateDirectory -File -Filter "*.zip" |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if (-not $package) {
            throw "更新目录中没有找到 ZIP 文件：$defaultUpdateDirectory"
        }
    }
    else {
        $resolvedPath = (Resolve-Path -LiteralPath $ZipPath).Path
        if (Test-Path -LiteralPath $resolvedPath -PathType Container) {
            $package = Get-ChildItem -LiteralPath $resolvedPath -File -Filter "*.zip" |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1
            if (-not $package) {
                throw "指定目录中没有找到 ZIP 文件：$resolvedPath"
            }
        }
        else {
            $package = Get-Item -LiteralPath $resolvedPath
        }
    }

    if ($package.Extension -ne ".zip") {
        throw "请选择 ZIP 更新包，当前文件：$($package.FullName)"
    }

    Write-Host ""
    Write-Host "正在计算更新包 SHA256，请稍候……" -ForegroundColor Cyan
    $stream = [System.IO.File]::OpenRead($package.FullName)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash($stream)
        $hash = -join ($hashBytes | ForEach-Object { $_.ToString("X2") })
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }

    try {
        Set-Clipboard -Value $hash
        $clipboardMessage = "SHA256 已自动复制到剪贴板。"
    }
    catch {
        $hash | clip.exe
        $clipboardMessage = "SHA256 已通过 clip.exe 复制到剪贴板。"
    }

    Write-Host ""
    Write-Host "更新包：" -NoNewline
    Write-Host $package.FullName -ForegroundColor White
    Write-Host "文件大小：$([Math]::Round($package.Length / 1MB, 2)) MB"
    Write-Host ""
    Write-Host "SHA256：" -ForegroundColor Green
    Write-Host $hash -ForegroundColor Yellow
    Write-Host ""
    Write-Host $clipboardMessage -ForegroundColor Green
    Write-Host "可以直接粘贴到 WebAPI 的 Update:Sha256 或 Update:Win32:Sha256 配置中。"
}
catch {
    Write-Host ""
    Write-Host "生成 SHA256 失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
