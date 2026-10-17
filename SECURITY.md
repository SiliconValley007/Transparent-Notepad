# Security Policy

Report vulnerabilities privately via GitHub **Security → Report a vulnerability** (or email the maintainer) — please do not open public issues for security problems.

Design notes: API keys are stored only on the user's PC, encrypted with Windows DPAPI (current user); the app has no backend; logs redact keys and tokens; native DLLs load from System32 only. Supported versions: the latest release.
