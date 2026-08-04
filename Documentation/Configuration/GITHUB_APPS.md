# Configure GitHub Apps

C-Sweet uses two installation-wide GitHub Apps:

- **Source Access** connects repositories selected by each business.
- **Repository Provisioner** is optional and lets C-Sweet create governed private repositories.

System administrators should normally configure these Apps from **Settings → Source control**. The
guided setup creates preconfigured Apps through GitHub, securely receives and encrypts their private
keys, verifies them, and activates the trusted services without terminal commands or a restart.

## Guided setup

1. Sign in to C-Sweet as a system administrator and open **Settings → Source control**.
2. Select **Start guided setup**.
3. Enter the central GitHub organization that should own the Apps and confirm that you can create
   GitHub Apps there.
4. Review the Source Access permissions and continue to GitHub. GitHub shows the generated App name
   and settings before anything is created.
5. After GitHub returns to C-Sweet, verify the sanitized App identity and confirm it.
6. Choose whether to connect existing projects only or also configure managed private projects.
7. If managed projects are selected, repeat the review and GitHub confirmation for the separate
   Repository Provisioner App.
8. Review the verified Apps and select **Activate source control**.

Progress is stored for 24 hours. A GitHub handoff link is valid for 20 minutes and can be regenerated
without exposing a private key. If C-Sweet receives the key but cannot reach a trusted host, the key
remains encrypted and the administrator can retry verification or activation.

The browser-visible application base URL supplies the manifest homepage, callback, and setup URLs.
Reverse proxies must preserve the public scheme, host, and path base. HTTPS is required except for a
loopback development URL.

## Permissions created by the wizard

The Source Access App requests only these repository permissions:

- Contents: read and write
- Pull requests: read and write
- Checks: read-only
- Metadata: read-only

The Repository Provisioner App requests only:

- Repository administration: read and write

Both Apps have webhooks and user authorization disabled and subscribe to no events. They are publicly
installable so separate C-Sweet businesses can authorize their own GitHub organizations; public
installability does not grant repository access. Each installation is still accepted only through an
authenticated business onboarding session and is scoped to the repositories selected on GitHub.

Never return, display, log, or commit an App private key, manifest conversion response, client secret,
webhook secret, trusted-service key, installation token, or protected credential value.

## Advanced manual setup

Use manual configuration only for recovery, an externally managed deployment, or GitHub Enterprise
Server. The same commands are available in the collapsed **Advanced manual setup** section of the
enterprise page.

For a local AppHost, create each App manually, generate its PEM, and use .NET user-secrets. Example
for Source Access:

```powershell
$sourcePem = [Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\secure\csweet-source.pem'))
$trustedKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))

dotnet user-secrets set 'CSweet:SourceControl:TrustedServiceKeyBase64' $trustedKey --project src/CSweet.AppHost
dotnet user-secrets set 'CSweet:SourceControl:SourceAccessAppId' '123456' --project src/CSweet.AppHost
dotnet user-secrets set 'CSweet:SourceControl:SourceAccessPrivateKeyBase64' $sourcePem --project src/CSweet.AppHost
dotnet user-secrets set 'CSweet:SourceControl:SourceAccessInstallUrl' 'https://github.com/apps/APP-SLUG/installations/new' --project src/CSweet.AppHost
```

The equivalent deployed configuration names are:

- `CSweet__SourceControl__TrustedServiceKeyBase64`
- `CSweet__SourceControl__SourceAccessAppId`
- `CSweet__SourceControl__SourceAccessPrivateKeyBase64`
- `CSweet__SourceControl__SourceAccessInstallUrl`
- `CSweet__SourceControl__ProvisionerAppId`
- `CSweet__SourceControl__ProvisionerPrivateKeyBase64`
- `CSweet__SourceControl__ProvisionerInstallUrl`

Store deployed values in the deployment platform's secret manager, never plaintext configuration or
source-controlled deployment files. Externally configured credentials remain supported and appear as
**Externally managed**; C-Sweet does not automatically import or replace them.
