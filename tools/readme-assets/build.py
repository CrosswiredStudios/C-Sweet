"""Editable Aurora diagrams and screenshot compositions; no generated product UI."""
from pathlib import Path
from html import escape
import hashlib
import json
import shutil

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / 'assets' / 'readme'
PREVIEW = ROOT / 'artifacts' / 'readme'
PREVIEW.mkdir(parents=True, exist_ok=True)
INK, MUTED, ACCENT = '#30263f', '#72667d', '#735090'

def text(x, y, value, size=24, color=INK, weight=400, extra=''):
    return f'<text x="{x}" y="{y}" font-size="{size}" fill="{color}" font-weight="{weight}" {extra}>{escape(value)}</text>'

def svg(width, height, title, description, body):
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" aria-labelledby="title desc">
<title id="title">{escape(title)}</title><desc id="desc">{escape(description)}</desc>
<defs>
 <linearGradient id="paper" x2="1" y2="1"><stop stop-color="#f1ecfb"/><stop offset=".6" stop-color="#fbfaff"/><stop offset="1" stop-color="#edf7f5"/></linearGradient>
 <radialGradient id="lavender"><stop stop-color="#c5c1f2" stop-opacity=".62"/><stop offset="1" stop-color="#c5c1f2" stop-opacity="0"/></radialGradient>
 <radialGradient id="pink"><stop stop-color="#eac2dd" stop-opacity=".46"/><stop offset="1" stop-color="#eac2dd" stop-opacity="0"/></radialGradient>
 <filter id="shadow" x="-10%" y="-15%" width="120%" height="145%"><feDropShadow dy="8" stdDeviation="12" flood-color="#735090" flood-opacity=".09"/></filter>
 <marker id="arrow" markerWidth="8" markerHeight="8" refX="6" refY="4" orient="auto"><path d="M0 0L8 4L0 8Z" fill="#a28bbb"/></marker>
