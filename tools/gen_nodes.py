#!/usr/bin/env python3
"""Generate 50 useful ircuitry community nodes (.ircnode), Python-stdlib only.

Convention: a node's primary input pin (uppercased) is read from env, falling back to ARGS, so
"!cmd <args>" works whether or not the input is wired. stdout becomes the output (JSON object -> named
pins; else raw -> first data output). Web nodes use urllib, short timeouts, and degrade gracefully.
"""
import json, os

REPO = os.path.join(os.path.dirname(__file__), "..", "docs", "examples", "nodes")
HOME = os.path.join(os.path.expanduser("~"), "ircuitry", "nodes")

def ex(n=""): return {"name": n, "kind": "Exec"}
def tx(n):    return {"name": n, "kind": "Text"}
def nm(n):    return {"name": n, "kind": "Number"}

NODES = []
def N(typeId, title, icon, category, desc, inputs, outputs, code, params=None, timeout=12):
    NODES.append({"typeId": typeId, "title": title, "subtitle": "community", "icon": icon,
                  "category": category, "description": desc, "inputs": inputs, "outputs": outputs,
                  "params": params or [], "language": "python", "timeout": timeout, "code": code})

T_IN = [ex(), tx("text")]
T_OUT = [ex("then"), tx("result")]

# ============================== TEXT & IRC ==============================
N("text.leet", "Leetspeak", "🅻", "Data", "Converts text to l33t sp34k.", T_IN, T_OUT, r'''
import os
s=(os.environ.get('TEXT') or os.environ.get('ARGS') or '')
m={'a':'4','e':'3','i':'1','o':'0','t':'7','s':'5','l':'1','b':'8','g':'9'}
print(''.join(m.get(c.lower(),c) for c in s))
''')
N("text.mock", "Mock Case", "🤪", "Data", "mOcKiNg SpOnGeBoB alternating caps.", T_IN, T_OUT, r'''
import os
s=(os.environ.get('TEXT') or os.environ.get('ARGS') or '')
o=[];u=False
for c in s:
    if c.isalpha(): o.append(c.upper() if u else c.lower()); u=not u
    else: o.append(c)
print(''.join(o))
''')
N("text.rot13", "ROT13", "🔁", "Data", "ROT13 cipher (its own inverse).", T_IN, T_OUT, r'''
import os,codecs
print(codecs.encode((os.environ.get('TEXT') or os.environ.get('ARGS') or ''),'rot13'))
''')
N("text.b64encode", "Base64 Encode", "🔐", "Data", "Encodes text to Base64.", T_IN, T_OUT, r'''
import os,base64
print(base64.b64encode((os.environ.get('TEXT') or os.environ.get('ARGS') or '').encode()).decode())
''')
N("text.b64decode", "Base64 Decode", "🔓", "Data", "Decodes Base64 back to text.", T_IN, T_OUT, r'''
import os,base64
try: print(base64.b64decode((os.environ.get('TEXT') or os.environ.get('ARGS') or '')+'===').decode('utf-8','replace'))
except Exception as e: print('decode error: '+str(e))
''')
N("text.urlencode", "URL Encode", "🔗", "Data", "Percent-encodes text for URLs.", T_IN, T_OUT, r'''
import os,urllib.parse
print(urllib.parse.quote((os.environ.get('TEXT') or os.environ.get('ARGS') or '')))
''')
N("text.urldecode", "URL Decode", "🔓", "Data", "Decodes percent-encoded URL text.", T_IN, T_OUT, r'''
import os,urllib.parse
print(urllib.parse.unquote((os.environ.get('TEXT') or os.environ.get('ARGS') or '')))
''')
N("text.slug", "Slugify", "🐌", "Data", "Makes a url-friendly slug (lowercase, dashes).", T_IN, T_OUT, r'''
import os,re
s=(os.environ.get('TEXT') or os.environ.get('ARGS') or '').lower()
print(re.sub(r'[^a-z0-9]+','-',s).strip('-') or '(empty)')
''')
N("irc.stripcolor", "Strip IRC Colors", "🧹", "Data", "Removes mIRC color/format codes (\\x03,\\x02,\\x1f,...).", T_IN, T_OUT, r'''
import os,re
s=(os.environ.get('TEXT') or os.environ.get('ARGS') or '')
s=re.sub(r'\x03(\d{1,2}(,\d{1,2})?)?','',s)
print(re.sub(r'[\x02\x0f\x16\x1d\x1e\x1f]','',s))
''')
N("irc.rainbow", "Rainbow Text", "🌈", "Data", "Colours each character with mIRC colour codes.", T_IN, T_OUT, r'''
import os
s=(os.environ.get('TEXT') or os.environ.get('ARGS') or '')
cols=['04','07','08','09','03','11','12','13','06']
o=[];i=0
C=chr(3)
for c in s:
    if c==' ': o.append(' ')
    else: o.append(C+cols[i%len(cols)]+c); i+=1
print(''.join(o)+chr(15))
''')

