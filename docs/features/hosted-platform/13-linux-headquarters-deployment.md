# Linux Headquarters deployment

The csweet repository owns the Headquarters application image. CSweet.Hosting owns provisioning and first-boot configuration. CSweet.Market owns orders, payments and the customer account page. A release and a purchase run at different times.

| When | What runs | Result |
|---|---|---|
| Reviewed source release | The [Linux Headquarters workflow](../../../.github/workflows/linux-headquarters.yml) publishes the Linux services and Blazor assets, builds the image and smoke-tests it against PostgreSQL. | A release tag or manual run publishes to GHCR. Copy the digest from the run summary into the provisioner's `HQ_IMAGE`. Pull requests run validation only. |
| Once per OS template | The Hosting container's `prepare-template` command imports Canonical's verified Ubuntu amd64 cloud image into a new TrueNAS disk and creates a stopped VM. | A reusable Linux baseline without a customer account, application release or tunnel token. |
| Per purchase | The Mac provisioner records the order and release, clones the VM, provides a per-order cloud-init seed, and creates an outbound tenant tunnel. | The guest migrates PostgreSQL, starts Headquarters and accepts first-run account linking only for the purchaser. |
| After verification | Hosting probes health and requests a signed response tied to the order, buyer, hostname, release and a fresh challenge. | Market publishes the HTTPS link in My Headquarters. |

To change the application version for future purchases, publish the reviewed revision and update the configured digest. Each accepted order retains its original version across retries. Existing tenant upgrades are a separate lifecycle operation. Rebuild the OS template when updating the Ubuntu baseline.

For the first hardware test, run Naomi on the Mac Studio, locate the running Market project and confirm the TrueNAS dashboard is reachable. The detailed operator steps, example settings, image verification, template import and teardown are in the CSweet.Hosting README. Real checkout currently covers Headquarters. Office enrollment and rented Office provisioning require separate gateway validation.
