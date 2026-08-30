# Runbook: Shared Claude Config Root Login (BATON_CLAUDE_CONFIG_ROOT)

AER supports isolating Claude Code state into an operator-chosen shared configuration root via the environment variable `BATON_CLAUDE_CONFIG_ROOT` (#442).

When `BATON_CLAUDE_CONFIG_ROOT=<abs path>` is set in the environment AER runs under, every spawned `claude` process receives `CLAUDE_CONFIG_DIR=<that path>` injected into its environment.

## One-Time Operator Login Requirement

Redirecting `CLAUDE_CONFIG_DIR` to a fresh root isolates conversation state and project memory from the host's primary `~/.claude` directory, but the new root begins without subscription credentials (`durability.config-dir-redirect-breaks-auth`).

Before dispatches under `BATON_CLAUDE_CONFIG_ROOT` can succeed, the operator must perform a one-time interactive login under the new root.

### Performing the Login

In PowerShell:
```powershell
$env:CLAUDE_CONFIG_DIR = "C:\path\to\shared-claude-root"
claude auth login
```

In cmd.exe:
```cmd
set CLAUDE_CONFIG_DIR=C:\path\to\shared-claude-root
claude auth login
```

In bash/zsh:
```bash
CLAUDE_CONFIG_DIR=/path/to/shared-claude-root claude auth login
```

Follow the browser OAuth prompt to complete authentication.

## Failure Mode Before Login

If `BATON_CLAUDE_CONFIG_ROOT` points to a directory where `claude auth login` has not been completed, every dispatched worker fails loudly at CLI invocation time with:

- Output / Stderr: `Not logged in`
- Process Exit Code: `1`

This failure is loud and immediate, preventing unauthenticated workers from running ungated.