# ============================== HASH / ENCODE ==============================
N("hash.md5", "MD5 Hash", "#️⃣", "Data", "MD5 hex digest of the input.", T_IN, T_OUT, r'''
import os,hashlib
print(hashlib.md5((os.environ.get('TEXT') or os.environ.get('ARGS') or '').encode()).hexdigest())
''')
N("hash.sha256", "SHA-256 Hash", "🔒", "Data", "SHA-256 hex digest of the input.", T_IN, T_OUT, r'''
import os,hashlib
print(hashlib.sha256((os.environ.get('TEXT') or os.environ.get('ARGS') or '').encode()).hexdigest())
''')
N("morse.encode", "Text to Morse", "📡", "Data", "Encodes text as Morse code.", T_IN, T_OUT, r'''
import os
M={'a':'.-','b':'-...','c':'-.-.','d':'-..','e':'.','f':'..-.','g':'--.','h':'....','i':'..','j':'.---','k':'-.-','l':'.-..','m':'--','n':'-.','o':'---','p':'.--.','q':'--.-','r':'.-.','s':'...','t':'-','u':'..-','v':'...-','w':'.--','x':'-..-','y':'-.--','z':'--..','0':'-----','1':'.----','2':'..---','3':'...--','4':'....-','5':'.....','6':'-....','7':'--...','8':'---..','9':'----.',' ':'/'}
s=(os.environ.get('TEXT') or os.environ.get('ARGS') or '').lower()
print(' '.join(M.get(c,'') for c in s if M.get(c,'')!='').strip() or '(nothing)')
''')
N("morse.decode", "Morse to Text", "📡", "Data", "Decodes Morse code (space between letters, / for space).", T_IN, T_OUT, r'''
import os
M={'.-':'a','-...':'b','-.-.':'c','-..':'d','.':'e','..-.':'f','--.':'g','....':'h','..':'i','.---':'j','-.-':'k','.-..':'l','--':'m','-.':'n','---':'o','.--.':'p','--.-':'q','.-.':'r','...':'s','-':'t','..-':'u','...-':'v','.--':'w','-..-':'x','-.--':'y','--..':'z','-----':'0','.----':'1','..---':'2','...--':'3','....-':'4','.....':'5','-....':'6','--...':'7','---..':'8','----.':'9'}
s=(os.environ.get('TEXT') or os.environ.get('ARGS') or '').strip()
print(''.join(' ' if t=='/' else M.get(t,'?') for t in s.split(' ')) or '(nothing)')
''')

