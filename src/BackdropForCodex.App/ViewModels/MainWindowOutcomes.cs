namespace BackdropForCodex.App.ViewModels;

public enum UiStatusTone
{
    Informational = 0,
    Success,
    Warning,
    Error,
}

public enum AutoLaunchOutcome
{
    Applied = 0,
    NeedsMedia,
    NeedsRiskAcknowledgement,
    Failed,
}
