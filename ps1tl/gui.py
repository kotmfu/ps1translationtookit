"""Local web GUI: python -m ps1tl gui [game.cue]  -> http://127.0.0.1:8732"""
import json, os, tempfile, webbrowser
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs
from .project import Project

HTML = os.path.join(os.path.dirname(__file__), 'gui.html')
P = Project()


class Server(ThreadingHTTPServer):
    allow_reuse_address = False   # on Windows reuse lets a second copy bind the same port silently


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a): pass

    def send(self, body, ctype='application/json', code=200, extra=None):
        if not isinstance(body, (bytes, bytearray)): body = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(body)))
        for k, v in (extra or {}).items(): self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def body(self):
        n = int(self.headers.get('Content-Length', 0))
        return self.rfile.read(n) if n else b''

    def do_GET(self):
        u = urlparse(self.path); q = {k: v[0] for k, v in parse_qs(u.query).items()}
        try:
            if u.path == '/': return self.send(open(HTML, 'rb').read(), 'text/html; charset=utf-8')
            if u.path == '/api/state': return self.send(P.summary())
            if u.path == '/api/rows': return self.send(P.rows(q))
            if u.path == '/api/job': return self.send(dict(P.job, live_text=P.live_text()))
            if u.path == '/api/jp.png': return self.send(P.jp_png(int(q['i'])), 'image/png')
            if u.path == '/api/en.png': return self.send(P.en_png(q.get('t', ''), int(q['i']) if 'i' in q else None), 'image/png')
            if u.path == '/api/export':
                tmp = os.path.join(tempfile.gettempdir(), 'ps1tl-export.csv')
                P.export_tl(tmp)
                return self.send(open(tmp, 'rb').read(), 'text/csv; charset=utf-8',
                                 extra={'Content-Disposition': 'attachment; filename="translation.csv"'})
            self.send({'error': 'not found'}, code=404)
        except Exception as e:
            self.send({'error': str(e)}, code=500)

    def do_POST(self):
        u = urlparse(self.path)
        try:
            data = self.body()
            if u.path == '/api/import':
                path = os.path.join(tempfile.gettempdir(), 'ps1tl-import.csv')
                open(path, 'wb').write(data)
                updated, unknown = P.import_tl(path)
                return self.send({'updated': updated, 'unknown': unknown})
            j = json.loads(data or b'{}')
            if u.path == '/api/open':
                P.open(j['cue']); return self.send(P.summary())
            if u.path == '/api/line':
                ew, missing, over = P.set_line(int(j['i']), **{k: j[k] for k in ('ja', 'en', 'notes') if k in j})
                return self.send({'ew': ew, 'missing': missing, 'over': over})
            if u.path == '/api/translate':
                P.start_translate(int(j.get('n', 40)), j.get('glossary', ''), j.get('model', 'opus'), j.get('key', ''), bool(j.get('pictures_only')))
                return self.send({'ok': True})
            if u.path == '/api/pictures':
                P.start_read_pictures(j.get('model', 'opus'), j.get('key', '')); return self.send({'ok': True})
            if u.path == '/api/ocr':
                P.start_ocr(j.get('model', 'opus'), j.get('key', '')); return self.send({'ok': True})
            if u.path == '/api/build':
                P.start_build(j.get('out', ''), bool(j.get('labels'))); return self.send({'ok': True})
            if u.path == '/api/stop':
                P.stop = True; return self.send({'ok': True})
            self.send({'error': 'not found'}, code=404)
        except Exception as e:
            self.send({'error': str(e)}, code=500)


def serve(cue=None, port=8732, open_browser=True):
    url = f'http://127.0.0.1:{port}'
    try:
        srv = Server(('127.0.0.1', port), Handler)
    except OSError:   # already running (e.g. launched twice): just show it
        print(f'ps1tl GUI already running on {url}', flush=True)
        if open_browser: webbrowser.open(url)
        return
    if cue: P.open(cue)
    print(f'ps1tl GUI on {url}  (close this window to quit)', flush=True)
    if open_browser: webbrowser.open(url)
    srv.serve_forever()
