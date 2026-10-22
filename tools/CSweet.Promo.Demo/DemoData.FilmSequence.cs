using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;

namespace CSweet.Promo.Demo;

// Fictional, ordered editorial states, rendered by the production components.
public sealed partial class DemoData
{
    public int FilmSpeaker => Scene is "chat-brief" or "chat-producer" or "chat-ready" ? 1 : Scene is "chat-team" or "chat-planning" ? 2 : 0;
    public string FilmChatTitle => Scene switch { "chat-brief" or "chat-producer" or "chat-ready" => "Naomi Chen", "chat-team" or "chat-planning" => "Prism Break · Studio", _ => "Evelyn Brooks" };
    public IReadOnlyList<CurrentActivityItem> FilmActivities => [
        new("direction",null,null,null,null,1,Id(20),"Evelyn Brooks","Set the company direction",null,"Chat","Orbit Arcade","Waiting","Waiting for your focus decision",Epoch,Epoch,null,null,null,false,[],ActivityKind:"Chat",EmployeeRole:"Chief of Staff"),
        new("brief",null,null,null,null,1,Id(20),"Evelyn Brooks","Prepare your first project",null,"Planning","Orbit Arcade","Waiting","Ready to turn your idea into a team",Epoch,Epoch,null,null,null,false,[],ActivityKind:"Work",EmployeeRole:"Chief of Staff")];
    private SuggestedUserActionResponse FilmAction(int i) => new(Id(510+i),"hiring.marketplace.browse.v1","Hire",FilmCast[i].Role,"/demo/cinematic/team","Pending",Epoch)
        { HiringRecommendationId=Id(520+i),HiringRole=FilmCast[i].Role };
    private CommunicationHubMessageResponse FilmMessage(int n,int person,string content) => new(Id(700+n),n,ChatId,person<0?OwnerId:Id(20+person),person<0?"You":FilmCast[person].Name,person<0?"Human":"Agent",content,Epoch.AddSeconds(n*20));
    private CommunicationHubMessageResponse Suggest(int n,params int[] people) => FilmMessage(n,people[0]==1?0:1,"") with { MessageType=CommunicationMessageTypes.SystemAction,Actions=people.Select(FilmAction).ToArray() };
    private IReadOnlyList<CommunicationHubMessageResponse> SequenceMessages => Scene switch {
        "chat-director" => [FilmMessage(1,0,"Creative & Product it is. Naomi can shape your game idea and bring the right studio together."),Suggest(2,1)],
        "chat-brief" => [FilmMessage(1,1,"What should the game feel like? Where will people play, and what matters most for the first prototype?"),FilmMessage(2,-1,"A vibrant browser arcade game. Keyboard and pointer controls. One minute to learn, one more go to master."),FilmMessage(3,1,"Prism Break: glowing bricks, responsive controls, a clean replay loop. Let's keep the first playable focused.")],
        "chat-producer" => [FilmMessage(1,1,"The direction is clear. I recommend Gabriel as Producer to scope the first playable and coordinate delivery."),Suggest(2,2)],
        "chat-team" => [FilmMessage(1,1,"Gabriel, let's make the core loop feel great before we add more features."),FilmMessage(2,2,"Agreed. I'll plan one playable milestone and keep the work visible. We'll need engineering, independent technical review and QA."),Suggest(3,3,4,5)],
        "chat-planning" => [FilmMessage(1,2,"One epic for the prototype. A playable-loop story and a player-experience story, with small owned tasks."),FilmMessage(2,4,"I'll define the architecture and review criteria. Controls, collisions and replay need separate checks before we integrate."),FilmMessage(3,2,"I'll turn that into the sprint. Every task gets an owner and a clear definition of done.")],
        "chat-ready" => [FilmMessage(1,1,"The prototype is ready for testing! The team has completed implementation, technical review and QA."),FilmMessage(2,1,"Try Prism Break in your browser and tell us how it feels.\n\n[http://127.0.0.1:5277/tools/csweet-explainer/game.html](http://127.0.0.1:5277/tools/csweet-explainer/game.html)")],
        _ => [] };
    public WorkBoardDetailResponse SequenceBoard
    {
        get
        {
            var names=new[]{"Ready","In progress","Technical Review","QA","Done"};
            var columns=names.Select((n,i)=>new WorkBoardColumnResponse(Id(130+i),n,i==0?"ToDo":i==4?"Done":"InProgress",i,"None",null)).ToArray();
            var titles=new[]{"Prism Break · first playable","Build the arcade loop","Make it feel great","Responsive paddle controls","Ball and brick collisions","Score, lives and restart","Approve the player experience"};
            var kinds=new[]{"Epic","Story","Story","Task","Task","Task","Task"};
            var owners=new[]{2,2,1,3,3,3,1};
            var stages=Scene switch {"board-progress1"=>new[]{1,1,1,1,0,0,0},"board-progress2"=>new[]{1,1,1,2,1,1,0},"board-progress3"=>new[]{1,1,1,3,2,3,1},"board-complete"=>new[]{4,4,4,4,4,4,4},_=>new[]{0,0,0,0,0,0,0}};
            var count=Scene switch {"board-empty"=>0,"board-epic"=>1,"board-stories"=>3,_=>7};
            var items=Enumerable.Range(0,count).Select(i=>new WorkBoardItemResponse(Id(800+i),BoardId,columns[stages[i]].Id,i==0?null:Id(i<3?800:i==6?802:801),SprintId,kinds[i],titles[i],"First playable · approved scope",stages[i]==4?"Completed":stages[i]>0?"InProgress":"Ready","Medium",i<3?null:3,i,1,null,Epoch,Epoch,AssignedEmployeeId:Id(20+owners[i]),AssignedInstallationId:Id(60+owners[i]),AssignedDisplayName:FilmCast[owners[i]].Name)
                { Identifier=$"PB-{i+1:D2}",TypeKey=$"work.{kinds[i].ToLowerInvariant()}.v1" }).ToArray();
            return new(new WorkBoardSummaryResponse(BoardId,OrganizationId,ProjectId,"Prism Break · first playable","One arena. Great controls. One more go.",true,false,true,Epoch,count,stages.Take(count).Count(i=>i!=4),stages.Take(count).Count(i=>i==4),Epoch,Epoch,[WorkBoardActions.Read,WorkItemActions.Read,WorkSprintActions.Read]){Key="PB"},columns,items);
        }
    }
    public IReadOnlyList<WorkSprintResponse> SequenceSprints => [new(SprintId,BoardId,"Sprint 01","Deliver the first playable","Active",Epoch,Epoch.AddDays(14),Epoch,null,12,SequenceBoard.Items.Count,Scene=="board-complete"?7:0,12,Scene=="board-complete"?12:0,1,Epoch,Epoch)];
}
