export function submitManifest(form, businessId) {
    if (businessId) sessionStorage.setItem("csweet.githubSetupBusiness", businessId);
    form.requestSubmit();
}

export function takeBusinessId() {
    const businessId = sessionStorage.getItem("csweet.githubSetupBusiness");
    sessionStorage.removeItem("csweet.githubSetupBusiness");
    return businessId;
}
