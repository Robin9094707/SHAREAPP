from pathlib import Path
import re, subprocess, tempfile
html=(Path(__file__).resolve().parents[1]/'web/index.html').read_text()
ids=re.findall(r'\bid="([^"]+)"',html)
assert len(ids)==len(set(ids)), 'Doppelte HTML-IDs'
for selector in re.findall(r'\$\([\'\"]#([A-Za-z][\w-]*)[\'\"]\)',html):assert selector in ids, selector
with tempfile.TemporaryDirectory() as folder:
    path=Path(folder)/'app.js';path.write_text(html.split('<script>')[1].split('</script>')[0]);subprocess.run(['node','--check',str(path)],check=True)
print('HTML-Verknüpfungen und JavaScript-Syntax geprüft.')
