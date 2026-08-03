#!/usr/bin/env python3
import hashlib,json,sys,pathlib
r=pathlib.Path(sys.argv[1]); p=r/'config/appsettings.json'; c=json.loads(p.read_text())
for d in c['drivers']:
 f=r/'drivers'/d['directory']/d['executable']; d['sha256']=hashlib.sha256(f.read_bytes()).hexdigest().upper()
p.write_text(json.dumps(c,indent=2)+"\n")
