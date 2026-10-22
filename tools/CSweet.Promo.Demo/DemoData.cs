using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Components.Employees.Models;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Promo.Demo;

// Entirely fictional states. This is not agent-execution or provider acceptance evidence.
public sealed partial class DemoData
{
    public static readonly DateTimeOffset Epoch = new(2026, 10, 11, 16, 0, 0, TimeSpan.Zero);
    public string Campaign { get; private set; } = "software";
    public string Scene { get; private set; } = "chat";
    public Guid OrganizationId => Id(1);
    public Guid OwnerId => Id(2);
    public Guid ChatId => Id(3);
    public Guid DocumentId => Id(4);
    public Guid ProjectId => Id(5);
    public Guid BoardId => Id(6);
    public Guid SprintId => Id(7);
    public string CompanyName => Campaign switch { "youtube" => "Signal Studio", "game" or "cinematic" => "Orbit Arcade", _ => "Northstar Software" };
    public string ProjectName => Campaign switch { "youtube" => "The first season", "game" => "Orbit Breaker", "cinematic" => "Prism Break", _ => "Client portal" };
    public string Brief => Campaign switch {
        "youtube" => "Build a thoughtful technology channel. Plan the first season, coordinate the calendar, review community replies, and use official channel reports to guide the next decision.",
        "game" => "Create a focused arcade game: responsive controls, readable action, and a distinctive visual identity. Start with a creative brief, assign each discipline, and review the first design milestone.",
        _ => "Create a client portal that keeps requests, project updates, and documents in one place. Define the requirements, assemble the product team, and review the first implementation milestone."
    };
    public void Select(string campaign, string scene)
    {
        if (campaign is not ("software" or "youtube" or "game" or "cinematic")) throw new ArgumentException("Unknown campaign.");
        if (campaign != "cinematic" && scene is not ("chat" or "team" or "plan" or "board" or "document" or "report" or "approval" or "calendar" or "evidence")) throw new ArgumentException("Unknown scene.");
        Campaign = campaign; Scene = scene;
    }
    public Guid Id(int number) => Guid.Parse($"{(Campaign == "game" ? 3 : Campaign == "youtube" ? 2 : 1):D8}-1111-4111-8111-{number:D12}");
    public OrganizationResponse Organization => new(OrganizationId, CompanyName, Campaign, Brief, "Early stage", Brief, null, Epoch, Epoch);
    private (string Name, string Role, string Portrait)[] Cast => Campaign switch {
        "cinematic" => FilmCast,
        "youtube" => [("Evelyn Brooks", "Chief of Staff", "chief-of-staff"), ("Morgan Vale", "YouTube Account Manager", "youtube-account-manager")],
        "game" => [("Naomi Chen", "Creative Director", "video-game-creative-director"), ("Alex Rivera", "Producer", "video-game-producer"), ("Marcus Reed", "Engineer", "video-game-engineer"), ("Priya Shah", "QA", "video-game-qa")],
        _ => [("Jordan Ellis", "Software Product Manager", "product-manager"), ("Adrian Cole", "Software Architect", "software-architect"), ("Sam Rivera", "Software Developer", "software-developer"), ("Priya Shah", "Software QA", "software-qa")]
    };
    public IReadOnlyList<OrganizationUserResponse> People => new[] { new OrganizationUserResponse(OwnerId, OrganizationId, null, null, null, "You", null, 0, 3, Epoch) }.Concat(Cast.Select((p, i) => new OrganizationUserResponse(Id(20+i), OrganizationId, OwnerId, Id(40+i), null, p.Name, null, 1, 1, Epoch) { AgentInstallationId = Id(60+i) })).ToArray();
    public IReadOnlyList<AgentInstallationResponse> Installations => Cast.Select((p,i) => new AgentInstallationResponse(Id(60+i), Id(80+i), OrganizationId.ToString(), p.Portrait, p.Name, "demo", "C-Sweet", new string('0',40), true, [], [], [], [], [], 512, 25,
        new AgentScheduleResponse(Id(90+i), "OnDemand", 60, null, null, null, null, 300, 1, 0, null, "Skip", true), Epoch, Epoch) { ImageUrl = $"_content/CSweet.UI/images/agents/{p.Portrait}-v1.jpg", RoleName = p.Role, AccentColor = Campaign == "game" ? "#D8B976" : "#735090" }).ToArray();
    public IReadOnlyList<EmployeeViewModel> Employees => Cast.Select((p,i) => new EmployeeViewModel(People[i+1], p.Name, string.Concat(p.Name.Split(' ').Select(x=>x[0])), p.Role, p.Role, "You", true, false, EmployeeRuntimeStatus.Online, 0, [], false, false, false, [EmployeeAction.OpenChat, EmployeeAction.OpenMemory]) { Teams = [new EmployeeTeamViewModel(Id(100), Campaign == "game" ? "Game production" : Campaign == "youtube" ? "Content operations" : "Product team", null, i==0, false, "#735090")] }).ToArray();
    public CommunicationHubResponse Hub => new(OwnerId, OwnerId, false, true,
        [new CommunicationChatResponse(ChatId, Campaign=="cinematic"?FilmChatTitle:Cast[0].Name, "Your direction, connected to the work.", true, true, true, false, Epoch,
            [new CommunicationParticipantResponse(OwnerId,"You","Human","Owner"), new CommunicationParticipantResponse(Id(20+(Campaign=="cinematic"?FilmSpeaker:0)),Cast[Campaign=="cinematic"?FilmSpeaker:0].Name,"Agent",Cast[Campaign=="cinematic"?FilmSpeaker:0].Role)], "The plan is ready for your review.", Epoch, 0)],
        People.Select((p,i)=>new CommunicationPersonResponse(p.Id,p.DisplayName,i==0?"Human":"Agent",p.RoleId,i==0?"Owner":Cast[i-1].Role,ReportsToOrganizationUserId:p.ReportsToOrganizationUserId)).ToArray(), []);
    public IReadOnlyList<CommunicationHubMessageResponse> Messages => Campaign == "cinematic" ? FilmMessages : [
        new(Id(110),1,ChatId,OwnerId,"You","Human",Campaign switch { "youtube"=>"I want to build a technology content business. Help me organize the first season.", "game"=>"Let's build a distinctive arcade game. Start with the creative direction and the team we need.", _=>"Let's create a client portal. Help me turn the idea into a plan and assemble the right team." }, Epoch),
        new(Id(111),2,ChatId,Id(20),Cast[0].Name,"Agent",Campaign switch { "youtube"=>"We'll connect your content plan, calendar, and channel operations. You can review community replies and use channel reports to shape the next step.", "game"=>"We'll begin with a focused creative brief, bring production and specialist roles together, and review the design milestone before expanding the scope.", _=>"We'll define the first milestone, assign product, architecture, development, and QA, and keep review evidence connected to the project." }, Epoch.AddMinutes(1)),
        new CommunicationHubMessageResponse(Id(112),3,ChatId,Id(20),Cast[0].Name,"Agent","The project brief is ready. Review the direction, then follow the team and the work in Projects.", Epoch.AddMinutes(2)) { Artifacts = [new CommunicationMessageArtifactResponse(Id(113),Id(112),DocumentId,Id(114)) { Title = DocumentTitle, Status = "Draft" }] }
    ];
    public string DocumentTitle => Campaign switch { "cinematic" => FilmDocumentTitle, "youtube" when Scene=="report"=>"Channel pulse · sample report", "youtube"=>"The first season · content plan", "game"=>"Orbit Breaker · creative brief", _=>"Client portal · milestone review" };
    private string DocumentContent => Campaign switch {
        "cinematic" => FilmDocumentContent,
        "youtube" when Scene=="report" => "# Channel pulse\n\n**Signal Studio · illustrative report**\n\n## A clear view of the channel\n\n| Metric | Sample value |\n| --- | --- |\n| Views | 12,480 |\n| Watch time | 624 hours |\n| Average view duration | 3:00 |\n\nThese fictional values demonstrate the report format, not measured channel performance.\n\n## What to review next\n\n- Compare topics across the first season.\n- Review questions appearing in the community.\n- Use the evidence to guide the next content plan.",
        "youtube" => "# The first season\n\n**Signal Studio · content plan**\n\n## The direction\n\nThoughtful technology stories for curious builders. One clear idea per episode, with a repeatable review process.\n\n## The first three episodes\n\n1. The tools behind a one-person business.\n2. From an idea to a working plan.\n3. Building with a small, focused team.\n\n## Connected operations\n\nCoordinate the calendar, review community replies, and inspect official channel reports.\n\n## Your next decision\n\nReview the plan and agree on the first production milestone.",
        "game" => "# Orbit Breaker\n\n**Creative brief · design milestone**\n\n## The vision\n\nA focused arcade experience with responsive controls, readable action, and a distinctive cosmic identity.\n\n## Three design pillars\n\n- **Immediate feel.** Every input has a clear response.\n- **Readable play.** The player understands what changed.\n- **A coherent world.** Art and audio support the same direction.\n\n## Team handoff\n\nProduction coordinates the milestone. Engineering defines the prototype work. Independent review checks the design criteria.\n\n## Review before expansion\n\nConfirm the brief and the next sprint scope. This sample does not represent a completed playable build.",
        _ => "# Client portal\n\n**First milestone · review packet**\n\n## The outcome\n\nA shared home for client requests, project updates, and documents.\n\n## Assigned work\n\n- **Product:** define the first user journey.\n- **Architecture:** document access boundaries.\n- **Development:** implement the request workflow.\n- **QA:** review the acceptance criteria independently.\n\n## Review checklist\n\n1. Requests have a clear owner.\n2. Documents are connected to the project.\n3. Review evidence matches the milestone.\n\n## Next decision\n\nInspect the evidence and request revisions where needed. Sample records illustrate the workflow; they do not certify a software release."
    };
    public ArtifactDocumentDetail Document {
        get {
            var revision = new ArtifactRevisionResponse(Id(114),DocumentId,1,null,DocumentContent,new string('0',64),"Draft",Cast[0].Name,Epoch,null,null);
            return new(new ArtifactDocumentSummary(DocumentId,OrganizationId,DocumentTitle,"general.document.v1","Draft",null,null,revision.Id,null,null,1,Cast[0].Name,false,Id(20),Epoch,Epoch,null),revision,null,[revision],[],[]) { DocumentTypeDisplayName = Campaign=="game"?"Creative brief":Scene=="report"?"Channel report":"Project document" };
        }
    }
    private ProjectInspectionResource Resource(int id,string type,string title,string status,object metadata) => new(Id(id),type,null,title,status,1,null,Id(20),null,null,null,Epoch,"/demo/"+Campaign+"/board",JsonSerializer.SerializeToElement(metadata));
    public ProjectInspectionResponse Project => new(Resource(5,"Project",ProjectName,"Active",new { Outcome=Brief,LifecycleStage="First milestone",TargetDate="2026-11-01" }),new(6,0,1,1,1,0,0,false),
        [Resource(100,"Team",Campaign=="game"?"Game production":Campaign=="youtube"?"Content operations":"Product team","Active",new {})], [Resource(6,"Board",ProjectName,"Active",new {})],
        [Resource(4,"Document",DocumentTitle,"Draft",new {})],[],[],[],
        [new ProjectAuditEntry(Id(120),1,Epoch,"ProjectCreated","Projects","Success","Project",ProjectId,"Project direction recorded","Agent",Id(20),Cast[0].Name,null,null,null,null,null,"sample"), new ProjectAuditEntry(Id(121),2,Epoch.AddMinutes(10),"TeamAssigned","Projects","Success","Project",ProjectId,"Team connected to the first milestone","Agent",Id(20),Cast[0].Name,null,null,null,null,null,"sample")],Epoch);
    public IReadOnlyList<ProjectInspectionResource> Evidence => [Resource(180,"DocumentReview","First milestone · review packet","Pending",new { Disposition="Awaiting independent review" }),Resource(181,"Validation","Acceptance criteria","InReview",new { Disposition="Review the exact revision" }),Resource(182,"Document",DocumentTitle,"Draft",new { ContentType="Project document" })];
    public WorkBoardDetailResponse Board {
        get {
            if (Campaign == "cinematic") return FilmBoard;
            var names=new[] {"Ready","In progress","Review","Done"};
            var columns=names.Select((n,i)=>new WorkBoardColumnResponse(Id(130+i),n,i==0?"ToDo":i==3?"Done":"InProgress",i,"None",null)).ToArray();
            var titles=Campaign switch { "youtube"=>new[]{"Outline the first season","Coordinate the content calendar","Review a community reply","Prepare the channel report","Review episode topics","Record the content direction"}, "game"=>new[]{"Define the first game loop","Implement input response","Review the creative brief","Align the design pillars","Plan the prototype scope","Record the visual direction"}, _=>new[]{"Define client request criteria","Implement the request workflow","Review access boundaries","Document the portal journey","Plan the first milestone","Record the product direction"}};
            var assignments=Campaign=="youtube"?new[]{0,0,1,1,0,0}:new[]{0,2,1,3,0,1};
            var positions=new[]{0,1,2,2,0,3};
            var items=titles.Select((t,i)=>new WorkBoardItemResponse(Id(140+i),BoardId,columns[positions[i]].Id,null,SprintId,"Task",t,"First milestone · sample work item",positions[i]==3?"Completed":positions[i]==0?"Ready":"InProgress","Medium",3,i,1,null,Epoch,Epoch,AssignedEmployeeId:Id(20+assignments[i]),AssignedInstallationId:Id(60+assignments[i]),AssignedDisplayName:Cast[assignments[i]].Name) { Identifier=$"{(Campaign=="game"?"ORB":Campaign=="youtube"?"SIG":"NTH")}-{i+1:D3}",TypeKey="work.task.v1" }).ToArray();
            return new(new WorkBoardSummaryResponse(BoardId,OrganizationId,ProjectId,Campaign=="game"?"Design milestone":Campaign=="youtube"?"Content operations":"First milestone",Brief,true,false,true,Epoch,6,4,1,Epoch,Epoch,[WorkBoardActions.Read,WorkItemActions.Read,WorkSprintActions.Read]) { Key=Campaign=="game"?"ORB":Campaign=="youtube"?"SIG":"NTH" },columns,items);
        }
    }
    public IReadOnlyList<WorkSprintResponse> Sprints => [new(SprintId,BoardId,"Sprint 01",Campaign=="game"?"Align the creative direction and prototype scope.":Campaign=="youtube"?"Connect the first season and channel operations.":"Establish the first client request workflow.","Active",Epoch,Epoch.AddDays(14),Epoch,null,18,6,1,18,3,1,Epoch,Epoch)];
    public ConnectorActionApprovalCardResponse CommentApproval => new(new ManagedAgentActionApprovalResponse(Id(160),"youtube.comment.reply","signal-studio",new string('0',64),1,"demo-reply",true,"sample-comment",AccountName:"Signal Studio",ReviewPayloadJson:JsonSerializer.Serialize(new { changes=new { reply="Thanks for the thoughtful question! We’ll cover how we organize the first milestone in the next episode." } })),"Reply to a viewer asking how the first milestone is organized.","Pending","Pending",true);
    public Wire.BusinessCalendarView Calendar {
        get {
            var dates=new[]{12,15,19,22,26};
            var titles=new[]{"Season planning","Episode 01 · outline review","Community reply review","Channel pulse review","Next milestone planning"};
            var events=dates.Select((day,i)=> {
                var start=new DateTime(2026,10,day,10,0,0); var input=new Wire.CalendarEventInput(titles[i],start,start.AddHours(1),"UTC",Description:"Signal Studio · sample calendar",OwnerOrganizationUserId:OwnerId,AttendeeIds:[Id(21)]);
                var view=new Wire.CalendarEventView(Id(170+i),1,OwnerId,input,false,true);
                return new Wire.CalendarOccurrence(view.Id,start,new DateTimeOffset(start,TimeSpan.Zero),new DateTimeOffset(start.AddHours(1),TimeSpan.Zero),view,input);
            }).ToArray();
            return new("UTC",1,OwnerId,true,true,People.Select(x=>new Wire.CalendarMember(x.Id,x.DisplayName)).ToArray(),events);
        }
    }
}
