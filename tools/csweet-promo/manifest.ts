export type Format = 'landscape' | 'vertical';
export type CampaignId = 'software' | 'youtube' | 'game';
export type Shot = { id: string; start: number; end: number; heading: string; detail: string; asset?: string; anchor?: string; chapter: number; focal: string; source: string; };
export type Campaign = { id: CampaignId; company: string; project: string; theme: string; accent: string; eyebrow: string; hook: string; summary: string; portraits: string[]; roles: string[]; names: string[]; };

export const formats = { landscape: { width:1920,height:1080,duration:50,fps:30 }, vertical: { width:1080,height:1920,duration:25,fps:30 } };
export const campaigns: Campaign[] = [
    {id:'software',company:'Northstar Software',project:'Client portal',theme:'aurora',accent:'#c7b5ee',eyebrow:'FOR THE BUILDERS',hook:'An idea needs\na team.',summary:'Your direction. Connected to the work.',portraits:['product-manager','software-architect','software-developer','software-qa'],roles:['Product','Architecture','Development','QA'],names:['Jordan Ellis','Adrian Cole','Sam Rivera','Priya Shah']},
    {id:'youtube',company:'Signal Studio',project:'The first season',theme:'aurora',accent:'#bce6d7',eyebrow:'FOR THE CREATORS',hook:'Build more than\na channel.',summary:'A content business. With a clear direction.',portraits:['chief-of-staff','youtube-account-manager'],roles:['Chief of Staff','YouTube Account Manager'],names:['Evelyn Brooks','Morgan Vale']},
    {id:'game',company:'Orbit Arcade',project:'Orbit Breaker',theme:'gold',accent:'#d8b976',eyebrow:'FOR THE WORLD BUILDERS',hook:'Give your world\na team.',summary:'A shared vision. Across every discipline.',portraits:['video-game-creative-director','video-game-producer','video-game-engineer','video-game-qa'],roles:['Creative direction','Production','Engineering','QA'],names:['Naomi Chen','Alex Rivera','Marcus Reed','Priya Shah']}
];

const source = {
    chat:'src/CSweet.UI/Pages/Communications.razor · CommunicationHubResponse / CommunicationHubMessageResponse',
    plan:'src/CSweet.UI/Components/Projects/ProjectOverview.razor · ProjectInspectionResponse',
    team:'src/CSweet.UI/Components/Employees/EmployeeDirectoryCard.razor · EmployeeViewModel / EmployeePortraitScope',
    board:'src/CSweet.UI/Components/WorkBoards/WorkBoardWorkspace.razor · WorkBoardDetailResponse / WorkSprintResponse',
    document:'src/CSweet.UI/Components/ArtifactWorkspace.razor · ArtifactDocumentDetail / ArtifactRevisionResponse',
    evidence:'src/CSweet.UI/Components/Projects/ProjectResourceSection.razor · ProjectInspectionResource',
    calendar:'src/CSweet.UI/Pages/Calendar.razor · BusinessCalendarView',
    approval:'src/CSweet.UI/Components/ConnectorActionApprovalCard.razor · ConnectorActionApprovalCardResponse',
    brand:'src/CSweet.App/wwwroot/images/icon_dark.svg'
};

export function shotsFor(c: Campaign, format: Format): Shot[] {
    const vertical = format === 'vertical';
    const times = vertical ? [0,2,4,6,10,13,16,21,25] : [0,3,6.5,10,18,24,31,38,44,50];
    const asset = (scene:string) => `/assets/${c.id}-${scene}.png`;
    const make = (id:string,start:number,end:number,heading:string,detail:string,chapter:number,scene?:keyof typeof source,anchor?:string):Shot => ({id,start,end,heading,detail,chapter,asset:scene?asset(scene):undefined,anchor:anchor?asset(anchor):undefined,focal:id==='task'?'Assigned work card':id==='team'?'Employee identity and role':id==='document'?'Document content and revision':heading.replaceAll('\n',' '),source:source[scene||'brand']});
    const result:Shot[] = [
        make('hook',times[0],times[1],c.hook,c.summary,0),
        make('chat',times[1],times[2],'Start with\nyour direction.','One conversation. A clear next step.',1,'chat','chat-focus'),
        make('plan',times[2],times[3],c.id==='game'?'Keep the vision\nin view.':'Give the idea\na plan.',c.project+' · the first milestone',1,'plan','chat-focus'),
        make('team',times[3],times[4],c.id==='game'?'Bring every\ndiscipline together.':c.id==='youtube'?'A business.\nWith a team.':'The right people.\nA shared direction.','People and agents. Connected in one workspace.',2,'team','team-focus'),
        make(c.id==='youtube'?'calendar':'board',times[4],times[5],c.id==='youtube'?'Keep the work\nconnected.':'Give every task\nan owner.',c.id==='youtube'?'Plan the season. Coordinate the calendar.':'A shared board. A visible next step.',3,c.id==='youtube'?'calendar':'board','team-focus'),
        make(c.id==='youtube'?'approval':'task',times[5],times[6],c.id==='youtube'?'Your voice.\nYour decision.':'Follow the work.\nStay in the loop.',c.id==='youtube'?'Review a reply before it reaches the community.':'See the assignment, the owner, and the milestone.',3,c.id==='youtube'?'approval':'board','board-focus'),
    ];
    if(vertical){
        result.push(make(c.id==='youtube'?'report':'document',times[6],times[7],c.id==='youtube'?'Understand\nyour audience.':c.id==='game'?'Keep the vision\nin view.':'Review what\nmoves forward.',c.id==='youtube'?'Channel reports inform the next decision.':'Open the document. Inspect the exact revision.',4,c.id==='youtube'?'document':'document','board-focus'));
        if(c.id==='youtube') result[result.length-1].asset=asset('report');
        result.push(make('end',times[7],times[8],'Run the company.','What will you build?',5));
    }else{
        result.push(make(c.id==='youtube'?'report':'document',times[6],times[7],c.id==='youtube'?'Understand\nyour audience.':c.id==='game'?'Keep the vision\nin view.':'Review what\nmoves forward.',c.id==='youtube'?'Channel reports inform the next decision.':'Open the document. Inspect the exact revision.',4,'document','board-focus'));
        if(c.id==='youtube') result[result.length-1].asset=asset('report');
        result.push(make(c.id==='youtube'?'report-close':'evidence',times[7],times[8],c.id==='youtube'?'Make the next\nmove informed.':'Direction. Work.\nReview. Connected.',c.id==='youtube'?'Bring the learning into your next plan.':'Evidence gives the next decision a home.',4,c.id==='youtube'?'document':'evidence'));
        if(c.id==='youtube') result[result.length-1].asset=asset('report');
        result.push(make('end',times[8],times[9],'Run the company.','What will you build?',5));
    }
    return result;
}
