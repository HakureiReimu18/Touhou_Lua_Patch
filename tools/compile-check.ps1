# 东方潜渊行动组Lua补丁 —— C# 离线编译检查
# 原理：用 .NET 8 运行时驱动 Roslyn 官方编译器（tools\nct 内的 csc.dll），
# 以友元程序集名 InternalsAwareAssembly 引用本机游戏程序集做语义级编译，
# 与 LuaCs 运行时编译行为一致：
#   客户端程序集 = CSharp\Client + CSharp\Shared（带 CLIENT 符号）
#   服务端程序集 = CSharp\Server + CSharp\Shared（无 CLIENT 符号）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\compile-check.ps1
# 注意：本文件必须保存为 UTF-8 with BOM（PowerShell 5.1 否则无法解析中文注释）。

$ErrorActionPreference = "Stop"
$modDir  = Split-Path -Parent $PSScriptRoot
$gameDir = "E:\SteamLibrary\steamapps\common\Barotrauma"
$dotnet  = "C:\Program Files\dotnet\dotnet.exe"
$csc     = "$PSScriptRoot\nct\tasks\netcore\bincore\csc.dll"
$runtimeDir = "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.21"
if (-not (Test-Path $runtimeDir)) {
    $runtimeDir = (Get-ChildItem "C:\Program Files\dotnet\shared\Microsoft.NETCore.App" | Sort-Object Name -Descending | Select-Object -First 1).FullName
}

function Build-Rsp($define, $dirs) {
    $native = @("coreclr", "clrjit", "clrgc", "clretwrc", "hostpolicy", "mscordaccore", "mscordbi", "mscorrc", "msquic", ".Native.")
    # 输出名必须为 InternalsAwareAssembly：游戏通过 InternalsVisibleTo 向该名称开放 internal API，
    # 与 LuaCs 运行时编译模组所用的程序集名一致，否则 CS0122 误报
    $lines = @("/nostdlib", "/nologo", "/target:library", "/define:$define", "/langversion:latest", "`"/out:$PSScriptRoot\InternalsAwareAssembly.dll`"")
    foreach ($f in (Get-ChildItem $runtimeDir -Filter *.dll)) {
        $skip = $false
        foreach ($x in $native) { if ($f.Name.Contains($x)) { $skip = $true; break } }
        if (-not $skip) { $lines += "`"/r:$($f.FullName)`"" }
    }
    foreach ($n in @("Barotrauma.dll", "BarotraumaCore.dll", "MonoGame.Framework.Windows.NetStandard.dll", "XNATypes.dll", "0Harmony.dll", "Farseer.NetStandard.dll")) {
        $lines += "`"/r:$gameDir\$n`""
    }
    $count = 0
    foreach ($d in $dirs) {
        $full = Join-Path $modDir $d
        if (Test-Path $full) {
            foreach ($cs in (Get-ChildItem $full -Recurse -Filter *.cs)) {
                $lines += "`"$($cs.FullName)`""
                $count++
            }
        }
    }
    [System.IO.File]::WriteAllLines("$PSScriptRoot\_check.rsp", $lines, [System.Text.Encoding]::UTF8)
    return $count
}

function Run-Check($name, $define, $dirs) {
    $count = Build-Rsp $define $dirs
    if ($count -eq 0) { Write-Host "[$name] 无源文件，跳过" -ForegroundColor Yellow; return $true }
    $env:DOTNET_ROLL_FORWARD = "LatestMajor"
    $log = & $dotnet $csc -noconfig "@$PSScriptRoot\_check.rsp" 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) {
        Write-Host "[$name] 编译检查通过（$count 个源文件）" -ForegroundColor Green
        return $true
    } else {
        Write-Host "[$name] 编译失败：" -ForegroundColor Red
        Write-Host $log -ForegroundColor Red
        return $false
    }
}

$client = Run-Check "client" "CLIENT;DEBUG" @("CSharp\Client", "CSharp\Shared")
$server = Run-Check "server" "DEBUG" @("CSharp\Server", "CSharp\Shared")

Remove-Item "$PSScriptRoot\_check.rsp", "$PSScriptRoot\InternalsAwareAssembly.dll" -ErrorAction SilentlyContinue
if (-not ($client -and $server)) { exit 1 }
Write-Host "`n双端编译检查全部通过" -ForegroundColor Green
