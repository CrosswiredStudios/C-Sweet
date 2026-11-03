using System.Reflection;
using CSweet.Contracts.Plugins;
using CSweet.UI.Components;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class PluginAccountUpgradeComponentTests
{
    [Fact]
    public async Task ReviewAndUncheckedControlNeverApproveAndPendingReceiptNeverAutomaticallyResubmits()
    {
        var api=new Accounts();var html=await Render(api,async panel=>
        {
            await Invoke(panel,"ReviewCompatible");Assert.Equal(0,api.Approvals);
            await Invoke(panel,"ApproveCompatible");Assert.Equal(0,api.Approvals);
            Set(panel,"_approveCompatible",true);await Invoke(panel,"ApproveCompatible");
            await Invoke(panel,"ApproveCompatible");Assert.Equal(1,api.Approvals);
            Assert.Equal(0,api.Resumes);Assert.Equal(0,api.Cancels);
        });
        Assert.Contains("Upgrade: Pending",html);Assert.Contains("Reconcile upgrade",html);
        Assert.Contains("source remains paused",html);Assert.Contains("No new sign-in",html);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task UncertainAcknowledgmentClearsApprovalAndRecoversOnlyAfterExplicitStatusThenResume(bool timeout)
    {
        var api=new Accounts{Failure=timeout?new TaskCanceledException("CANARY-untrusted-timeout"):new HttpRequestException("CANARY-network-error")};
        var navigation=new Navigation();var html=await Render(api,async panel=>
        {
            await Invoke(panel,"ReviewCompatible");Set(panel,"_approveCompatible",true);await Invoke(panel,"ApproveCompatible");
            await Invoke(panel,"ApproveCompatible");Assert.Equal(1,api.Approvals);Assert.Equal(0,api.Reads);Assert.Equal(0,api.Resumes);
            Assert.Contains("outcome is unconfirmed",(string)typeof(PluginUpdatePanel).GetField("_error",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(panel)!);
            await Invoke(panel,"ReadUpgradeStatus");Assert.Equal(1,api.Reads);Assert.Equal(0,api.Resumes);
            await Invoke(panel,"ResumeCompatible");Assert.Equal(1,api.Resumes);
        },navigation);
        Assert.DoesNotContain("CANARY",html);
        Assert.Equal($"/settings/plugins/{api.Target:D}",navigation.Target);Assert.True(navigation.Force);
    }
    [Fact]
    public async Task RepeatedClickWhileSubmissionIsInFlightIsIgnored()
    {
        var pending=new TaskCompletionSource<PluginAccountUpgradeProgress>();var api=new Accounts{Submission=pending.Task};
        await Render(api,async panel=>
        {
            await Invoke(panel,"ReviewCompatible");Set(panel,"_approveCompatible",true);
            var first=Invoke(panel,"ApproveCompatible");await Invoke(panel,"ApproveCompatible");Assert.Equal(1,api.Approvals);
            pending.SetResult(api.Pending);await first;Assert.Equal(1,api.Approvals);Assert.Equal(0,api.Resumes);
        });
    }
    private static Task Invoke(PluginUpdatePanel panel,string name)=>(Task)typeof(PluginUpdatePanel).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(panel,null)!;
    private static void Set(PluginUpdatePanel panel,string name,object value)=>typeof(PluginUpdatePanel).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(panel,value);
    private static async Task<string> Render(Accounts state,Func<PluginUpdatePanel,Task> action,Navigation? navigation=null)
    {
        var services=new ServiceCollection().AddLogging();services.AddMudServices();
        var proxy=DispatchProxy.Create<IPluginAccountApiClient,AccountsProxy>();((AccountsProxy)(object)proxy).State=state;
        services.AddSingleton(proxy);services.AddSingleton(DispatchProxy.Create<IPluginApiClient,UnusedProxy>());
        services.AddSingleton<NavigationManager>(navigation??new Navigation());services.AddSingleton<IJSRuntime,NoJs>();
        var activator=new Activator();services.AddSingleton<IComponentActivator>(activator);
        await using var provider=services.BuildServiceProvider();await using var renderer=new HtmlRenderer(provider,provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async()=>
        {
            var theme=await renderer.RenderComponentAsync<MudThemeProvider>();
            var view=new PluginAccountManagementView(state.Target,"Example connector","0.1.1",new string('c',64),"Built",false,"update_review_required",new(),[],[],[],true)
                {Administration=new(false,false,false,1,[],[]){Staged=true,PreviousInstallationId=state.Source,RepositoryUrl="https://github.com/example/connector"}};
            var component=await renderer.RenderComponentAsync<PluginUpdatePanel>(ParameterView.FromDictionary(new Dictionary<string,object?>{["View"]=view}));
            await action(activator.Panel!);typeof(ComponentBase).GetMethod("StateHasChanged",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(activator.Panel,null);await Task.Yield();
            return theme.ToHtmlString()+component.ToHtmlString();
        });
    }
    public class AccountsProxy:DispatchProxy
    {
        public Accounts State=null!;
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch
        {
            nameof(IPluginAccountApiClient.ReviewUpgradeAsync)=>Task.FromResult(State.Review),
            nameof(IPluginAccountApiClient.UpgradeStatusAsync)=>Read(),
            nameof(IPluginAccountApiClient.ApproveUpgradeAsync)=>Approve(),
            nameof(IPluginAccountApiClient.ResumeUpgradeAsync)=>Resume(),
            nameof(IPluginAccountApiClient.CancelUpgradeAsync)=>Cancel(),
            _=>throw new InvalidOperationException("Upgrade must not call another account operation.")
        };
        private Task<PluginAccountUpgradeStatus> Read(){State.Reads++;return Task.FromResult(new PluginAccountUpgradeStatus(State.Pending));}
        private Task<PluginAccountUpgradeProgress> Approve(){State.Approvals++;return State.Submission??(State.Failure is{} error?Task.FromException<PluginAccountUpgradeProgress>(error):Task.FromResult(State.Pending));}
        private Task<PluginAccountUpgradeProgress> Resume(){State.Resumes++;return Task.FromResult(State.Pending with{Status="Completed"});}
        private Task<PluginAccountUpgradeProgress> Cancel(){State.Cancels++;return Task.FromResult(State.Pending with{Status="Cancelled"});}
    }
    public class UnusedProxy:DispatchProxy
    {protected override object? Invoke(MethodInfo? method,object?[]? args)=>throw new InvalidOperationException("No plugin install or normal replacement is allowed in this flow.");}
    public sealed class Accounts
    {
        public Guid Source=Guid.NewGuid(),Target=Guid.NewGuid(),Id=Guid.NewGuid();public int Approvals,Reads,Resumes,Cancels;
        public Exception? Failure;public Task<PluginAccountUpgradeProgress>? Submission;
        public PluginAccountUpgradeProgress Pending=>new(Id,Source,Target,"Pending");
        public PluginAccountUpgradeReview Review=>new(Source,Target,"0.1.0","0.1.1",new string('a',64),new string('c',64),"https://managed.example",1,1,["base"],DateTimeOffset.UtcNow.AddMinutes(10),"synthetic-review");
    }
    private sealed class Activator:IComponentActivator
    {public PluginUpdatePanel? Panel;public IComponent CreateInstance(Type type){var value=(IComponent)System.Activator.CreateInstance(type)!;if(value is PluginUpdatePanel panel)Panel=panel;return value;}}
    private sealed class Navigation:NavigationManager
    {public string? Target;public bool Force;public Navigation()=>Initialize("https://app.example/","https://app.example/settings/plugins");protected override void NavigateToCore(string uri,bool forceLoad){Target=uri;Force=forceLoad;}}
    private sealed class NoJs:IJSRuntime
    {public ValueTask<T> InvokeAsync<T>(string id,object?[]? args)=>ValueTask.FromResult(default(T)!);public ValueTask<T> InvokeAsync<T>(string id,CancellationToken ct,object?[]? args)=>ValueTask.FromResult(default(T)!);}
}
