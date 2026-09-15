# Office runtime update checks

The Offices page checks the available source used to install each Office.

## Local assisted setup

On the configured Windows development host, initial setup builds and certifies a payload from the sibling
Office repository. The update check reads that source's VersionPrefix from Directory.Build.props and
compares it with the Office's last reported runtime version. A newer version offers Update even when
GitHub has no published releases. The page labels this source as local setup.

The local update action uses the same elevated setup builder as first installation. It requires a drained
Office with zero active assignments, rebuilds the payload, and invokes the installer with the upgrade
action and no enrollment token. This preserves the existing Office identity. The installed configurator
is not used to rebuild the source because its packaged payload may be older.

Local candidates require the configured launcher and bootstrap scripts to exist and match this server's
machine, Windows OS, and process architecture. Remote Offices cannot use this source.

## Published installers

Other Offices use CSweet:ExecutionFleet:ReleaseManifestUrl over HTTPS, with a ten-second timeout and
a one-MiB response limit. Schema 1 and protocol 1.0 are required. Assets match OS and architecture.
Missing or inaccessible releases remain unavailable for remote Offices; local setup can still proceed.
A failed lookup never means an Office is current. Equal or newer installed versions do not offer downgrades.

GitHub currently has no published Office releases. The first signed release must publish installers and
office-release.json through the protected workflow. It reads the runtime and contracts versions from
Directory.Build.props and Directory.Packages.props and rejects mismatched tags. The current candidate is
Office 0.5.2 with contracts 0.7.0.

## Runtime version reporting

Office 0.5.2 sends its running Node assembly version on HTTP and gRPC heartbeats using contracts 0.7.0.
Headquarters persists the reported value and refreshes the UI. Earlier runtimes omit the new field;
their last recorded version is retained. Local source changes need version bumps to be detected.

Validation includes local-source discovery when GitHub returns 404, remote-office exclusion, numeric
version comparison, heartbeat updates, source-to-launch routing, and existing maintenance checks.
An actual elevated installation is not performed by these tests.

## Upgrade preflight regression (Office 0.5.3)

A drained Office could report zero headquarters assignments while retaining authorization records and
powered-off Hyper-V VMs. The general recovery probe treated any such record or VM as active and rejected
the update before building a payload. Upgrade mode now requires a local drain and no active markers,
recognizes Hyper-V handles, and checks the matching/owned VMs' actual power states. Only absent or Off
VMs may be preserved; Running, Saved, unknown states and unknown providers block the update. Records and
VMs are not deleted by the probe. Removal/re-enrollment continues to use the stricter general probe.
The installer repeats the upgrade check immediately before applying changes.

The local launcher now preserves structured API rejection messages. The Office care dialog correctly
binds its status text and distinguishes update failures from repair failures. The runtime candidate is
0.5.3. The corrected read-only probe returned clean on the affected machine; its old general probe
returned active. The live install still requires Windows administrator approval.

Validation: 106 headquarters tests and 170 Office tests passed, including eight mocked Windows probe
scenarios run through the real PowerShell script. The probe was also checked against the affected
machine with administrator access.
