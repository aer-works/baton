# Deploys the fleet mailbox Worker (worker.js) using wrangler's stored OAuth credentials.
# Secrets (PUSH_TOKEN, READ_SEGMENT) are generated once into secrets.local.json and never printed.
# secrets.local.json is gitignored (#1413) -- run this locally; it is not part of any CI job.
#
# One-time prerequisite: `npx wrangler login` (opens a browser) so wrangler holds its own OAuth
# session -- this script never handles a Cloudflare credential itself.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 1. Generate tokens once (idempotent).
$secretsPath = Join-Path $PSScriptRoot "secrets.local.json"
if (-not (Test-Path $secretsPath)) {
    $alphabet = [char[]]((48..57) + (97..122))
    $rand = {
        $bytes = [byte[]]::new(40)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
    }
    @{ push_token = (& $rand); read_segment = (& $rand) } | ConvertTo-Json | Set-Content $secretsPath
}
$secrets = Get-Content $secretsPath | ConvertFrom-Json

# 2. Create KV namespace if placeholder still present.
$toml = Get-Content wrangler.toml -Raw
if ($toml -match "KV_ID_PLACEHOLDER") {
    $out = npx --yes wrangler kv namespace create FLEET 2>&1 | Out-String
    if ($out -match 'id\s*[=:]\s*"([0-9a-f]{32})"') {
        ($toml -replace "KV_ID_PLACEHOLDER", $Matches[1]) | Set-Content wrangler.toml -NoNewline
    } else {
        Write-Host $out
        throw "could not parse KV namespace id"
    }
}

# 3. Push secrets (values go via stdin, never argv).
$secrets.push_token   | npx --yes wrangler secret put PUSH_TOKEN
$secrets.read_segment | npx --yes wrangler secret put READ_SEGMENT

# 4. Deploy.
npx --yes wrangler deploy
