export function submitManifest(form, businessId, draftId) {
    if (businessId) sessionStorage.setItem("csweet.githubSetupBusiness", businessId);
    else sessionStorage.removeItem("csweet.githubSetupBusiness");
    if (draftId) rememberDraftId(draftId);
    else sessionStorage.removeItem("csweet.githubSetupDraft");
    form.requestSubmit();
}

export function takeBusinessId() {
    const businessId = sessionStorage.getItem("csweet.githubSetupBusiness");
    sessionStorage.removeItem("csweet.githubSetupBusiness");
    return businessId;
}

export function rememberDraftId(draftId) {
    if (draftId) sessionStorage.setItem("csweet.githubSetupDraft", draftId);
    return sessionStorage.getItem("csweet.githubSetupDraft");
}

export function takeDraftId() {
    const draftId = sessionStorage.getItem("csweet.githubSetupDraft");
    sessionStorage.removeItem("csweet.githubSetupDraft");
    return draftId;
}
