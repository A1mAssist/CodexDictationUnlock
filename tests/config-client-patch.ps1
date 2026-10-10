param([string]$AssemblyPath, [string]$FixturePath)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath))
$patch = $assembly.GetType('Program').GetMethod('PatchDictationSource', [Reflection.BindingFlags]'NonPublic,Static')
$fixtures = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$results = @($fixtures | ForEach-Object { $patch.Invoke($null, @([string]$_)) })
ConvertTo-Json -InputObject $results -Compress
