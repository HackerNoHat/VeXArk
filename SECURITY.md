# Security policy

## Supported versions

VeXArk is pre-1.0 software. Security fixes are applied to the latest published
release only.

## Reporting a vulnerability

Do not open a public issue for a vulnerability that could expose backup contents,
private keys, root access or arbitrary file operations.

Use GitHub's **Private vulnerability reporting** for this repository. Include:

- affected VeXArk version and platform;
- device/ROM/Android version when relevant;
- impact and reproducible steps;
- a minimal proof of concept without real personal data.

You should receive an acknowledgement within seven days. Please allow a
reasonable remediation window before public disclosure.

## Security boundaries

- The Agent listens only on loopback and is reached through `adb forward`.
- A desktop must be explicitly trusted on-device.
- Every restore requires a new on-device confirmation.
- The root helper exposes a fixed command set and path allowlist, not a shell.
- Backups are encrypted before being committed to the repository.
- Passwords, recovery phrases and private signing keys must never be included in
  issues, logs or screenshots.

Read [the threat model](docs/THREAT_MODEL.md) for the current assumptions.