# ============================== FUN / RANDOM ==============================
N("fun.dice", "Dice Roller", "🎲", "Data", "Rolls dice in NdM(+K) notation, e.g. 2d6+3.", [ex(), tx("dice")], T_OUT, r'''
import os,random,re
s=(os.environ.get('DICE') or os.environ.get('ARGS') or '1d6').strip().lower().replace(' ','')
m=re.match(r'^(\d*)d(\d+)([+-]\d+)?$',s)
if not m: print('format: NdM(+K) e.g. 2d6+3'); raise SystemExit
n=min(int(m.group(1) or 1),100); sides=min(max(int(m.group(2)),2),1000); mod=int(m.group(3) or 0)
rolls=[random.randint(1,sides) for _ in range(n)]
tot=sum(rolls)+mod
extra=('  ['+'+'.join(map(str,rolls))+(('%+d'%mod) if mod else '')+']') if (n>1 or mod) else ''
print(f'🎲 {s} -> {tot}{extra}')
''')
N("fun.8ball", "Magic 8-Ball", "🎱", "Data", "Answers a yes/no question.", [ex(), tx("question")], T_OUT, r'''
import os,random
a=['It is certain.','Without a doubt.','Yes definitely.','You may rely on it.','Most likely.','Outlook good.','Signs point to yes.','Reply hazy, try again.','Ask again later.','Cannot predict now.','Don\'t count on it.','My reply is no.','Very doubtful.','Outlook not so good.']
q=(os.environ.get('QUESTION') or os.environ.get('ARGS') or '').strip()
print('🎱 '+random.choice(a))
''')
N("fun.choose", "Choose One", "🎯", "Data", "Picks one option at random from a list (comma or 'or' separated).", [ex(), tx("options")], T_OUT, r'''
import os,re,random
s=(os.environ.get('OPTIONS') or os.environ.get('ARGS') or '')
parts=[p.strip() for p in re.split(r',| or ',s) if p.strip()]
print('🎯 '+random.choice(parts) if parts else 'give me some options (a, b, c)')
''')
N("fun.rps", "Rock Paper Scissors", "✊", "Data", "Play rock/paper/scissors against the bot.", [ex(), tx("move")], T_OUT, r'''
import os,random
beats={'rock':'scissors','paper':'rock','scissors':'paper'}
e={'rock':'✊','paper':'✋','scissors':'✌️'}
you=(os.environ.get('MOVE') or os.environ.get('ARGS') or '').strip().lower()
bot=random.choice(list(beats))
if you not in beats: print('pick rock, paper or scissors'); raise SystemExit
if you==bot: r='tie!'
elif beats[you]==bot: r='you win! 🎉'
else: r='I win! 😎'
print(f'{e[you]} vs {e[bot]} - {r}')
''')
N("fun.insult", "Playful Insult", "😈", "Data", "A cheeky (PG) insult, aimed at {nick} or the given name.", [ex(), tx("target")], T_OUT, r'''
import os,random
adj=['absolute','utterly','spectacularly','remarkably','impressively']
noun=['walnut','potato','keyboard goblin','soggy biscuit','lost packet','404 error','rubber duck','spam filter']
t=(os.environ.get('TARGET') or os.environ.get('ARGS') or os.environ.get('NICK') or 'you').strip()
print(f'{t} is an {random.choice(adj)} {random.choice(noun)}. 😈')
''')
N("fun.compliment", "Compliment", "💖", "Data", "A wholesome compliment for {nick} or the given name.", [ex(), tx("target")], T_OUT, r'''
import os,random
c=['you are a ray of sunshine','your code compiles on the first try','you are the bug-fix this channel needed','you make great choices','your ping is low and your spirits high','you are 100% uptime energy']
t=(os.environ.get('TARGET') or os.environ.get('ARGS') or os.environ.get('NICK') or 'you').strip()
print(f'{t}, {random.choice(c)}. 💖')
''')
N("fun.decide", "Decide", "🤔", "Data", "Yes / No / Maybe to anything.", [ex(), tx("question")], T_OUT, r'''
import os,random
print('🤔 '+random.choice(['yes','no','maybe','definitely','absolutely not','ask me tomorrow','100%','nah']))
''')
N("fun.clap", "Clap Emojify", "👏", "Data", "Puts 👏 between 👏 every 👏 word.", T_IN, T_OUT, r'''
import os
s=(os.environ.get('TEXT') or os.environ.get('ARGS') or '').split()
print(' 👏 '.join(s) if s else 'give 👏 me 👏 words')
''')
N("fun.fortune", "Fortune Cookie", "🥠", "Data", "A random fortune-cookie message.", [ex()], T_OUT, r'''
import random
f=['A pleasant surprise is waiting for you.','Your hard work is about to pay off.','Now is the time to try something new.','Good news will come to you by email.','A thrilling time is in your near future.','The bug you seek is on line 42.','Trust your instincts; ship it.','Beware of off-by-one errors.']
print('🥠 '+random.choice(f))
''')

