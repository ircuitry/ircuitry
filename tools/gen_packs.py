#!/usr/bin/env python3
"""Generate ircuitry community-node packs (.ircnode) for code editing + GitHub.

Each node is a script-backed custom node. The script runs through ircuitry's CodeRunner:
inputs/params arrive as UPPERCASE env vars (first data input also as INPUT); stdout becomes the
output (a JSON object maps to named pins, otherwise raw → the first data output).

NOTE: input/param names must avoid clobbering real env vars in the subprocess (PATH, HOME, ...),
so we use FILE/DIR/ROOT/CMD/etc, never "path".
"""
import json, os, sys

REPO = os.path.join(os.path.dirname(__file__), "..", "docs", "examples", "packs")
HOME = os.path.join(os.path.expanduser("~"), "ircuitry", "nodes")

def ex(name=""):    return {"name": name, "kind": "Exec"}
def tx(name):       return {"name": name, "kind": "Text"}
def nm(name):       return {"name": name, "kind": "Number"}

def node(typeId, title, icon, category, desc, inputs, outputs, params, code):
    return {
        "typeId": typeId, "title": title, "subtitle": "community", "icon": icon,
        "category": category, "description": desc,
        "inputs": inputs, "outputs": outputs, "params": params,
        "language": "python", "timeout": 20, "code": code,
    }

NODES = []

# ============================ AI CODE-EDITING TOOLS ============================
# Designed to wire into an "AI Tool": route the tool's arg outputs into these inputs,
# and the node's output into a "Tool Reply".

NODES.append(node(
    "tool.readfile", "Read File", "📖", "Storage",
    "Reads a file (under an optional workspace Root) and outputs its contents. Wire an AI Tool's arg into 'file'.",
    [ex(), tx("file")], [ex("then"), tx("content")],
    [{"key": "root", "label": "Workspace root", "type": "Text", "default": "", "placeholder": "/path/to/repo (optional)"}],
    "import os\n"
    "root=os.environ.get('ROOT','')\n"
    "f=os.environ.get('FILE','')\n"
    "p=os.path.join(root,f) if root else f\n"
    "try:\n"
    "    print(open(p,'r',errors='replace').read(200000))\n"
    "except Exception as e:\n"
    "    print('ERROR: '+str(e))\n"))

NODES.append(node(
    "tool.listfiles", "List Files", "📂", "Storage",
    "Lists files under Root matching a glob (e.g. **/*.cs). Outputs newline-separated relative paths.",
    [ex(), tx("glob")], [ex("then"), tx("files")],
    [{"key": "root", "label": "Workspace root", "type": "Text", "default": ".", "placeholder": "."}],
    "import os,glob\n"
    "root=os.environ.get('ROOT','.') or '.'\n"
    "pat=os.environ.get('GLOB','**/*') or '**/*'\n"
    "hits=[os.path.relpath(h,root) for h in glob.glob(os.path.join(root,pat),recursive=True) if os.path.isfile(h)]\n"
    "print('\\n'.join(sorted(hits)[:500]) if hits else 'no files')\n"))

NODES.append(node(
    "tool.search", "Search Files (grep)", "🔎", "Storage",
    "Regex-searches files under Root and outputs file:line: text for each match (capped).",
    [ex(), tx("pattern")], [ex("then"), tx("matches")],
    [{"key": "root", "label": "Workspace root", "type": "Text", "default": ".", "placeholder": "."},
     {"key": "glob", "label": "File glob", "type": "Text", "default": "**/*", "placeholder": "**/*.cs"}],
    "import os,glob,re\n"
    "root=os.environ.get('ROOT','.') or '.'\n"
    "g=os.environ.get('GLOB','**/*') or '**/*'\n"
    "try:\n"
    "    rx=re.compile(os.environ.get('PATTERN',''))\n"
    "except Exception as e:\n"
    "    print('bad pattern: '+str(e)); raise SystemExit\n"
    "out=[]\n"
    "for f in glob.glob(os.path.join(root,g),recursive=True):\n"
    "    if not os.path.isfile(f): continue\n"
    "    try:\n"
    "        for i,line in enumerate(open(f,errors='replace'),1):\n"
    "            if rx.search(line):\n"
    "                out.append(os.path.relpath(f,root)+':'+str(i)+': '+line.rstrip()[:200])\n"
    "                if len(out)>=200: break\n"
    "    except: pass\n"
    "    if len(out)>=200: break\n"
    "print('\\n'.join(out) if out else 'no matches')\n"))

