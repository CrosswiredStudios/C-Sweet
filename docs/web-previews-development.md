# Developing Web Previews from sibling checkouts

Clone the repositories into the same parent directory. No NuGet upload is needed to build this work locally.

```text
GitHub/
  csweet/
  CSweet.Isolation/
  CSweet.WebHost.Contracts/
  CSweet.WebHost/
  CSweet.Plugins.WebPreviews/
  CSweet.Office.Contracts/
  CSweet.Office/                 # When developing or checking Office
  CSweet.Agent.Sdk/              # Uses source when present; published SDK otherwise
```

Push the changed sibling repositories together so other developers receive matching source versions. No NuGet publication is required for this clone-based workflow.
Keep Office.Contracts current with Headquarters: the current source includes the upstream 0.6.0
certificate-recovery API plus the compatible 0.6.1 isolation/sibling-build changes.

From Headquarters:

```powershell
dotnet build CSweet.slnx -c Release
dotnet test tests/CSweet.UnitTests -c Release
dotnet build ../CSweet.WebHost/CSweet.WebHost.slnx -c Release
dotnet test ../CSweet.WebHost/tests/CSweet.WebHost.Tests -c Release
dotnet build ../CSweet.Plugins.WebPreviews/CSweet.Plugins.WebPreviews.slnx -c Release
```

Directory.Build.props detects sibling source for Isolation, WebHost, WebHost.Contracts and Office.Contracts.
The Web Previews plugin also detects the sibling Agent SDK. Software Developer and Video Game Engineer detect the plugin sibling and reference its separate Client project. BrowserProbe is inside WebHost; Client is inside WebPreviews. No additional repository is needed for either project. Explicitly supplied properties take precedence.
Missing checkouts fall back to the pinned packages; they are not downloaded as source or silently created.

For custom checkout locations, set CSweetIsolationRepositoryRoot, CSweetWebHostRepositoryRoot,
CSweetWebHostContractsRepositoryRoot, CSweetOfficeContractsRepositoryRoot CSweetWebPreviewsRepositoryRoot or CSweetAgentSdkRepositoryRoot
to the relevant repository directory. Automatic detection uses those roots, and project references use
the same paths.

Package-only release checks still use:

```text
-p:UseLocalIsolation=false
-p:UseLocalWebHost=false
-p:UseLocalWebHostContracts=false
-p:UseLocalOfficeContracts=false
-p:UseLocalCSweetAgentSdk=false
-p:UseLocalWebPreviews=false
```

These checks require the exact versions in a configured package feed. They remain mandatory because local
source builds can otherwise hide stale package versions. Office.Contracts is now 0.6.1 in both Office and
Headquarters; WebHost remains 0.2.0; WebHost.Contracts and WebPreviews are 0.3.0; Isolation remains 0.1.0. WebPreviews.Client is 0.3.0, Software QA is 0.8.0, Software Developer is 0.9.0 and Video Game Engineer is 2.6.0. These packages have not been published.

Building the projects does not install WebHost or launch product VMs. Runtime installation and certified guest images require the hardened release environment. Deployment and certification gates are tracked in
[the implementation checklist](implementation/web-previews.md).

Once source changes are pushed, clone missing siblings from the common parent directory:

```powershell
git clone https://github.com/CrosswiredStudios/CSweet.Isolation.git
git clone https://github.com/CrosswiredStudios/CSweet.WebHost.Contracts.git
git clone https://github.com/CrosswiredStudios/CSweet.WebHost.git
git clone https://github.com/CrosswiredStudios/CSweet.Plugins.WebPreviews.git
```

Also keep the existing Office.Contracts checkout current; it supplies the shared Office API needed by
Headquarters. Run the normal build commands above from the csweet directory.
