# RelayForge Agent Instructions

Follow `C:\Users\Yuri\.codex\AGENTS.md` and the workspace instructions in the parent vault.

## Engineering gates

- Target .NET 8 and use PowerShell-compatible commands.
- Follow test-driven development for behavior changes.
- Keep PostgreSQL as the source of truth.
- Do not promise exactly-once delivery.
- Run build, tests and Slopwatch before each commit.
- Never add real credentials, customer data or private domain rules.
- Do not commit, push or open pull requests unless the active user request authorizes it.
