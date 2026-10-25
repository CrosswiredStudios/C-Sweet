using CSweet.Application.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
    [Fact]
    public async Task LargeDescriptionsSurvivePersonalTaskCreationAndEditing()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(setup.FirstManager.Id, null);
        var original = WorkItemDescriptionTests.LargeBrief.Trim();
        var item = await service.AddAsync(setup.Organization.Id, actor,
            Add("Stress scene", "large-brief", null) with { Description = original });
        Assert.Equal(original, item.Description);
        var updated = original + "\nAdditional validation guidance.";
        item = await service.UpdateAsync(setup.Organization.Id, actor,
            new(item.Id, item.Title, updated, item.Priority, null, item.Revision, "edit-large-brief"));
        db.ChangeTracker.Clear();
        Assert.Equal(updated, (await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id)).Description);
    }
}
