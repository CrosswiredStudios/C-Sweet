using CSweet.Contracts.Compute;

namespace CSweet.UI.Services;

internal sealed record ComputePreparationFailure(string Cause, string Recovery)
{
    public static ComputePreparationFailure Describe(ComputeSetupStatus setup) => setup.FailureCode switch
    {
        "administrator_approval_required" => new("The host needs administrator permission to prepare Linux compute.",
            "Continue setup and approve the system permission prompt on the host machine."),
        "setup_interrupted" => new("The installer stopped before preparation completed.",
            "Retry to validate and reuse any completed work."),
        "image_export_failed" => new("The image export failed.",
            "Check available host disk space and retry preparation."),
        "local_provider_unavailable" => new("Local Linux compute currently needs a Windows host with Hyper-V.",
            ""),
        "setup_components_unavailable" => new("The host is missing required setup components or its secure compute connection.",
            "Restore the host configuration before retrying."),
        "setup_configuration_invalid" => new("The compute service could not read its setup configuration.",
            "Update C-Sweet and retry preparation. The completed Linux image can be reused."),
        "compute_checks_build_failed" => new("C-Sweet could not compile the checks used to prepare Linux compute.",
            "Open installation.log in the setup folder, fix the build error it names, then retry preparation. The completed Linux image can be reused."),
        "compute_checks_failed" => new("The compute service checks failed before installation.",
            "Update or repair C-Sweet, then retry preparation. The completed Linux image can be reused."),
        "compute_provider_publish_failed" => new("The compute service software could not be prepared.",
            "Check the host build tools and available disk space, then retry preparation."),
        "compute_storage_unavailable" => new("Compute could not use the selected storage volume.",
            "Check the drive and its permissions, or choose another drive in Settings → Storage, then retry."),
        "compute_enrollment_failed" => new("The Linux image is ready, but the compute service could not connect to this business.",
            "Check the host installation and secure connection, then retry preparation."),
        "compute_service_start_failed" => new("The compute service was installed but could not start.",
            "Check the Windows service on the host, then retry preparation."),
        "compute_readiness_failed" => new("The compute service started but could not confirm readiness with C-Sweet.",
            "Check the host connection, then retry preparation."),
        _ when setup.Milestone == 5 || (setup.Milestone == 0 && setup.PreparationStep == 3) => new("The Linux image is ready, but compute service setup failed.",
            "Update or repair C-Sweet, then retry preparation to reuse the completed image."),
        _ => new("Linux compute setup stopped before the environment was ready.",
            "Review preparation activity, resolve any host setup issue, and retry. Completed images are verified before reuse.")
    };
}
