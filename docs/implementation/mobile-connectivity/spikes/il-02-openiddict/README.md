# IL-02 spike: OpenIddict issuer over ASP.NET Core Identity

Throwaway proof, run 2026-11-13 on .NET SDK 10.0.404. Not production code. Kept so IL-03 starts from a working shape.

- `Server_Program.cs`: Identity (cookie sign-in, `RequireConfirmedAccount`) plus OpenIddict 7.8.0 server with device authorization for `csweet-hq` and authorization code + PKCE for `csweet-phone`, ephemeral keys, in-memory SQLite.
- `Tests_FlowTests.cs`: six end-to-end tests through `WebApplicationFactory`, all passing.

To rerun: put the four files back into `Server/` and `Tests/` folders with their original names (`Program.cs`, `Server.csproj`, `FlowTests.cs`, `Tests.csproj`) and run `dotnet test` in `Tests/`.

Results are recorded in [the decision log](../../decisions.md) under IL-02.