NODES.append(node(
    "tool.editfile", "Edit File", "✏️", "Storage",
    "Replaces text in a file (first match, or all). Wire AI Tool args into file/find/replace.",
    [ex(), tx("file"), tx("find"), tx("replace")], [ex("then"), tx("result")],
    [{"key": "root", "label": "Workspace root", "type": "Text", "default": "", "placeholder": "(optional)"},
     {"key": "all", "label": "Replace all", "type": "Bool", "default": "false"}],
    "import os\n"
    "root=os.environ.get('ROOT','')\n"
    "f=os.environ.get('FILE','')\n"
    "find=os.environ.get('FIND','')\n"
    "repl=os.environ.get('REPLACE','')\n"
    "allv=os.environ.get('ALL','false').lower()=='true'\n"
    "p=os.path.join(root,f) if root else f\n"
    "try:\n"
    "    if find=='':\n"
    "        print('ERROR: empty find'); raise SystemExit\n"
    "    s=open(p,errors='replace').read()\n"
    "    n=s.count(find)\n"
    "    if n==0:\n"
    "        print('not found: '+find); raise SystemExit\n"
    "    s=s.replace(find,repl) if allv else s.replace(find,repl,1)\n"
    "    open(p,'w').write(s)\n"
    "    print('replaced '+str(n if allv else 1)+' occurrence(s) in '+f)\n"
    "except SystemExit: raise\n"
    "except Exception as e:\n"
    "    print('ERROR: '+str(e))\n"))

NODES.append(node(
    "tool.run", "Run Command", "⚙️", "Action",
    "Runs a shell command in a working directory and returns stdout+stderr and the exit code. POWERFUL - gives shell access.",
    [ex(), tx("cmd")], [ex("then"), tx("stdout"), nm("exit")],
    [{"key": "cwd", "label": "Working dir", "type": "Text", "default": ".", "placeholder": "."}],
    "import os,json,subprocess\n"
    "cmd=os.environ.get('CMD','')\n"
    "cwd=os.environ.get('CWD','.') or '.'\n"
    "if not cmd:\n"
    "    print(json.dumps({'stdout':'','exit':'0'})); raise SystemExit\n"
    "try:\n"
    "    r=subprocess.run(cmd,shell=True,cwd=cwd,capture_output=True,text=True,timeout=15)\n"
    "    print(json.dumps({'stdout':(r.stdout+r.stderr)[:6000],'exit':str(r.returncode)}))\n"
    "except Exception as e:\n"
    "    print(json.dumps({'stdout':'ERROR: '+str(e),'exit':'1'}))\n"))

# ================================ GITHUB (gh) ================================
def gh(typeId, title, icon, desc, inputs, outputs, params, code):
    NODES.append(node(typeId, title, icon, "Action", desc, inputs, outputs, params, code))

gh("gh.run", "GitHub: Run gh", "🐙",
   "Runs any gh CLI command (uses your existing gh auth), e.g. ghargs = issue list. Outputs the result.",
   [ex(), tx("ghargs")], [ex("then"), tx("output")], [],
   "import os,subprocess,shlex\n"
   "args=os.environ.get('GHARGS','')\n"
   "try:\n"
   "    r=subprocess.run(['gh']+shlex.split(args),capture_output=True,text=True,timeout=18)\n"
   "    print((r.stdout+r.stderr).strip()[:6000] or 'ok')\n"
   "except Exception as e:\n"
   "    print('ERROR: '+str(e))\n")

gh("gh.issue.create", "GitHub: Create Issue", "🐛",
   "Opens an issue and outputs its URL.",
   [ex()], [ex("then"), tx("url")],
   [{"key": "repo", "label": "Repo", "type": "Text", "default": "", "placeholder": "owner/name (blank = current)"},
    {"key": "title", "label": "Title", "type": "Text", "default": ""},
    {"key": "body", "label": "Body", "type": "Multiline", "default": ""}],
   "import os,subprocess\n"
   "cmd=['gh','issue','create','-t',os.environ.get('TITLE',''),'-b',os.environ.get('BODY','')]\n"
   "repo=os.environ.get('REPO','')\n"
   "if repo: cmd+=['-R',repo]\n"
   "try:\n"
   "    r=subprocess.run(cmd,capture_output=True,text=True,timeout=18)\n"
   "    print((r.stdout or r.stderr).strip()[:2000])\n"
   "except Exception as e:\n"
   "    print('ERROR: '+str(e))\n")

