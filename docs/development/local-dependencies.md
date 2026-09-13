# Local dependencies

C-Sweet detects sibling Agent SDK, Memory, WorkManagement.Contracts and Office.Contracts source checkouts through Directory.Build.props. Explicit UseLocalCSweetAgentSdk=false, UseLocalCSweetMemory=false, UseLocalCSweetWorkManagementContracts=false and UseLocalOfficeContracts=false select centrally pinned packages.

WebHost.Core, WebHost.Contracts and the WebPreviews client are retired dependencies. Shared CSweet.Isolation libraries remain available to the execution providers. Validate changed surviving packages with sibling references disabled before release.
