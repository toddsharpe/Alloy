# Ranks the hottest frames of a PageProbe run from a speedscope trace: self time excluding sleeps and waits, then inclusive time of Alloy frames
# dotnet-trace collect --output probe.nettrace -- tools/PageProbe/bin/Debug/net10.0/PageProbe.exe https://github.com -wait 500
# dotnet-trace convert probe.nettrace --format speedscope --output probe
# python tools/PageProbe/speedscope_top.py probe.speedscope.json 40
import json, sys, collections

path = sys.argv[1]
top = int(sys.argv[2]) if len(sys.argv) > 2 else 40
data = json.load(open(path, encoding='utf-8'))
frames = data['shared']['frames']
names = [f.get('name', '?') for f in frames]
self_time = collections.Counter()
incl_time = collections.Counter()
total = 0.0
# the busiest thread is the page load worker
profile = max(data['profiles'], key=lambda p: len(p.get('events', [])))
print('thread', profile.get('name'), 'events', len(profile['events']))
stack = []
last = profile['startValue']
for event in profile['events']:
    at = event['at']
    dt = at - last
    blocked = any(('Sleep' in names[f] or 'Monitor.Wait' in names[f] or 'PollGC' in names[f] or 'recv' in names[f] or 'WaitFor' in names[f]) for f in stack)
    if dt > 0 and stack and not blocked:
        total += dt
        leaf = next((f for f in reversed(stack) if names[f] not in ('CPU_TIME', 'UNMANAGED_CODE_TIME')), stack[-1])
        self_time[leaf] += dt
        for idx in set(stack):
            incl_time[idx] += dt
    last = at
    if event['type'] == 'O':
        stack.append(event['frame'])
    elif stack:
        stack.pop()

def short(n):
    for prefix in ('Alloy.Sdk.Web.', 'Alloy.Sdk.', 'System.Private.CoreLib.il!', 'PageProbe!', 'System.'):
        n = n.replace(prefix, '')
    return n[:120]

print(f'total ms on stack: {total:.0f}')
print('--- self time')
for idx, w in self_time.most_common(top):
    print(f'{w:8.0f} {100*w/total:5.1f}%  {short(names[idx])}')
print('--- inclusive (Alloy frames only)')
shown = 0
for idx, w in incl_time.most_common(600):
    if 'Alloy' not in names[idx]:
        continue
    print(f'{w:8.0f} {100*w/total:5.1f}%  {short(names[idx])}')
    shown += 1
    if shown >= top:
        break