# ============================== GENERATORS ==============================
N("gen.password", "Password Generator", "🔑", "Data", "Generates a strong random password (length param).", [ex()], T_OUT, r'''
import os,secrets,string
n=int(os.environ.get('LENGTH') or os.environ.get('ARGS') or 16)
n=min(max(n,4),128)
al=string.ascii_letters+string.digits+'!@#$%^&*-_=+'
print(''.join(secrets.choice(al) for _ in range(n)))
''', params=[{"key":"length","label":"Length","type":"Int","default":"16"}])
N("gen.uuid", "UUID", "🆔", "Data", "Generates a random UUID4.", [ex()], T_OUT, r'''
import uuid
print(str(uuid.uuid4()))
''')
N("gen.lorem", "Lorem Ipsum", "📝", "Data", "Generates placeholder lorem-ipsum words.", [ex()], T_OUT, r'''
import os,random
w='lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua enim ad minim veniam quis nostrud'.split()
n=min(max(int(os.environ.get('WORDS') or os.environ.get('ARGS') or 20),1),100)
s=' '.join(random.choice(w) for _ in range(n))
print(s.capitalize()+'.')
''', params=[{"key":"words","label":"Words","type":"Int","default":"20"}])
N("gen.color", "Random Color", "🎨", "Data", "A random colour as hex + RGB.", [ex()], [ex("then"), tx("hex")], r'''
import random
r,g,b=(random.randint(0,255) for _ in range(3))
print('#%02X%02X%02X  rgb(%d, %d, %d)'%(r,g,b,r,g,b))
''')

