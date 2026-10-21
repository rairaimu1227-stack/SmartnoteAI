param([switch]$SkipBrowserTests)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
function Run([scriptblock]$Command) { & $Command; if ($LASTEXITCODE -ne 0) { throw "Command failed: $Command" } }
Push-Location editor-web
try {
    Run { npm ci }
    Run { npm run build }
    Run { npm test }
    if (-not $SkipBrowserTests) {
        Run { npx playwright install chromium }
        Run { npm run test:e2e }
    }
} finally { Pop-Location }
Run { dotnet restore SmartNote.sln --locked-mode }
Run { dotnet build SmartNote.sln -c Release --no-restore }
Run { dotnet test SmartNote.Tests -c Release --no-build --logger 'trx;LogFileName=results.trx' }
Run { dotnet publish SmartNote.Desktop -c Release -r win-x64 --self-contained true --no-restore -o artifacts/win-x64 -p:PublishReadyToRun=false }
Copy-Item *.md artifacts/win-x64/ -Force
Copy-Item docs artifacts/win-x64/ -Recurse -Force
Compress-Archive -Path artifacts/win-x64/* -DestinationPath artifacts/SmartNoteAI-PoC-v0.1.1-win-x64.zip -Force
