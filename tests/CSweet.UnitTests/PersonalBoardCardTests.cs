using CSweet.UI.Components.WorkBoards;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class PersonalBoardCardTests
{
    [Fact]
    public async Task CountsExcludeArchivedAndCompletedTicketsButIncludeBlockedInActive()
    {
        var html = await Render([
            Item(PersonalTodoStatuses.Ready),
            Item(PersonalTodoStatuses.Running),
            Item(PersonalTodoStatuses.Blocked),
            Item(PersonalTodoStatuses.Completed),
            Item(PersonalTodoStatuses.Blocked, DateTimeOffset.UtcNow),
            Item(PersonalTodoStatuses.Ready, DateTimeOffset.UtcNow)
        ]);

        Assert.Contains("3 active tickets, 1 blocked", html);
        Assert.Contains("is-blocked", html);
    }

    [Fact]
    public async Task ArchivedBlockersDoNotFlagAnOtherwiseEmptyBoard()
    {
        var html = await Render([
            Item(PersonalTodoStatuses.Completed),
            Item(PersonalTodoStatuses.Blocked, DateTimeOffset.UtcNow)
        ]);

        Assert.Contains("0 active tickets, 0 blocked", html);
        Assert.Contains("No blockers", html);
        Assert.DoesNotContain("is-blocked", html);
    }

    private static PersonalTodoItem Item(string status, DateTimeOffset? archived = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Manager", "Task", "",
            status, "Normal", 0, 1, null, null, null, [], null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, archived);

    private static async Task<string> Render(IReadOnlyList<PersonalTodoItem> items)
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var board = new PersonalTodoBoard(Guid.NewGuid(), Guid.NewGuid(), "Evelyn Brooks", null, null, 1, items);
            var output = await renderer.RenderComponentAsync<PersonalBoardCard>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(PersonalBoardCard.Board)] = board }));
            return output.ToHtmlString();
        });
    }
}
