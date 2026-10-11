param([string]$AssemblyPath, [int]$Port)
$ErrorActionPreference = 'Stop'
$assembly = [System.Reflection.Assembly]::LoadFrom($AssemblyPath)
$method = $assembly.GetType('Program').GetMethod('WaitForCdpEndpointAsync', [System.Reflection.BindingFlags]'NonPublic,Static')
$task = $method.Invoke($null, @($Port, [TimeSpan]::FromSeconds(5)))
$task.GetAwaiter().GetResult()
