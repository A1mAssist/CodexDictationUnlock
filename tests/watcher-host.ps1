param([string]$AssemblyPath, [string]$SocketUrl)
$ErrorActionPreference = 'Stop'
$assembly = [System.Reflection.Assembly]::LoadFrom($AssemblyPath)
$method = $assembly.GetType('Program').GetMethod('WatchVoiceBundleAsync', [System.Reflection.BindingFlags]'NonPublic,Static')
$script = 'globalThis.__CDU_RELOAD_TEST__=true;'
$task = $method.Invoke($null, @($SocketUrl, $script, [System.Threading.CancellationToken]::None))
$task.GetAwaiter().GetResult()
