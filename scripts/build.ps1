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
$publishDirectory = "artifacts/publish-v0.2.0-" + [Guid]::NewGuid().ToString("N")
Run { dotnet publish SmartNote.Desktop -c Release -r win-x64 --self-contained true --no-restore -o $publishDirectory -p:PublishReadyToRun=false }
Copy-Item *.md $publishDirectory -Force
Copy-Item docs $publishDirectory -Recurse -Force
Compress-Archive -Path "$publishDirectory/*" -DestinationPath artifacts/SmartNoteAI-PoC-v0.2.0-win-x64.zip -Force
