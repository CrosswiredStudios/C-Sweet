# Windows Hyper-V Satellite Office onboarding

Windows Hyper-V image building, certification, RuntimeHost installation, repair, smoke tests, and signed MSI creation are owned by the `CrosswiredStudios/CSweet.SatelliteOffice` repository.

From C-Sweet Headquarters, create a one-use Satellite Office enrollment, download the signed Windows x64 MSI from the release manifest, install it with administrator approval, verify the claimed fingerprint, and approve the office. Headquarters does not bundle or launch the runtime payload.

For implementation and certification commands, use the Satellite Office repository's `scripts/windows` directory and README. Upgrades require the office to be drained with zero active assignments.
