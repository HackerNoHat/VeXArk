# Threat model

- The ADB authorization boundary is not enough on its own. The agent additionally
  pairs a desktop public key and requires an on-device confirmation for restores.
- The agent binds only to loopback. ADB port forwarding is the only supported
  transport.
- Root operations are allow-listed. Package names and paths are validated before
  being passed to the helper.
- Repository metadata and chunks are authenticated encryption payloads.
- Restore rejects absolute paths, `..`, NULs, and symlink escapes.
- Keystore, Gatekeeper, lock credentials, eSIM, TEE/StrongBox and DRM data are
  permanent exclusions.
- A safety snapshot is required before overwriting existing application data.

