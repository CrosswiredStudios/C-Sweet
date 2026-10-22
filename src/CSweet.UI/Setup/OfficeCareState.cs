namespace CSweet.UI.Setup;

public static class OfficeCareState
{
    public static bool IsInProgress(string? state) =>
        state is "created" or "redeemed" or "connected" or "removalinprogress" or
            "preparing" or "stopping" or "installing" or "reconnecting" or "resuming" or "cancelling";
}
