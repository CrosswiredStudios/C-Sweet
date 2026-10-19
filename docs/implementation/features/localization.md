# Localization

C-Sweet's UI (`CSweet.UI`, shared by the WebAssembly app and the MAUI app) is localized through
JSON resource files with ICU MessageFormat. Agents are asked to write user-facing prose in the
user's language through the same preference. This page is the contract for anyone adding UI text.

## Supported cultures

`CSweet.Contracts.Localization.SupportedCultures` is the single list (Tier 1: `en`, `zh-Hans`, `es`,
`ja`, `de`, `fr`, `pt-BR`, `ar`, `ru`, `ko`). It drives the language picker, API validation of
`PreferredLanguage`, and `tools/localization/cultures.json` must match it. Adding a language means
adding it there, creating `<culture>.json` in every resource area, and (for RTL) setting `RightToLeft`.

## Where the preference lives

- `ApplicationUser.PreferredLanguage` / `TimeZoneId` (nullable: follow the device). Migration
  `UserLocalePreferences`. Read/written through `GET/PUT /api/auth/preferences`
  (`IAuthenticationService.GetPreferencesAsync/UpdatePreferencesAsync`) and surfaced on
  `AuthStatusResponse` so the UI knows it on the first session call.
- Browser/device: `localStorage` keys `csweet.language`, `csweet.timezone` (via
  `wwwroot/js/localization.js`).
- Resolution order in `AppCulture.InitializeAsync`: account preference → stored choice → device
  language (`navigator.language` mapped by `SupportedCultures.Normalize`) → `en`. After sign-in the
  account preference wins and is written back to local storage.
- **Detected once, at account creation.** `Register.razor` sends `AppCulture.CurrentCode` (the
  device language, or whatever the person picked in the signed-out language menu) and the browser's
  IANA time zone on `RegisterAdminRequest.PreferredLanguage/TimeZoneId`. The `/register` endpoint
  (`AuthenticationEndpoints.WithInitialLocale`) falls back to the first supported `Accept-Language`
  tag when the client sent none, drops unsupported languages and unknown time zones, and the values
  are stored on the new `ApplicationUser`. Nothing re-detects afterwards: only Settings → Language &
  region changes them, so a person's override is never reverted. Accounts created before this
  (null preference) keep following the device until they pick a language.

## Runtime pieces (`src/CSweet.UI/Localization`)

| Type | Role |
|---|---|
| `AppCulture` | Current culture/time zone, `IsRightToLeft`, `Changed` event, `SetLanguageAsync`/`SetTimeZoneAsync` (persist locally and to the account). Sets `CultureInfo` defaults and `<html lang dir>`. |
| `IAppLocalizer` / `AppLocalizer` | `L["Key"]`, `L.Format("Key", new { count })`, `L.Has`. Falls back to English, then to the key. Missing keys are logged once to the console. |
| `LocalizationResources` | Loads embedded `Localization/Resources/<Area>/<culture>.json` and merges areas per culture. |
| `IcuMessageFormatter` + `PluralRules` | ICU subset: arguments, number/date/time, plural (with offset and `=n`), selectordinal, select, apostrophe quoting. |
| `UserTime` | `Format(DateTimeOffset, "g")` in the user's zone and culture. Use instead of `ToLocalTime().ToString("g")`. |
| `CSweetMudLocalizer` | Bridges MudBlazor's built-in strings to the `Mud` area. |

`Routes.razor` awaits `AppCulture.InitializeAsync()` before rendering and re-keys the component tree
on `CurrentCode`, so a language change re-renders everything without a reload. `MainLayout` and
`AnonymousLayout` wrap content in `MudRTLProvider`. `CSweet.App` loads the full ICU data set
(`BlazorWebAssemblyLoadAllGlobalizationData`) because the UI culture can differ from the browser's.

## Resource files

`src/CSweet.UI/Localization/Resources/<Area>/<culture>.json` — one flat object per file.

- **Area** = a feature folder or page family (`Settings`, `WorkBoards`, `Employees`, `Auth`,
  `Layout`, …). `Common` holds verbs and fragments reused in three or more places; `Mud` holds
  MudBlazor keys.
- **Key** = `<Component>.<Name>` (`LanguageSettings.Title`, `NavMenu.Projects`). Keys are global
  across areas; the component prefix keeps them unique. Sub-structure with dots
  (`Settings.NoMatches.Title`), never nested JSON objects.
- Keys starting with `_` are translator notes (`"_Foo.Bar": "…"` describes `Foo.Bar`); `"_"` describes
  the file. Notes are not loaded at runtime.
- Values are ICU messages. Use `{name}` arguments (never positional), `{count, plural, one {…} other {…}}`
  for anything counted, `{kind, select, …}` for enum-driven wording. Apostrophes: `''` is a literal
  apostrophe only when quoting is needed; `don't` is fine as written. A literal brace is `'{'`.
- Do not put HTML in values. Split around markup instead (`@L["X.Before"] <a>@L["X.Link"]</a>`).
- Do not concatenate fragments in code to build a sentence; word order differs by language. Use one
  message with arguments.
- Enum/status labels: one key per value (`WorkStatus.Blocked`), resolved as `L["WorkStatus." + status]`.
  Keep those areas listed under a `_dynamic` note so the checker's "unused key" warning is understood.