</defs>
<rect width="{width}" height="{height}" rx="28" fill="url(#paper)"/>
<circle cx="{width*.15}" cy="{height*.2}" r="{width*.4}" fill="url(#lavender)"/><circle cx="{width*.94}" cy="{height*.14}" r="{width*.38}" fill="url(#pink)"/>
<g font-family="Segoe UI,Arial,sans-serif">{body}</g></svg>'''

steps = [
    ('Direction', 'Name the outcome.', 'Set the boundaries.'),
    ('Plan', 'Review the first milestone.', 'Agree on the next step.'),
    ('Team', 'Choose the roles.', 'Review their capabilities.'),
    ('Work', 'Give every task an owner.', 'Follow the assignment.'),
    ('Review', 'Inspect the exact revision.', 'Decide what moves forward.'),
    ('Company memory', 'Keep the decisions.', 'Inform the next assignment.'),
]

def workflow(mobile=False):
    width, height = (720, 1264) if mobile else (1440, 740)
    body = text(48, 54, 'THE COMPANY AT WORK', 17, ACCENT, 650, 'letter-spacing="3"')
    body += text(48, 108, 'A clear direction. A visible next step.' if not mobile else 'A company at work.', 43, weight=600)
    if not mobile:
        positions = [(48, 170), (528, 170), (1008, 170), (1008, 430), (528, 430), (48, 430)]
        body += '<g fill="none" stroke="#a28bbb" stroke-width="2" marker-end="url(#arrow)"><path d="M432 277H514"/><path d="M912 277H994"/><path d="M1200 386V416"/><path d="M1008 537H926"/><path d="M528 537H446"/></g>'
        body += '<path d="M240 646V688H24V277H40" fill="none" stroke="#a28bbb" stroke-width="1.5" stroke-dasharray="5 7" marker-end="url(#arrow)"/>'
        body += text(380, 693, 'Learning and reviewed decisions inform the next direction.', 20, MUTED)
        for i, ((title, line1, line2), (x, y)) in enumerate(zip(steps, positions)):
            dark = i == 0
            body += f'<rect x="{x}" y="{y}" width="384" height="216" rx="22" fill="{INK if dark else "#fbfaff"}" stroke="{"#473553" if dark else "#ffffff"}" stroke-width="2" filter="url(#shadow)"/>'
            body += f'<rect x="{x+26}" y="{y+24}" width="48" height="30" rx="10" fill="{"#4b395e" if dark else "#ede5f6"}"/>'
            body += text(x+50, y+46, f'{i+1:02}', 17, '#e8dff4' if dark else ACCENT, 650, 'text-anchor="middle"')
            body += text(x+26, y+101, title, 30, '#fbfaff' if dark else INK, 600)
            body += text(x+26, y+147, line1, 21, '#e0d7ea' if dark else MUTED)
            body += text(x+26, y+180, line2, 21, '#e0d7ea' if dark else MUTED)
    else:
        for i, (title, line1, line2) in enumerate(steps):
            x, y = 48, 156+i*164
            dark = i == 0
            body += f'<rect x="{x}" y="{y}" width="624" height="138" rx="22" fill="{INK if dark else "#fbfaff"}" stroke="#ffffff" filter="url(#shadow)"/>'
            body += f'<rect x="{x+24}" y="{y+24}" width="46" height="35" rx="11" fill="{"#4b395e" if dark else "#ede5f6"}"/>'
            body += text(x+47, y+49, f'{i+1:02}', 19, '#e8dff4' if dark else ACCENT, 650, 'text-anchor="middle"')
            body += text(x+96, y+48, title, 31, '#fbfaff' if dark else INK, 600)
            body += text(x+96, y+85, line1, 24, '#e0d7ea' if dark else MUTED)
            body += text(x+96, y+117, line2, 24, '#e0d7ea' if dark else MUTED)
            if i < 5: body += f'<path d="M360 {y+142}V{y+158}" fill="none" stroke="#a28bbb" stroke-width="2" marker-end="url(#arrow)"/>'
        body += text(48, 1184, 'Keep learning. Refine the next direction.', 25, ACCENT, 600)
        body += text(48, 1225, 'Execution depends on setup and granted capabilities.', 21, MUTED)
    return svg(width, height, 'From direction to reviewed work', 'Direction, plan, team, work, review and company memory. Learning informs the next direction.', body)

def security(mobile=False):
    width, height = (720, 1310) if mobile else (1440, 680)
    body = text(48, 54, 'YOUR COMPANY, YOUR CONTROL', 17, ACCENT, 650, 'letter-spacing="3"')
    body += text(48, 108, 'Authority stays explicit.', 43, weight=600)
    if not mobile: body += text(48, 148, 'You lead. Headquarters governs. Office executes within a boundary.', 23, MUTED)
    groups = [
        ('01', 'You', 'Direction & decisions', ['Define the outcome', 'Review plans and results', 'Choose permissions and policy']),
        ('02', 'Headquarters', 'Company state & policy', ['Identity and capability grants', 'Approvals, quotas and audit', 'Storage and brokered access']),
        ('03', 'Office', 'Separate execution plane', ['Independent installation', 'Current builder/runtime certification', 'Hardware-isolated agent guests']),
    ]
    for i, (number, title, subtitle, lines) in enumerate(groups):
        x, y, w, h = (48, 176+i*350, 624, 256) if mobile else (48+i*480, 208, 384, 294)
        dark = i == 1
        body += f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="24" fill="{INK if dark else "#fbfaff"}" stroke="{"#473553" if dark else "#ffffff"}" stroke-width="2" filter="url(#shadow)"/>'
        body += text(x+26, y+37, number, 18, '#cbb5ef' if dark else ACCENT, 650, 'letter-spacing="2"')
        body += text(x+26, y+88, title, 35 if mobile else 32, '#fbfaff' if dark else INK, 600)
        body += text(x+26, y+124, subtitle, 24 if mobile else 21, '#cbb5ef' if dark else ACCENT, 550)
        for j, line in enumerate(lines): body += text(x+26, y+174+j*31, line, 23 if mobile else 20, '#e0d7ea' if dark else MUTED)
    if mobile:
        for y, label in [(446, 'Direction and reviewed decisions'), (796, 'Bounded work and return evidence')]:
            body += f'<path d="M360 {y}V{y+66}" fill="none" stroke="#a28bbb" stroke-width="2" marker-end="url(#arrow)"/>'
            body += f'<rect x="100" y="{y+19}" width="520" height="34" rx="12" fill="#f6f3fb"/>'
            body += text(360, y+44, label, 23, ACCENT, 500, 'text-anchor="middle"')
        body += text(48, 1205, 'Docker runs trusted development infrastructure.', 24, ACCENT, 550)
        body += text(48, 1250, 'Untrusted execution requires a ready Office.', 24, MUTED)
    else:
        body += '<g fill="none" stroke="#a28bbb" stroke-width="2" marker-end="url(#arrow)"><path d="M438 355H514"/><path d="M918 330H994"/><path d="M1002 388H926"/></g>'
        body += text(960, 296, 'Work', 17, ACCENT, 550, 'text-anchor="middle"')
        body += text(960, 423, 'Evidence', 17, ACCENT, 550, 'text-anchor="middle"')
        body += '<rect x="48" y="552" width="1344" height="80" rx="20" fill="#ffffff8f" stroke="#ffffff"/>'
        body += text(78, 585, 'TRUSTED DEVELOPMENT INFRASTRUCTURE', 15, ACCENT, 650, 'letter-spacing="2"')
        body += text(78, 614, 'Docker / PostgreSQL support Headquarters. Untrusted agents run in a separately enrolled Office.', 21, MUTED)
    return svg(width, height, 'C-Sweet authority and execution boundary', 'You set direction. Headquarters governs identity, capabilities, approvals and company state. Office supplies certified hardware-isolated execution. Docker is trusted development infrastructure.', body)

for filename, content in [
    ('csweet-company-workflow.svg', workflow()), ('csweet-company-workflow-mobile.svg', workflow(True)),
    ('csweet-security.svg', security()), ('csweet-security-mobile.svg', security(True)),
]:
    (ASSETS/filename).write_text(content, encoding='utf-8')

capture_names = ['software-chat.png', 'software-chat-focus.png', 'software-board.png', 'software-ticket-0.png', 'software-ticket-1.png', 'software-ticket-2.png', 'software-employee-0.png', 'software-employee-1.png', 'software-document-content-mobile.png']
capture_root = ROOT/'artifacts'/'promo'/'captures'
dest = ASSETS/'screenshots'
dest.mkdir(exist_ok=True)
provenance = []
for name in capture_names:
    source = capture_root/name
    # Existing published inputs also permit rebuilding after generated artifacts are cleaned.
    if source.exists(): shutil.copyfile(source, dest/name)
    if not (dest/name).exists(): raise FileNotFoundError(f'Capture missing: {name}. Run the isolated software demo capture first.')
    provenance.append({'file': f'screenshots/{name}', 'sha256': hashlib.sha256((dest/name).read_bytes()).hexdigest(), 'campaign': 'Northstar Software', 'fictionalSampleData': True})
(ASSETS/'screenshots'/'provenance.json').write_text(json.dumps(provenance, indent=2), encoding='utf-8')

styles = '''
*{box-sizing:border-box}html,body{margin:0;font-family:"Segoe UI",Arial,sans-serif;color:#30263f}body{background:#f6f3fb}.canvas{position:relative;overflow:hidden;background:radial-gradient(ellipse at 7% 5%,#c5c1f2a6,transparent 48%),radial-gradient(ellipse at 94% 18%,#eac2dd99,transparent 45%),radial-gradient(ellipse at 44% 75%,#c6e8eaa6,transparent 55%),#f6f3fb}.ring{position:absolute;width:860px;height:860px;border:1px solid #73509016;border-radius:50%;right:-80px;top:-160px}.ring.second{width:1050px;height:1050px;right:-175px;top:-255px}.brand{position:absolute;left:70px;top:54px;display:flex;gap:16px;align-items:center;font-size:25px;font-weight:650;letter-spacing:-.5px}.brand img{width:43px;height:43px}.eyebrow{font-size:15px;font-weight:650;letter-spacing:3px;color:#735090}.headline{position:absolute;left:70px;top:177px;margin:0;font-size:96px;line-height:1.04;font-weight:600;letter-spacing:-5px}.subtitle{position:absolute;left:74px;top:413px;font-size:27px;line-height:1.6;color:#72667d;width:510px}.window{position:absolute;border:1px solid #ffffff;overflow:hidden;border-radius:24px;background:#fbfaff;box-shadow:0 28px 75px #73509023}.chrome{height:43px;padding:0 20px;display:flex;align-items:center;justify-content:space-between;color:#72667d;font-size:12px;background:#fbfaff;border-bottom:1px solid #dcd4e7}.dots{display:flex;gap:6px}.dots i{width:6px;height:6px;border-radius:50%;background:#baa7cb}.pane{overflow:hidden}.pane img{display:block;width:100%;height:auto}.board{left:670px;top:250px;width:855px;transform:rotate(-1.2deg)}.direction{left:923px;top:92px;width:576px;transform:rotate(2deg)}.direction .pane{padding:12px}.ticket{left:1030px;top:685px;width:428px;transform:rotate(1.2deg)}.ticket .pane{padding:0}.ticket .chrome{color:#735090}.roles{position:absolute;left:72px;top:660px;display:flex;gap:24px}.role{display:flex;flex-direction:column;gap:14px;font-size:16px;color:#72667d;align-items:center}.role img{width:64px;height:64px;object-fit:cover;border-radius:20px;border:2px solid #ffffff;box-shadow:0 12px 26px #73509016}.mini-rule{position:absolute;left:74px;top:585px;width:68px;height:2px;background:#735090}.foot{position:absolute;left:72px;bottom:36px;font-size:13px;letter-spacing:2px;color:#72667d}.side-label{position:absolute;top:63px;right:70px;font-size:12px;letter-spacing:2px;color:#72667d}.mobile .brand{left:44px;top:38px}.mobile .side-label{display:none}.mobile .headline{left:44px;top:129px;font-size:77px;letter-spacing:-3.5px}.mobile .subtitle{left:48px;top:316px;font-size:24px;width:650px;line-height:1.5}.mobile .board{left:44px;top:483px;width:708px;transform:rotate(-.5deg)}.mobile .direction{left:187px;top:416px;width:555px;transform:rotate(1deg);z-index:2}.mobile .direction .chrome{display:none}.mobile .ticket{left:265px;top:900px;width:460px}.mobile .roles{left:48px;top:1055px;gap:22px}.mobile .role{font-size:17px;flex-direction:row;gap:9px}.mobile .role img{width:34px;height:34px;border-radius:11px}.mobile .role:nth-child(n+3){display:none}.mobile .foot{left:48px;bottom:27px;font-size:12px;letter-spacing:1px}.mobile .mini-rule{display:none}
.workspace .brand{top:45px;left:56px}.workspace h1{position:absolute;left:56px;top:100px;font-size:48px;letter-spacing:-1.7px;font-weight:600}.workspace .intro{position:absolute;left:58px;top:185px;color:#72667d;font-size:23px}.feature{position:absolute;top:265px;width:480px}.feature:nth-of-type(1){left:56px}.feature:nth-of-type(2){left:560px}.feature:nth-of-type(3){left:1064px}.feature h2{font-size:26px;font-weight:600;margin:0 0 8px;letter-spacing:-.5px}.feature p{font-size:18px;color:#72667d;margin:0 0 26px}.feature .box{height:525px;border:1px solid #ffffff;border-radius:22px;background:#fbfaffad;overflow:hidden;padding:16px;box-shadow:0 20px 44px #73509012}.feature .box img{display:block;width:100%;height:auto;margin-bottom:17px;border-radius:14px}.feature.work .box{padding:20px}.feature.work .box img{margin-bottom:24px}.feature.document .box{padding:0}.feature.document .box img{border-radius:0}.workspace .foot{left:56px}.workspace.mobile .brand{left:44px}.workspace.mobile h1{left:44px;top:110px;font-size:44px;width:680px;line-height:1.17}.workspace.mobile .intro{left:47px;top:263px;font-size:23px}.workspace.mobile .feature{left:44px;width:712px}.workspace.mobile .feature:nth-of-type(1){top:346px}.workspace.mobile .feature:nth-of-type(2){top:1044px}.workspace.mobile .feature:nth-of-type(3){top:1748px}.workspace.mobile .feature h2{font-size:32px}.workspace.mobile .feature p{font-size:23px}.workspace.mobile .feature .box{height:550px}.workspace.mobile .feature.work .box img{width:570px;margin:0 auto 22px}.workspace.mobile .feature.document .box{height:830px}.workspace.mobile .foot{left:44px;font-size:13px}
'''

def frame(class_name, image, label):
    return f'<div class="window {class_name}"><div class="chrome"><span class="dots"><i></i><i></i><i></i></span><span>{label}</span><span>C-Sweet</span></div><div class="pane"><img src="/assets/readme/screenshots/{image}" alt="Actual sample app view"></div></div>'

def page(body): return f'<!doctype html><html lang="en"><meta charset="utf-8"><style>{styles}</style>{body}</html>'

for mobile in [False, True]:
    width, height = (800, 1170) if mobile else (1600, 940)
    body = f'<main class="canvas {"mobile" if mobile else ""}" style="width:{width}px;height:{height}px"><div class="ring"></div><div class="ring second"></div><div class="brand"><img src="/src/CSweet.App/wwwroot/images/icon.svg" alt="">C-Sweet</div><div class="side-label">YOUR COMPANY. YOUR CONTROL.</div><h1 class="headline">Run the<br>company.</h1><div class="subtitle">Turn direction into connected work.<br>Keep the next decision in view.</div><div class="mini-rule"></div>'
    body += frame('board','software-board.png','Northstar Software · First milestone')
    body += frame('direction','software-chat-focus.png','Start with your direction')
    body += frame('ticket','software-ticket-0.png','An owner. A visible next step.')
    body += '<div class="roles">'+''.join(f'<div class="role"><img src="/src/CSweet.UI/wwwroot/images/agents/{p}-v1.jpg" alt=""><span>{r}</span></div>' for p,r in [('product-manager','Product'),('software-architect','Architecture'),('software-developer','Development'),('software-qa','QA')])+'</div>'
    body += '<div class="foot">ACTUAL APP VIEWS · FICTIONAL SAMPLE DATA</div></main>'
    (PREVIEW/f'hero{"-mobile" if mobile else ""}.html').write_text(page(body),encoding='utf-8')
    width, height = (800, 2760) if mobile else (1600, 960)
    body = f'<main class="canvas workspace {"mobile" if mobile else ""}" style="width:{width}px;height:{height}px"><div class="ring"></div><div class="brand"><span class="eyebrow">A WORKSPACE YOU CAN INSPECT</span></div><h1>See the people. Follow the work.<br>Inspect the revision.</h1><p class="intro">The app, in Aurora. One connected first milestone.</p>'
    body += '<section class="feature people"><h2>01 / People &amp; roles</h2><p>Know who owns the next step.</p><div class="box"><img src="/assets/readme/screenshots/software-employee-0.png" alt="Product Manager employee card"><img src="/assets/readme/screenshots/software-employee-1.png" alt="Architect employee card"></div></section>'
    body += '<section class="feature work"><h2>02 / Assigned work</h2><p>Keep each assignment visible.</p><div class="box">'+''.join(f'<img src="/assets/readme/screenshots/software-ticket-{i}.png" alt="Assigned work card {i+1}">' for i in range(3))+'</div></section>'
    body += '<section class="feature document"><h2>03 / Revisioned documents</h2><p>Review the result in context.</p><div class="box"><img src="/assets/readme/screenshots/software-document-content-mobile.png" alt="Revision 1 of the sample client portal review packet"></div></section>'
    body += '<div class="foot">NORTHSTAR SOFTWARE · FICTIONAL SAMPLE RECORDS · NO EXECUTION-SPEED CLAIM</div></main>'
    (PREVIEW/f'workspace{"-mobile" if mobile else ""}.html').write_text(page(body),encoding='utf-8')

print('Built four editable Aurora SVGs and four screenshot compositions.')