# ============================== MATH / CONVERT ==============================
N("calc.eval", "Calculator", "🧮", "Data", "Safely evaluates an arithmetic expression (+ - * / % ** // and parens).", [ex(), tx("expr")], T_OUT, r'''
import os,ast,operator as op
ops={ast.Add:op.add,ast.Sub:op.sub,ast.Mult:op.mul,ast.Div:op.truediv,ast.Mod:op.mod,ast.Pow:op.pow,ast.FloorDiv:op.floordiv,ast.USub:op.neg,ast.UAdd:op.pos}
def ev(n):
    if isinstance(n,ast.Constant) and isinstance(n.value,(int,float)): return n.value
    if isinstance(n,ast.BinOp):
        if isinstance(n.op,ast.Pow) and ev(n.right)>256: raise ValueError('exponent too large')
        return ops[type(n.op)](ev(n.left),ev(n.right))
    if isinstance(n,ast.UnaryOp): return ops[type(n.op)](ev(n.operand))
    raise ValueError('bad')
e=(os.environ.get('EXPR') or os.environ.get('ARGS') or '').strip()
try:
    r=ev(ast.parse(e,mode='eval').body); print(('%g'%r) if isinstance(r,float) else str(r))
except Exception: print('cannot evaluate: '+e)
''')
N("calc.base", "Base Converter", "🔢", "Data", "Converts a number between bases. Input like '255' or '0xff' or '0b1010'.", [ex(), tx("number")], T_OUT, r'''
import os
s=(os.environ.get('NUMBER') or os.environ.get('ARGS') or '').strip().lower()
try:
    v=int(s,0) if s.startswith(('0x','0b','0o')) else int(s)
    print(f'dec {v} · hex 0x{v:X} · oct 0o{v:o} · bin 0b{v:b}')
except Exception: print('give an integer (e.g. 255, 0xff, 0b1010)')
''')
N("calc.roman", "Roman Numerals", "🏛️", "Data", "Converts between integers and Roman numerals.", [ex(), tx("value")], T_OUT, r'''
import os
s=(os.environ.get('VALUE') or os.environ.get('ARGS') or '').strip().upper()
vals=[(1000,'M'),(900,'CM'),(500,'D'),(400,'CD'),(100,'C'),(90,'XC'),(50,'L'),(40,'XL'),(10,'X'),(9,'IX'),(5,'V'),(4,'IV'),(1,'I')]
try:
    if s.isdigit():
        n=int(s); o=''
        if not 1<=n<=3999: print('1-3999 only'); raise SystemExit
        for v,r in vals:
            while n>=v: o+=r; n-=v
        print(o)
    else:
        rmap={'I':1,'V':5,'X':10,'L':50,'C':100,'D':500,'M':1000}; t=0;p=0
        for ch in reversed(s):
            if ch not in rmap: print('not a roman numeral'); raise SystemExit
            v=rmap[ch]; t+=-v if v<p else v; p=v
        print(t)
except SystemExit: pass
''')
N("calc.temp", "Temperature Convert", "🌡️", "Data", "Converts temperature, e.g. '100C', '212F', '300K'.", [ex(), tx("value")], T_OUT, r'''
import os,re
s=(os.environ.get('VALUE') or os.environ.get('ARGS') or '').strip().upper().replace('°','')
m=re.match(r'^(-?\d+(?:\.\d+)?)\s*([CFK])$',s)
if not m: print('e.g. 100C, 212F, 300K'); raise SystemExit
v=float(m.group(1));u=m.group(2)
c = v if u=='C' else (v-32)*5/9 if u=='F' else v-273.15
print('%.1f°C · %.1f°F · %.1fK'%(c, c*9/5+32, c+273.15))
''')
N("calc.units", "Unit Convert", "📏", "Data", "Length/mass, e.g. '5 km', '10 mi', '70 kg', '150 lb'.", [ex(), tx("value")], T_OUT, r'''
import os,re
s=(os.environ.get('VALUE') or os.environ.get('ARGS') or '').strip().lower()
m=re.match(r'^(-?\d+(?:\.\d+)?)\s*([a-z]+)$',s)
if not m: print('e.g. 5 km, 10 mi, 70 kg, 150 lb'); raise SystemExit
v=float(m.group(1));u=m.group(2)
if u in ('km','mi','m','ft'):
    km = v if u=='km' else v*1.60934 if u=='mi' else v/1000 if u=='m' else v*0.0003048
    print('%.3f km · %.3f mi · %.1f m'%(km, km/1.60934, km*1000))
elif u in ('kg','lb','g','oz'):
    kg = v if u=='kg' else v*0.453592 if u=='lb' else v/1000 if u=='g' else v*0.0283495
    print('%.3f kg · %.3f lb · %.0f g'%(kg, kg/0.453592, kg*1000))
else: print('units: km mi m ft kg lb g oz')
''')
N("calc.percent", "Percentage", "％", "Data", "X% of Y, or X out of Y. e.g. '20% of 150' or '30 of 120'.", [ex(), tx("expr")], T_OUT, r'''
import os,re
s=(os.environ.get('EXPR') or os.environ.get('ARGS') or '').strip().lower()
m=re.match(r'^(-?\d+(?:\.\d+)?)\s*%\s*of\s*(-?\d+(?:\.\d+)?)$',s)
if m: print('%g'%(float(m.group(1))/100*float(m.group(2)))); raise SystemExit
m=re.match(r'^(-?\d+(?:\.\d+)?)\s*of\s*(-?\d+(?:\.\d+)?)$',s)
if m and float(m.group(2)): print('%g%%'%(float(m.group(1))/float(m.group(2))*100)); raise SystemExit
print("try '20% of 150' or '30 of 120'")
''')
N("calc.tip", "Tip Split", "🧾", "Data", "Tip + split a bill. Input 'amount [tip%] [people]', e.g. '80 18 4'.", [ex(), tx("bill")], T_OUT, r'''
import os
p=(os.environ.get('BILL') or os.environ.get('ARGS') or '').split()
try:
    amt=float(p[0]); pct=float(p[1]) if len(p)>1 else 18.0; ppl=int(p[2]) if len(p)>2 else 1
    tip=amt*pct/100; tot=amt+tip
    print('bill %.2f + %.0f%% tip %.2f = %.2f  (%.2f each / %d)'%(amt,pct,tip,tot,tot/max(ppl,1),max(ppl,1)))
except Exception: print('e.g. 80 18 4  (amount tip%% people)')
''')
N("num.words", "Number to Words", "🔤", "Data", "Spells an integer in English, e.g. 42 -> forty-two.", [ex(), tx("number")], T_OUT, r'''
import os
ones='zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen'.split()
tens='_ _ twenty thirty forty fifty sixty seventy eighty ninety'.split()
def w(n):
    if n<0: return 'negative '+w(-n)
    if n<20: return ones[n]
    if n<100: return tens[n//10]+('-'+ones[n%10] if n%10 else '')
    if n<1000: return ones[n//100]+' hundred'+(' '+w(n%100) if n%100 else '')
    if n<1000000: return w(n//1000)+' thousand'+(' '+w(n%1000) if n%1000 else '')
    if n<1000000000: return w(n//1000000)+' million'+(' '+w(n%1000000) if n%1000000 else '')
    return str(n)
s=(os.environ.get('NUMBER') or os.environ.get('ARGS') or '').strip()
try: print(w(int(s)))
except Exception: print('give an integer')
''')

