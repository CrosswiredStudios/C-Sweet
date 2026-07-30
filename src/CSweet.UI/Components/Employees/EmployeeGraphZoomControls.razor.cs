using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeGraphZoomControls
{
    [Parameter]
    public double Zoom { get; set; } = 1;

    [Parameter]
    public EventCallback ZoomInRequested { get; set; }

    [Parameter]
    public EventCallback ZoomOutRequested { get; set; }

    [Parameter]
    public EventCallback FitRequested { get; set; }

    protected string ZoomLabel => $"{Zoom:P0}";
    protected Task ZoomInAsync() => ZoomInRequested.InvokeAsync();
    protected Task ZoomOutAsync() => ZoomOutRequested.InvokeAsync();
    protected Task FitAsync() => FitRequested.InvokeAsync();
}
