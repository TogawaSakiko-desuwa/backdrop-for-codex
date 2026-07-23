namespace BackdropForCodex.App.Models;

public enum WallpaperOperationStage
{
    Idle = 0,
    Saving,
    Updating,
    Validating,
    Launching,
    Discovering,
    Applying,
    Restoring,
    Resetting,
}

/// <summary>
/// Immutable progress for one foreground wallpaper operation.
/// </summary>
public sealed class WallpaperOperationProgress
{
    private WallpaperOperationProgress(
        WallpaperOperationStage stage,
        bool isBusy,
        bool canCancel,
        bool isCancellationRequested)
    {
        Stage = stage;
        IsBusy = isBusy;
        CanCancel = canCancel;
        IsCancellationRequested = isCancellationRequested;
    }

    public WallpaperOperationStage Stage { get; }

    public bool IsBusy { get; }

    public bool CanCancel { get; }

    public bool IsCancellationRequested { get; }

    public static WallpaperOperationProgress Idle { get; } =
        new(
            WallpaperOperationStage.Idle,
            isBusy: false,
            canCancel: false,
            isCancellationRequested: false);

    public static WallpaperOperationProgress Begin(
        WallpaperOperationStage stage = WallpaperOperationStage.Validating)
    {
        if (!Enum.IsDefined(stage) || stage is WallpaperOperationStage.Idle)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stage),
                "An operation must begin at a defined, non-idle stage.");
        }

        return new WallpaperOperationProgress(
            stage,
            isBusy: true,
            canCancel: true,
            isCancellationRequested: false);
    }

    public WallpaperOperationProgress AdvanceTo(WallpaperOperationStage stage)
    {
        if (!IsBusy)
        {
            throw new InvalidOperationException("No wallpaper operation is in progress.");
        }

        if (!Enum.IsDefined(stage) ||
            stage is WallpaperOperationStage.Idle ||
            stage < Stage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stage),
                "A wallpaper operation cannot move backward or advance to Idle.");
        }

        return new WallpaperOperationProgress(
            stage,
            isBusy: true,
            canCancel: !IsCancellationRequested,
            isCancellationRequested: IsCancellationRequested);
    }

    public WallpaperOperationProgress RequestCancellation()
    {
        if (!IsBusy || IsCancellationRequested)
        {
            return this;
        }

        return new WallpaperOperationProgress(
            Stage,
            isBusy: true,
            canCancel: false,
            isCancellationRequested: true);
    }

    public WallpaperOperationProgress Complete() => IsBusy ? Idle : this;
}
