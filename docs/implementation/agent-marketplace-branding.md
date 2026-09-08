# Agent marketplace branding

Agents can customize their marketplace cards in `csweet-plugin.json` with optional
`catalog` metadata. Supported by C-Sweet with Agent SDK **3.36.0**.

```json
{
  "publisher": { "id": "com.example", "name": "Example Company" },
  "catalog": {
    "summary": "Turns ideas into a prioritized product plan.",
    "longDescription": "Defines the product direction, organizes discovery, and delivers requirements and a roadmap your team can act on.",
    "imageUrl": "https://assets.example.com/product-manager.webp",
    "companyLogoUrl": "https://assets.example.com/company-logo.svg",
    "accentColor": "#1C6252"
  }
}
```

This is a fragment to merge into an existing valid manifest; keep the required role,
license, runtime, protocol, and capability declarations.

| Field | Meaning | Default |
| --- | --- | --- |
| `imageUrl` | Agent portrait, illustration, or branded artwork | Bundled neutral agent avatar |
| `companyLogoUrl` | Publisher/company logo | `publisher.name` as text |
| `accentColor` | Six-digit `#RRGGBB` accent | C-Sweet green (`#1C6252`) |
| `longDescription` | Plain text for expanded card and profile; up to 2,000 characters | `summary` |

All fields are optional. Omitted, null, and blank values preserve defaults. Manifest images
require absolute HTTPS URLs of at most 2,048 characters without embedded credentials.
Failed image requests also use the corresponding fallback. Use square artwork with
the subject near the center; the front crops it to fill the image area. Company logos
are contained, keeping their aspect ratio. Existing `iconUrls` keep their icon meaning
and are not assumed to be portraits or publisher logos.

The collapsed card shows image, publisher branding, name, role, short summary, and
an explicit Explore agent control. Hover, keyboard focus, or tap opens the sliding
panel. Escape, Close, or a tap outside dismisses it. Moving between the panel and its
actions keeps it open. Reduced-motion preferences disable the transition.

Meet Agent opens a profile. Review and hire continues the existing hiring review;
unavailable/disabled agents stay visible with their state but cannot be hired.
Long descriptions scroll inside the detail area while the Meet Agent action stays
visible. Description text is HTML-escaped. Accent values cannot inject CSS; button
foreground colors are chosen for contrast.

Installed and local manifests, `CSweet:Marketplace:FirstPartyAgents` entries (same
field names), and remote discovery responses carry the optional fields. Existing
catalog entries work without changing their configuration. Remote publishers must
serve these fields in the discovery response to customize their pre-install cards.
No database migration or reconfiguration of existing installations is required;
new manifest metadata takes effect when that agent version is imported/refreshed.

## Verification

The SDK release must be packed and published before consumers restore 3.36.0 from
NuGet. For local verification, add the generated package directory as a restore
source and set `UseLocalCSweetAgentSdk=false` to verify the actual package contract.

## Temporary first-party portraits

The first-party JSON catalog is a temporary bridge while the live marketplace is
unfinished. The intended long-term sources are the live marketplace and downloaded,
locally installed agents for air-gapped systems. This artwork update does not change
catalog discovery or implement that future migration.

All 22 current entries in `src/CSweet.Api/first-party-agents.json` have a unique
portrait and a clothing-coordinated accent. Portraits are bundled as 768 × 768 JPEGs
in `src/CSweet.UI/wwwroot/images/agents/`, with versioned filenames. These temporary
entries use `_content/CSweet.UI/images/agents/<slug>-v1.jpg` so images work without an
external image host. The UI permits only that narrowly defined local portrait path
in addition to HTTPS URLs; publisher manifest validation is unchanged. This does
not yet add a general offline asset importer for downloaded agent packages.

The collection was generated with the built-in image generation tool. Reproducible
art direction, individual prompts, roles, and accent colors are recorded in
[the portrait prompt set](../design/agent-portrait-prompts.json). Missing or failed
portraits still use the default avatar; missing logos still show the company name.

## Agent names and portable branding

The 22 first-party entries now use realistic fictional names; `RoleName`, role keys,
agent IDs, and listing slugs retain their existing meaning. The corresponding root
agent manifests use the same names, and agents with a compiled default display name
have that default synchronized. Existing installed metadata is refreshed through
the normal import/update flow.

Every catalog agent repository includes `assets/branding/portrait-v1.jpg`,
`assets/branding/csweet-icon.svg`, and an asset README. Manifest `catalog.imageUrl`
and `catalog.companyLogoUrl` point to those files on the repository's `main` branch,
with the matching `catalog.accentColor`. These links become available after pushing
the changes; this update does not publish them. The embedded catalog continues to
use bundled assets, including the shared `_content/CSweet.UI/images/csweet-icon.svg`.
The logo is byte-for-byte identical to the app's `/images/icon.svg`.

| Agent | Role |
| --- | --- |
| Arjun Mehta | Infrastructure Engineer |
| Evelyn Brooks | Chief of Staff |
| Maya Patel | Product Manager |
| Naomi Chen | Creative Director |
| Gabriel Reyes | Video Game Producer |
| Daniel Kim | Software Developer |
| Claire Morgan | Software Architect |
| Priya Shah | Software QA |
| Marcus Bennett | Video Game Art Director |
| Clara Hayes | Video Game Artist |
| Samir Haddad | Video Game Audio Designer |
| Avery Coleman | Video Game Build Release Engineer |
| Adrian Silva | Video Game Engineer |
| Hana Mori | Video Game Designer |
| Owen Reed | Video Game Level Designer |
| Nadia Ellis | Video Game Narrative Designer |
| Elena Torres | Video Game Playtest Researcher |
| Rohan Desai | Video Game QA |
| Leila Morgan | Video Game Technical Artist |
| Victor Lin | Video Game Technical Director |
| Julia Bennett | Video Game UI UX Accessibility Designer |
| Jordan Mitchell | YouTube Account Manager |
