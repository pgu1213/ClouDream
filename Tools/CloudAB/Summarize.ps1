param([Parameter(Mandatory=$true)][string]$Directory)

# 유효 원본 표본을 합쳐 평균과 nearest-rank 백분위수를 계산합니다.
function Get-Stats($Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    return [ordered]@{
        samples = $sorted.Count
        mean = ($sorted | Measure-Object -Average).Average
        p95 = $sorted[[Math]::Min($sorted.Count - 1, [Math]::Ceiling($sorted.Count * .95) - 1)]
        p99 = $sorted[[Math]::Min($sorted.Count - 1, [Math]::Ceiling($sorted.Count * .99) - 1)]
        maximum = $sorted[-1]
    }
}

$reports = @(Get-ChildItem -LiteralPath $Directory -Filter '*.json' | Where-Object { $_.Name -match '^\d{8}-' } | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
$result = @()
foreach ($group in ($reports | Group-Object mode)) {
    $frames = @($group.Group | ForEach-Object { $_.frames })
    $cloud = @($group.Group | ForEach-Object { $_.cloudGpuSamples } | Where-Object { $_ -gt 0 })
    $gpuFrames = @($frames | Where-Object { $_.frameTimingAvailable -and $_.frameGpuMs -gt 0 })
    $result += [ordered]@{
        mode = $group.Name
        runs = $group.Count
        completed = @($group.Group | Where-Object { -not $_.completed }).Count -eq 0
        failures = @($group.Group | Where-Object { $_.failure } | ForEach-Object { $_.failure })
        fallbackFrames = ($group.Group | Measure-Object fallbackFrames -Sum).Sum
        wall = Get-Stats @($frames | ForEach-Object { $_.wallMs })
        frameGpu = Get-Stats @($gpuFrames | ForEach-Object { $_.frameGpuMs })
        frameGpuMissing = $frames.Count - $gpuFrames.Count
        cloudGpu = Get-Stats $cloud
        cloudGpuMissing = ($group.Group | ForEach-Object { $_.cloudGpu.missing } | Measure-Object -Sum).Sum
        cpuMain = Get-Stats @($gpuFrames | ForEach-Object { $_.cpuMainMs })
        cpuRender = Get-Stats @($gpuFrames | ForEach-Object { $_.cpuRenderMs })
        cpuPresentWait = Get-Stats @($gpuFrames | ForEach-Object { $_.cpuPresentWaitMs })
        build = Get-Stats @($frames | Where-Object { $_.buildGpuMs -gt 0 } | ForEach-Object { $_.buildGpuMs })
        surface = Get-Stats @($frames | Where-Object { $_.surfaceGpuMs -gt 0 } | ForEach-Object { $_.surfaceGpuMs })
        volume = Get-Stats @($frames | Where-Object { $_.volumeGpuMs -gt 0 } | ForEach-Object { $_.volumeGpuMs })
        composite = Get-Stats @($frames | Where-Object { $_.compositeGpuMs -gt 0 } | ForEach-Object { $_.compositeGpuMs })
        maximumBytes = ($frames | Measure-Object bytes -Maximum).Maximum
        proxyFrames = @($frames | Where-Object { $_.missing -gt 0 }).Count
    }
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Directory 'Summary.json') -Encoding utf8
$result | ForEach-Object {
    '{0,-16} runs={1} wall={2:F3}/{3:F3} cloud={4:F3}/{5:F3} fallback={6}' -f $_.mode,$_.runs,$_.wall.mean,$_.wall.p95,$_.cloudGpu.mean,$_.cloudGpu.p95,$_.fallbackFrames
}


