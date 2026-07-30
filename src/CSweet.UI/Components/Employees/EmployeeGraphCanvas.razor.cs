using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeGraphCanvas : IAsyncDisposable
{
    private ElementReference _viewport;
    private ElementReference _content;
    private IJSObjectReference? _module;
    private DotNetObjectReference<EmployeeGraphCanvas>? _selfReference;
    private bool _initialized;
    private bool _fitRequested;
    private string? _graphSignature;
    private double _zoom = 1;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired]
    public EmployeeGraphModel Graph { get; set; } = default!;

    [Parameter]
    public Guid? SelectedId { get; set; }

    [Parameter]
    public EventCallback<Guid> SelectedIdChanged { get; set; }

    [Parameter]
    public Guid? HighlightedTeamId { get; set; }

    protected string ViewBox => $"0 0 {Graph.Width:0.#} {Graph.Height:0.#}";

    protected override void OnParametersSet()
    {
        var signature = $"{SelectedId}:{Graph.Width:0.#}:{Graph.Height:0.#}:{Graph.Nodes.Count}";
        if (_graphSignature is not null && !string.Equals(_graphSignature, signature, StringComparison.Ordinal))
        {
            _fitRequested = true;
        }

        _graphSignature = signature;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_initialized)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>(
                "import",
                "./_content/CSweet.UI/js/employee-graph-canvas.js");
            _selfReference = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("initialize", _viewport, _content, _selfReference);
            _initialized = true;
        }
        else if (_fitRequested && _module is not null)
        {
            _fitRequested = false;
            await _module.InvokeVoidAsync("fit", _viewport);
        }
    }

    [JSInvokable]
    public Task UpdateZoom(double zoom)
    {
        _zoom = zoom;
        return InvokeAsync(StateHasChanged);
    }

    protected Task SelectAsync(Guid id) => SelectedIdChanged.InvokeAsync(id);

    protected Task ZoomInAsync() =>
        _module is null ? Task.CompletedTask : _module.InvokeVoidAsync("zoomIn", _viewport).AsTask();

    protected Task ZoomOutAsync() =>
        _module is null ? Task.CompletedTask : _module.InvokeVoidAsync("zoomOut", _viewport).AsTask();

    protected Task FitAsync() =>
        _module is null ? Task.CompletedTask : _module.InvokeVoidAsync("fit", _viewport).AsTask();

    protected string? NodeFilterClass(EmployeeViewModel employee) =>
        HighlightedTeamId.HasValue && !employee.Teams.Any(x => x.TeamId == HighlightedTeamId.Value)
            ? "team-filter-dimmed"
            : null;

    protected string EdgePath(EmployeeGraphLayoutEdge edge)
    {
        var startY = edge.From.Y + Graph.NodeHeight / 2;
        var endY = edge.To.Y - Graph.NodeHeight / 2;
        var middleY = startY + (endY - startY) / 2;
        return $"M {edge.From.X:0.#} {startY:0.#} V {middleY:0.#} H {edge.To.X:0.#} V {endY:0.#}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose", _viewport);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The browser already released the component.
            }
        }

        _selfReference?.Dispose();
    }
}