gh("gh.pr.list", "GitHub: List PRs", "🔀",
   "Lists pull requests.",
   [ex()], [ex("then"), tx("prs")],
   [{"key": "repo", "label": "Repo", "type": "Text", "default": "", "placeholder": "owner/name (blank = current)"},
    {"key": "state", "label": "State", "type": "Choice", "default": "open", "choices": ["open", "closed", "merged", "all"]},
    {"key": "limit", "label": "Limit", "type": "Int", "default": "10"}],
   "import os,subprocess\n"
   "cmd=['gh','pr','list','--state',os.environ.get('STATE','open'),'--limit',os.environ.get('LIMIT','10')]\n"
   "repo=os.environ.get('REPO','')\n"
   "if repo: cmd+=['-R',repo]\n"
   "try:\n"
   "    r=subprocess.run(cmd,capture_output=True,text=True,timeout=18)\n"
   "    print((r.stdout or r.stderr).strip()[:4000] or 'none')\n"
   "except Exception as e:\n"
   "    print('ERROR: '+str(e))\n")

gh("gh.comment", "GitHub: Comment", "💬",
   "Comments on an issue or PR by number.",
   [ex(), tx("body")], [ex("then"), tx("result")],
   [{"key": "repo", "label": "Repo", "type": "Text", "default": "", "placeholder": "owner/name (blank = current)"},
    {"key": "number", "label": "Issue/PR #", "type": "Text", "default": ""}],
   "import os,subprocess\n"
   "cmd=['gh','issue','comment',os.environ.get('NUMBER',''),'-b',os.environ.get('BODY','')]\n"
   "repo=os.environ.get('REPO','')\n"
   "if repo: cmd+=['-R',repo]\n"
   "try:\n"
   "    r=subprocess.run(cmd,capture_output=True,text=True,timeout=18)\n"
   "    print((r.stdout or r.stderr).strip()[:2000] or 'commented')\n"
   "except Exception as e:\n"
   "    print('ERROR: '+str(e))\n")

gh("gh.api", "GitHub: API", "🛰️",
   "Calls the GitHub REST/GraphQL API via gh api, e.g. endpoint = repos/owner/name.",
   [ex(), tx("endpoint")], [ex("then"), tx("json")],
   [{"key": "method", "label": "Method", "type": "Choice", "default": "GET", "choices": ["GET", "POST", "PATCH", "DELETE"]}],
   "import os,subprocess\n"
   "cmd=['gh','api',os.environ.get('ENDPOINT',''),'-X',os.environ.get('METHOD','GET')]\n"
   "try:\n"
   "    r=subprocess.run(cmd,capture_output=True,text=True,timeout=18)\n"
   "    print((r.stdout or r.stderr).strip()[:6000])\n"
   "except Exception as e:\n"
   "    print('ERROR: '+str(e))\n")

# ================================ write out ================================
def main():
    os.makedirs(REPO, exist_ok=True)
    os.makedirs(HOME, exist_ok=True)
    # give the GitHub nodes a real GitHub logo (base64 PNG icon)
    import base64
    ghpng = os.path.join(os.path.dirname(__file__), "icons", "github.png")
    if os.path.exists(ghpng):
        b64 = base64.b64encode(open(ghpng, "rb").read()).decode()
        for n in NODES:
            if n["typeId"].startswith("gh."):
                n["iconImage"] = b64
    for n in NODES:
        fn = n["typeId"] + ".ircnode"
        blob = json.dumps(n, indent=2, ensure_ascii=False)
        for d in (REPO, HOME):
            with open(os.path.join(d, fn), "w") as f:
                f.write(blob + "\n")
    print(f"wrote {len(NODES)} nodes to:\n  {os.path.abspath(REPO)}\n  {HOME}")
    for n in NODES:
        print("  -", n["typeId"], "·", n["title"])

if __name__ == "__main__":
    main()
