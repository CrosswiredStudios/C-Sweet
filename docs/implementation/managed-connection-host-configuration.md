# Managed connection configuration across hosts

The API handles consent and resume requests; `PluginAccountSetupWorker` in AgentHost performs the import. Both need the same existing deployment registration. A database-backed service selection supplies the mode and origin, but intentionally does not supply the machine-local registration path or browser return address.

`ManagedConnectionHostConfiguration.ReadEnvironment` reads the resolved AppHost `CSweet:PluginConnections:Managed` section once and forwards only its deployment-client settings to API and AgentHost. AppHost's usual JSON, environment, user-secrets and command-line provider precedence is preserved. The forwarded environment settings take precedence over the children's JSON defaults. No other resource receives them. Owner-controller configuration, Google client credentials, provider tokens and unrelated configuration are excluded.

Configure these values together in the operator's local AppHost configuration (for development, AppHost user secrets or environment variables are suitable):

| Setting under `CSweet:PluginConnections:Managed` | Value |
| --- | --- |
| `Enabled` | Explicit `true` or `false` |
| `ServiceOrigin` | The exact paired HTTPS origin |
| `ReturnUri` | The existing browser return URL ending in `/settings/plugins/managed-return` |
| `RegistrationPath` | Absolute path to the **existing protected deployment registration** on this machine |
| `CustomerOwnedProfiles` | Optional array of provider profile IDs that use a customer-owned backend |

This change does not choose an origin, invent a path, prepare a key, pair a deployment, copy a credential or change the stored service selection. It does not read a registration while resolving AppHost settings. An absent AppHost section preserves standalone/legacy configuration. A present section must be complete, including when the configured `Enabled` default is false, so cleanup coordinates are retained. The stored service selection still controls the effective mode; this default does not override an explicit stored choice. Invalid, partial or unknown shared fields fail with the fixed `managed_shared_configuration_invalid` code; values are not included in the error.

AppHost also forwards a SHA-256 consistency hash of these non-secret settings. `AddCSweetInfrastructure` checks the final bound child options against it before gateway use. The hash is a configuration consistency check, not a credential or remote trust proof. A conflicting higher-priority child override or leftover array entries from a lower-priority child configuration fail with `managed_shared_configuration_conflict`. Remove conflicting per-child managed overrides rather than weakening the check. A deliberately different backend/profile selection requires consistent operator configuration in both consumers. Child projects launched independently still need their own complete configuration; AppHost wiring does not affect independent launches.

For the current Windows test setup, adoption requires moving the existing API managed settings into the **local AppHost configuration**, using the same protected registration file and approved localhost return. Do not publish personal paths or credential files. Existing compatible per-child defaults may remain, but avoid competing profile arrays. Pull/build and restart only under the operator's normal workflow. A further import resume is a separate action; the configuration fix never retries it automatically. There is no database migration, protocol/package release or Mac service change.

`AppHostManagedConnectionConfigurationTests` exercises host parity, configuration precedence, safe fixed errors, partial and conflicting settings, customer-owned profile arrays, disabled cleanup configuration and the two-consumer wiring. Account-import CI runs these checks on Windows and Linux alongside account authority, pairing, diagnostics and UI regressions, then compiles AppHost without starting it. These tests do not use live keys, databases or Google accounts.
