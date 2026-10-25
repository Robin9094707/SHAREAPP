from pathlib import Path
root=Path(__file__).resolve().parents[1]
source=(root/'server/robinshare.py').read_text()
html=(root/'web/index.html').read_text()
source=source.replace("INDEX_HTML = ''  # Wird beim Paketbau eingebettet.", 'INDEX_HTML = '+repr(html))
(root/'dist').mkdir(exist_ok=True)
(root/'dist/RobinsShare-Server.py').write_text(source)
print('dist/RobinsShare-Server.py erstellt – ohne Zusatzpakete startbar.')
