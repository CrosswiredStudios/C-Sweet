# Local dependency checkouts

C-Sweet automatically references `../CSweet.WebHost/src/CSweet.WebHost.Core/CSweet.WebHost.Core.csproj` when present. Override its location with `CSweetWebHostRepositoryRoot`, or set `UseLocalWebHost=false` to use the centrally pinned package.

WebHost automatically detects sibling `CSweet.WebHost.Contracts` and `CSweet.Isolation` source projects. WebHost.Contracts also detects its sibling Isolation dependency. Their explicit `UseLocalWebHostContracts=false` and `UseLocalIsolation=false` settings select packages instead. Each repository owns detection of its direct dependencies so both restore and build see the same references.

The expected dependency chain is C-Sweet → WebHost.Core → WebHost.Contracts → Isolation.Security. A folder containing only `.git` does not count as available source. Package versions remain pinned for package-based builds.

Local-reference defaults for WebHost and WebHost.Contracts live in those repositories' `Directory.Build.props`; include those changes when sharing the development setup.
