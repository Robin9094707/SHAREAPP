param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference='Stop'
$installDir=Join-Path $env:RUNNER_TEMP 'Robins Share Installation'
$installLog=Join-Path $env:RUNNER_TEMP 'RobinsShare-install.log'
$result=Join-Path $env:RUNNER_TEMP 'RobinsShare-startup.json'
$setup=Start-Process -FilePath (Resolve-Path $Installer) -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$installDir+'"'),('/LOG="'+$installLog+'"')) -Wait -PassThru
if($setup.ExitCode -ne 0){Get-Content $installLog;throw "Installation fehlgeschlagen: $($setup.ExitCode)"}
$exe=Join-Path $installDir 'RobinsShare.exe'
$stderr=Join-Path $env:RUNNER_TEMP 'RobinsShare-stderr.log'
$stdout=Join-Path $env:RUNNER_TEMP 'RobinsShare-stdout.log'
$p=Start-Process -FilePath $exe -WorkingDirectory $env:WINDIR -ArgumentList @('--startup-smoke-test','--result-file',('"'+$result+'"')) -RedirectStandardError $stderr -RedirectStandardOutput $stdout -PassThru
try{
    if(-not $p.WaitForExit(45000)){throw 'Das installierte Programm hat innerhalb von 45 Sekunden kein sichtbares Fenster bestätigt.'}
    if(Test-Path $result){Get-Content $result}
    if($p.ExitCode -ne 0 -or -not (Test-Path $result)){throw "Start des installierten Programms fehlgeschlagen: $($p.ExitCode)"}
    $state=Get-Content $result -Raw | ConvertFrom-Json
    if(-not $state.ok -or $state.stage -ne 'window-rendered'){throw 'Das Hauptfenster wurde nicht erfolgreich dargestellt.'}
    Write-Host 'Installiertes Programm aus fremdem Arbeitsverzeichnis gestartet; Hauptfenster wurde gerendert.'
}finally{
    if(-not $p.HasExited){Stop-Process -Id $p.Id -Force}
    foreach($path in @($stderr,$stdout,(Join-Path $env:LOCALAPPDATA 'RobinsShare/Logs/startup.log'))){if(Test-Path $path){Get-Content $path}}
}
