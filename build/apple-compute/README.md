# Generic Compute VMs on a Mac Studio

An enrolled Mac Office supplies the VM, and Headquarters authorizes its use. The Studio runs a private writable copy of an approved Ubuntu arm64 image in Apple Virtualization.framework. The guest accepts Compute commands over virtio socket port 2763. Each VM has zero network adapters. The image includes Docker Engine and Python 3. Its advertised feature is `docker`. Bring workload inputs into the image if they would otherwise require downloads.

## Build and test from source

Use macOS 14 or later on Apple silicon, Xcode command-line tools, .NET 10 and Docker with its Linux engine running. From the Headquarters checkout run:

```sh
docker buildx build --platform linux/arm64 -f build/apple-compute/Dockerfile \
  --output type=local,dest=artifacts/apple-compute .
shasum -a 256 artifacts/apple-compute/compute.img
```

The output includes the raw ext4 disk, kernel and initrd. The installed Mac Office supplies the kernel and initrd at VM boot. Use matching kernel builds for the Office and Compute images. The hardware test checks the kernel match and boots using the Office kernel and initrd.

Check out the matching Office sources alongside Headquarters. From the Headquarters checkout, run the Studio hardware test:

```sh
../CSweet.Office/scripts/macos/test-compute-vm.sh artifacts/apple-compute \
  ../CSweet.Office/artifacts/apple-guest ../CSweet.Office/artifacts/macos-arm64-acceptance.json
```

The last command requires genuine hardware acceptance evidence for the matching Office agent guest. It prompts for a local administrator to create a disposable root-owned RuntimeHost fixture. It pins a temporary Headquarters public key, signs the images with a temporary test key, and checks guest readiness, command execution, duplicate delivery, stop, start, restart and confirmed destruction.

## Use the Studio as a business Compute provider

1. Install the updated Mac Office with its certified agent guest, enroll it with Headquarters, and approve its execution pool for the business. Deploy the Headquarters changes to the API and execution gateway.
2. Have the operator who holds the private image-signing key for that Office sign the Compute disk. The accepted formats are RSA PKCS#1 v1.5/SHA-256 and ECDSA P-256/SHA-256 with fixed-width signatures. Keep the private key at the signer. On the Studio run `sudo /usr/local/sbin/csweet-configure-compute BUSINESS_UUID /absolute/path/compute.img /absolute/path/compute.img.sig`. The command prints the exact SHA-256 digest and configures one slot, two CPUs, 2 GiB RAM and a 20 GiB private disk.
3. Add an entry to the `CSweet:OfficeCompute:ApprovedImages` array in the Headquarters configuration used by the gateway and API. Retain existing business approvals. Use the same business UUID, the printed digest and the exact feature list.

   ```json
   {
     "OrganizationId": "YOUR-BUSINESS-UUID",
     "ImageDigest": "sha256:YOUR-64-HEX-DIGEST",
     "Features": ["docker"]
   }
   ```

4. Apply the Headquarters configuration. After draining any active Office work, reload its runtime with `sudo launchctl kickstart -k system/com.csweet.office.runtime`. Once the Office reports capacity, open **Compute**, select an eligible agent and project, and create a Linux instance. Headquarters derives arm64 from the selected template. Explicit API callers should request `linux` and `arm64`.

Stopping or restarting a VM retains its private disk. Destruction or expiry of a finite ephemeral lease removes it. Other machines need the matching source changes or releases, a certified Mac Office and an operator-signed Compute image.