# ============================== DATE / TIME ==============================
N("time.now", "Current Time", "🕐", "Data", "The current local date and time.", [ex()], T_OUT, r'''
import datetime
print(datetime.datetime.now().strftime('%A %d %B %Y, %H:%M:%S'))
''')
N("time.until", "Countdown", "⏳", "Data", "Time remaining until a date/time, e.g. '2026-12-31' or '2026-12-31 09:00'.", [ex(), tx("when")], T_OUT, r'''
import os,datetime
s=(os.environ.get('WHEN') or os.environ.get('ARGS') or '').strip()
dt=None
for f in ('%Y-%m-%d %H:%M','%Y-%m-%d','%d/%m/%Y','%H:%M'):
    try: dt=datetime.datetime.strptime(s,f); break
    except: pass
if not dt: print("date like 2026-12-31 or 2026-12-31 09:00"); raise SystemExit
now=datetime.datetime.now()
if dt.year==1900: dt=dt.replace(year=now.year,month=now.month,day=now.day)
d=dt-now; secs=int(d.total_seconds())
if secs<0: print('that was '+str(-d).split('.')[0]+' ago'); raise SystemExit
days=secs//86400; h=secs%86400//3600; m=secs%3600//60
print(f'{days}d {h}h {m}m until {dt.strftime("%Y-%m-%d %H:%M")}')
''')
N("time.unix", "Unix Timestamp", "⏱️", "Data", "Current unix timestamp, or convert one to a date.", [ex(), tx("ts")], T_OUT, r'''
import os,datetime
s=(os.environ.get('TS') or os.environ.get('ARGS') or '').strip()
if not s: print(int(datetime.datetime.now().timestamp())); raise SystemExit
try: print(datetime.datetime.fromtimestamp(int(s)).strftime('%Y-%m-%d %H:%M:%S'))
except Exception: print('give a unix timestamp (or leave blank for now)')
''')
N("time.age", "Age Calculator", "🎂", "Data", "Age from a birthdate (YYYY-MM-DD).", [ex(), tx("birthdate")], T_OUT, r'''
import os,datetime
s=(os.environ.get('BIRTHDATE') or os.environ.get('ARGS') or '').strip()
try:
    b=datetime.datetime.strptime(s,'%Y-%m-%d').date(); t=datetime.date.today()
    y=t.year-b.year-((t.month,t.day)<(b.month,b.day))
    print(f'{y} years old ({(t-b).days} days)')
except Exception: print('birthdate like 1990-05-21')
''')
N("time.tz", "Time in Timezone", "🌍", "Data", "Current time in an IANA timezone, e.g. 'Europe/London', 'America/New_York'.", [ex(), tx("zone")], T_OUT, r'''
import os,datetime
try: from zoneinfo import ZoneInfo
except Exception: print('zoneinfo unavailable'); raise SystemExit
z=(os.environ.get('ZONE') or os.environ.get('ARGS') or 'UTC').strip()
try: print(datetime.datetime.now(ZoneInfo(z)).strftime('%Y-%m-%d %H:%M:%S %Z'))
except Exception: print('unknown zone: '+z+'  (e.g. Europe/London)')
''')