- Text that must stay English: identifiers, code, provider/product names, log excerpts, raw error
  payloads from the API, file names, `data-*` attributes, CSS classes, test ids.

## Extraction conventions in components

```razor
@inject IAppLocalizer L
<MudText Typo="Typo.h4">@L["Employees.Title"]</MudText>
<MudButton Disabled="@_saving">@L["Common.Save"]</MudButton>
<MudTextField Label="@L["Employees.SearchLabel"]" />
<p>@L.Format("Employees.Count", new { count = _employees.Count })</p>
<span title="@L["Employees.LastSeen"]">@Time.Format(employee.LastSeenAt, "g")</span>
```

- `@inject UserTime Time` for dates and numbers; the `"g"`, `"d"`, `"t"`, `"f"` format strings keep
  working but now follow the user's culture and zone.
- `PageTitle` content goes through `L` too (`"<Page>.PageTitle": "… - C-Sweet"`).
- `aria-label`, `title`, `placeholder`, `alt` (when meaningful) are user-facing and get keys.
- Static classes that hold display text (`*Presentation.cs`, `CapabilityDisplayNames.cs`,
  `SettingsSections.cs`) return **keys**; the component resolves them with `L[...]`.
- Strings passed to MudBlazor dialogs/snackbars are resolved at the call site.
- A component test that renders a component which injects `IAppLocalizer` must call
  `services.AddCSweetLocalization()` on its `ServiceCollection` (English resources load from the
  embedded files, so existing English assertions keep passing). Tests that construct a component
  directly use `TestLocalization.Inject(new X())`; source-text assertions on `.razor` files use
  `LocalizedSource.AssertContains(text, source)`, which also accepts a referenced resource key whose
  English value contains the text.
- Never split a sentence around a value (`@L["Delete"] <b>@name</b> @L["…rest"]`): word order differs
  by language. Use one message with a placeholder: `@L.Format("Organizations.DeleteConfirm", new { name })`.

## Tooling (`tools/localization`)

- `node tools/localization/check-resources.mjs [--strict]` — JSON validity, duplicate keys,
  keys referenced in source but missing from English, per-culture missing/orphan keys, placeholder
  parity. `--strict` turns missing translations and unreferenced English keys into errors. Run it
  before handoff of any UI change.
- `node tools/localization/translate.mjs` — fills missing or stale translations through an
  OpenAI-compatible endpoint (`CSWEET_TRANSLATE_ENDPOINT`, `CSWEET_TRANSLATE_MODEL`,
  optional `CSWEET_TRANSLATE_API_KEY`), honoring `glossary.json` and the plural categories of each
  language. Staleness is tracked in `<Area>/translations.<culture>.lock` (sha1 of the English text per key).
  `--culture`, `--area`, `--dry-run`, `--force`.
- `glossary.json` — mandatory renderings for domain terms. Add a term before it drifts.
  A standalone "Chief" always means the Chief of Staff; "Enterprise" is the host level above
  businesses and stays distinct from "business".
- `python3 tools/localization/stage.py export <Area> <culture>` / `apply <Area> <culture> <file>` —
  manual or assistant-driven translation: export lists numbered English values that are missing or
  stale for a culture, apply imports `{"<number>": "<translation>"}` with placeholder and plural
  validation. Shares the per-culture lock files with `translate.mjs`.
- `python3 tools/localization/extract.py <Area> <files…> [--report]` — conservative extractor that
  moves markup text, common text attributes (`Label`, `Title`, `aria-label`, `placeholder`, …) and
  message assignments (`_error = "…"`, `Snackbar.Add("…")`) into `<Area>/en.json`. It skips Razor
  directives, C# expressions, interpolated strings and markup-mixed text; `--report` lists what is
  left for hand conversion. Review its diff: it cannot tell data from display text in `@code`.

## Agents and language

Every agent inference request flows through Headquarters (`PlatformLlmCapabilityHandler`, capability
`platform.llm.chat.stream`). The handler appends `AgentLanguageInstructions.For(code)` to the combined
instructions, next to the forecast-driven-planning and case instructions, so agent packages need no
change and no SDK bump: chat replies, plans, briefings, work-card prose, review notes and incident
reports come back in the workspace language while JSON keys, enum-like values, identifiers, code,
branch/file names and commit messages stay English (the UI localizes those). For English the
instruction is omitted, so existing prompts are unchanged.

`WorkspaceLanguagePolicy.ResolveAsync(db, session.BusinessId)` picks the language: user-scoped agents
(`user:<id>`, the Personal Assistant) follow that user; business agents follow the business owner
(highest-permission human member linked to an account), then the root administrator, then English.
Answers are cached for 30 s and dropped when a preference is saved. Per-turn language (a second
member writing in another language) is handled by the instruction itself: agents answer in the
language a person wrote in.

Quality caveat: small models write well in `es`, `de`, `fr`, `zh-Hans`, `ja`; `ar`, `ru`, `ko`
should be benchmarked per model before being recommended.

## Not yet localized

- API-generated human text (validation messages, `AuthActionResponse.Message`, notification bodies)
  — the UI maps known `ErrorCode`s to resource keys; unknown messages show as received.
- Account e-mails, the Discord relay, the MAUI shell (`MainPage.xaml`), the launcher script, docs.
- A pseudo-locale for layout testing.