# ============================== WEB (no API key) ==============================
def web(typeId, title, icon, desc, inputs, code):
    N(typeId, title, icon, "Action", desc, inputs, [ex("then"), tx("result")], code, timeout=15)

GET = r'''
import os,urllib.request,urllib.parse,json
def get(url,headers=None,timeout=9):
    req=urllib.request.Request(url,headers=headers or {'User-Agent':'ircuitry-bot'})
    return urllib.request.urlopen(req,timeout=timeout).read().decode('utf-8','replace')
'''
web("web.weather", "Weather", "🌦️", "Current weather for a place (wttr.in, no key).", [ex(), tx("place")], GET + r'''
q=(os.environ.get('PLACE') or os.environ.get('ARGS') or '').strip()
try: print(get('https://wttr.in/'+urllib.parse.quote(q)+'?format=%l:+%c+%t+(feels+%f),+%h+humidity,+wind+%w').strip())
except Exception as e: print('weather error: '+str(e)[:80])
''')
web("web.wiki", "Wikipedia", "📚", "First-paragraph summary of a Wikipedia article (no key).", [ex(), tx("topic")], GET + r'''
q=(os.environ.get('TOPIC') or os.environ.get('ARGS') or '').strip().replace(' ','_')
try:
    d=json.loads(get('https://en.wikipedia.org/api/rest_v1/page/summary/'+urllib.parse.quote(q)))
    ex=d.get('extract','no summary'); print((ex[:380]+'…' if len(ex)>380 else ex))
except Exception as e: print('not found / error: '+str(e)[:60])
''')
web("web.define", "Dictionary", "📖", "Defines a word (dictionaryapi.dev, no key).", [ex(), tx("word")], GET + r'''
w=(os.environ.get('WORD') or os.environ.get('ARGS') or '').strip()
try:
    d=json.loads(get('https://api.dictionaryapi.dev/api/v2/entries/en/'+urllib.parse.quote(w)))
    m=d[0]['meanings'][0]; defn=m['definitions'][0]['definition']
    print(f"{w} ({m['partOfSpeech']}): {defn}")
except Exception: print('no definition for: '+w)
''')
web("web.urban", "Urban Dictionary", "🏙️", "Slang definition from Urban Dictionary (no key).", [ex(), tx("term")], GET + r'''
t=(os.environ.get('TERM') or os.environ.get('ARGS') or '').strip()
try:
    d=json.loads(get('https://api.urbandictionary.com/v0/define?term='+urllib.parse.quote(t)))
    if not d.get('list'): print('no entry for: '+t); raise SystemExit
    de=d['list'][0]['definition'].replace('[','').replace(']','').replace(chr(10),' ')
    print(f'{t}: '+(de[:360]+'…' if len(de)>360 else de))
except SystemExit: pass
except Exception as e: print('error: '+str(e)[:60])
''')
web("web.crypto", "Crypto Price", "₿", "Current price of a coin in USD (CoinGecko, no key). e.g. bitcoin, ethereum.", [ex(), tx("coin")], GET + r'''
c=(os.environ.get('COIN') or os.environ.get('ARGS') or 'bitcoin').strip().lower()
try:
    d=json.loads(get('https://api.coingecko.com/api/v3/simple/price?ids='+urllib.parse.quote(c)+'&vs_currencies=usd&include_24hr_change=true'))
    if c not in d: print('unknown coin: '+c); raise SystemExit
    print('%s: $%s (%+.2f%% 24h)'%(c, format(d[c]['usd'],','), d[c].get('usd_24h_change',0)))
except SystemExit: pass
except Exception as e: print('error: '+str(e)[:60])
''')
web("web.currency", "Currency Convert", "💱", "Convert currency, e.g. '100 USD EUR' (Frankfurter, no key).", [ex(), tx("query")], GET + r'''
p=(os.environ.get('QUERY') or os.environ.get('ARGS') or '').strip().upper().split()
try:
    amt=float(p[0]); src=p[1]; dst=p[2]
    d=json.loads(get(f'https://api.frankfurter.app/latest?amount={amt}&from={src}&to={dst}'))
    print('%s %s = %s %s'%(amt,src,format(list(d['rates'].values())[0],','),dst))
except Exception: print('e.g. 100 USD EUR')
''')
web("web.shorten", "Shorten URL", "🔗", "Shortens a URL with is.gd (no key).", [ex(), tx("url")], GET + r'''
u=(os.environ.get('URL') or os.environ.get('ARGS') or '').strip()
try: print(get('https://is.gd/create.php?format=simple&url='+urllib.parse.quote(u)).strip())
except Exception as e: print('error: '+str(e)[:60])
''')
web("web.dadjoke", "Dad Joke", "😄", "A random dad joke (icanhazdadjoke, no key).", [ex()], GET + r'''
try:
    d=json.loads(get('https://icanhazdadjoke.com/',{'Accept':'application/json','User-Agent':'ircuitry-bot'}))
    print(d.get('joke','...'))
except Exception as e: print('error: '+str(e)[:60])
''')
web("web.advice", "Advice", "💡", "A random piece of advice (adviceslip, no key).", [ex()], GET + r'''
try:
    d=json.loads(get('https://api.adviceslip.com/advice'))
    print('💡 '+d['slip']['advice'])
except Exception as e: print('error: '+str(e)[:60])
''')
web("web.fact", "Random Fact", "🧠", "A random interesting fact (uselessfacts, no key).", [ex()], GET + r'''
try:
    d=json.loads(get('https://uselessfacts.jsph.pl/api/v2/facts/random?language=en'))
    print('🧠 '+d.get('text','...'))
except Exception as e: print('error: '+str(e)[:60])
''')
web("web.iplookup", "IP / Host Lookup", "🌐", "Geolocates an IP or hostname (ip-api.com, no key).", [ex(), tx("host")], GET + r'''
h=(os.environ.get('HOST') or os.environ.get('ARGS') or '').strip()
try:
    d=json.loads(get('http://ip-api.com/json/'+urllib.parse.quote(h)))
    if d.get('status')!='success': print('lookup failed: '+d.get('message','?')); raise SystemExit
    print('%s -> %s, %s, %s · %s'%(d.get('query'),d.get('city'),d.get('regionName'),d.get('country'),d.get('isp')))
except SystemExit: pass
except Exception as e: print('error: '+str(e)[:60])
''')

def main():
    os.makedirs(REPO, exist_ok=True); os.makedirs(HOME, exist_ok=True)
    for n in NODES:
        blob=json.dumps(n,indent=2,ensure_ascii=False)+"\n"
        fn=n["typeId"]+".ircnode"
        for d in (REPO,HOME): open(os.path.join(d,fn),"w").write(blob)
    print(f"wrote {len(NODES)} nodes")
    assert len(NODES) >= 50, f"expected >=50, got {len(NODES)}"

if __name__=="__main__": main()
