"""An open translation project (rom + its .script.json), shared by the web GUI and the desktop app.

Saves are atomic and fsync'd; a rolling backup is kept in backups/ next to the project.
Long tasks (read Japanese, translate, build) run on a background thread; poll .job for progress.
"""
import glob, json, os, re, shutil, threading, time, traceback
from . import games, tfile, build, translate, ocr, llm, flb, images, font as fontmod
from .disc import Disc

BACKUP_EVERY = 600   # seconds
BACKUPS_KEPT = 30


def merge(old, new):
    """add lines/glyphs/refs from a newer extraction to an existing project; translations untouched"""
    from . import ocr
    by_key = {l['key']: l for l in old['lines']}
    for l in new['lines']:
        if l['key'] in by_key: by_key[l['key']]['refs'] = sorted(set(by_key[l['key']]['refs']) | set(l['refs']))
        else: old['lines'].append(l)
    for k, g in new['glyphs'].items(): old['glyphs'].setdefault(k, g)
    images.merge_catalog(old, new.get('images', {}))
    ocr.apply(old)   # new lines made of already-read glyphs get their Japanese straight away
    return old


class Project:
    def __init__(self):
        self.cue = self.script_path = self.script = self.plugin = None
        self.font = fontmod.load()
        self.lock = threading.Lock()
        self.live_lock = threading.Lock()
        self.job = {'name': None, 'log': [], 'result': None}
        self.stop = False
        self.budget = 0
        self.last_backup = 0
        self._pics = None

    # --- files -------------------------------------------------------------
    def open(self, cue):
        """open a rom; extracts its text the first time (creates <rom>.script.json)"""
        disc = Disc(cue)
        plugin = games.for_serial(disc.serial())
        script_path = os.path.splitext(disc.path)[0] + '.script.json'
        fresh = not os.path.exists(script_path)
        script = plugin.extract(disc) if fresh else json.load(open(script_path, encoding='utf-8'))
        stale = script.get('extractor', 0) != getattr(plugin, 'EXTRACTOR', 0)
        if stale:   # the plugin now finds more text: add it, keeping all existing work
            script = merge(script, plugin.extract(disc)) if not fresh else script
            script['extractor'] = getattr(plugin, 'EXTRACTOR', 0)
        with self.lock:
            self.cue, self.script_path, self.script, self.plugin = cue, script_path, script, plugin
            self._pics = None
            # text box width ~= what Japanese lines use; 99th percentile ignores a few odd non-dialogue strings
            w = sorted(translate.line_width(l, script['glyphs']) for l in script['lines'])
            self.budget = w[int(len(w) * 0.99)]
        if fresh or stale:
            if not fresh: self.backup('before-reextract')
            self.save()

    def save(self):
        """atomic + durable write, plus a rolling backup at most every BACKUP_EVERY seconds"""
        with self.lock:
            tmp = self.script_path + '.tmp'
            with open(tmp, 'w', encoding='utf-8') as f:
                json.dump(self.script, f, ensure_ascii=False)
                f.flush(); os.fsync(f.fileno())
            os.replace(tmp, self.script_path)
            if time.time() - self.last_backup > BACKUP_EVERY: self.backup()

    def backup(self, reason='auto'):
        """timestamped copy in backups/ next to the project; keeps the newest BACKUPS_KEPT"""
        if not os.path.exists(self.script_path): return
        d = os.path.join(os.path.dirname(self.script_path), 'backups')
        os.makedirs(d, exist_ok=True)
        name = os.path.basename(self.script_path).replace('.script.json', '')
        shutil.copy2(self.script_path, os.path.join(d, f'{name} {time.strftime("%Y-%m-%d %H%M%S")} {reason}.script.json'))
        self.last_backup = time.time()
        old = sorted(glob.glob(os.path.join(d, f'{glob.escape(name)} *.script.json')), key=os.path.getmtime)
        for p in old[:-BACKUPS_KEPT]: os.remove(p)

    def export_tl(self, path):
        tfile.save(self.script, path)

    def import_tl(self, path):
        self.backup('before-import')
        updated, unknown = tfile.load(self.script, path)
        self.save()
        return updated, unknown

    # --- lines -------------------------------------------------------------
    def summary(self):
        if not self.script: return {'open': False}
        lines = self.script['lines']
        return {'open': True, 'cue': self.cue, 'game': self.script['game'], 'script': self.script_path,
                'total': len(lines), 'translated': self.translated_count(),
                'budget': self.budget, 'labelled': len(self.script.get('chars', {})), 'glyphs': len(self.script['glyphs']),
                'with_ja': sum(1 for l in lines if l.get('ja')), 'job': self.job['name'], 'backend': llm.backend(),
                'pictures': sum(1 for l in lines if images.is_image(l)),
                'pictures_unchecked': sum(1 for v in self.script.get('images', {}).values() if not v.get('checked') and v['h'] <= images.MAX_H),
                'chars': ''.join(sorted(c for c in self.font if c != ' '))}

    def translated_count(self):
        shared = tfile.shared_en(self.script)
        return sum(1 for l in self.script['lines'] if tfile.effective_en(l, shared))

    def en_width(self, text):
        return translate.text_width(text, self.font) if text else 0

    def find_label(self, label):
        """'8/137/18:6' (from a diagnostic build) -> line index, or None"""
        m = re.fullmatch(r'\s*(\d+(?:/\d+)*):(\d+)\s*', label)
        if not m: return None
        ref = f'{flb.long(m.group(1))}:{m.group(2)}'
        return next((i for i, l in enumerate(self.script['lines']) if ref in l['refs']), None)

    def matches(self, flt='all', text=''):
        """indexes of lines passing a filter (all/todo/done/over) and a search string.
        A diagnostic label ('8/137/18:6') as the search jumps straight to that line."""
        hit = self.find_label(text)
        if hit is not None: return [hit]
        text = text.lower()
        shared = tfile.shared_en(self.script)
        out = []
        for i, l in enumerate(self.script['lines']):
            en = tfile.effective_en(l, shared)
            if flt == 'todo' and en: continue
            if flt == 'done' and not en: continue
            if flt == 'over' and not (en and self.widths(l, en)[2]): continue
            if flt == 'pictures' and not images.is_image(l): continue
            if text and text not in l.get('ja', '').lower() and text not in en.lower() and text not in l.get('notes', '').lower()                     and text not in l['key']: continue   # key: picture labels from a diagnostic build
            out.append(i)
        return out

    def rows(self, q):
        idx = self.matches(q.get('filter', 'all'), q.get('q', ''))
        start, n = int(q.get('start', 0)), int(q.get('n', 50))
        shared = tfile.shared_en(self.script)
        out = []
        for i in idx[start:start + n]:
            l = self.script['lines'][i]; en = l.get('en', ''); auto = '' if en else tfile.effective_en(l, shared)
            ew, jw, over = self.widths(l, en or auto)
            out.append({'i': i, 'ja': l.get('ja', ''), 'en': en, 'auto': auto, 'notes': l.get('notes', ''), 'refs': len(l['refs']),
                        'jw': jw, 'ew': ew, 'over': over, 'picture': images.is_image(l)})
        return {'count': len(idx), 'rows': out}

    def widths(self, l, en):
        """-> (english px, japanese px, too long?). Pictures: their box width, and whether the English fits it."""
        if images.is_image(l):
            w = self.script['images'][l['image']]['w']
            return (self.en_width(en), w, bool(en) and not images.draw(*self.picture(l), en)[1])
        ew = self.en_width(en)
        return ew, translate.line_width(l, self.script['glyphs']), ew > self.budget

    def pics(self):
        """picture files of the rom (read once)"""
        if self._pics is None: self._pics = self.plugin.image_files(Disc(self.cue))
        return self._pics

    def picture(self, l):
        return images.pixels(self.pics(), self.script['images'][l['image']], self.plugin)

    def set_line(self, i, **fields):
        """update ja/en/notes of line i and save -> (english width px, chars missing from the font, too long?)"""
        l = self.script['lines'][i]
        for k in ('ja', 'en', 'notes'):
            if k in fields: l[k] = fields[k].strip()
        self.save()
        en = l.get('en', '')
        return self.en_width(en), fontmod.missing(en, self.font), self.widths(l, en)[2]

    def jp_png(self, i):
        l = self.script['lines'][i]
        if images.is_image(l): return images.png(*self.picture(l), scale=3)
        return translate.render([l], self.script['glyphs'], self.plugin.glyph_pixels, numbered=False)

    def en_png(self, text, i=None):
        """English preview; for a picture line, the picture as it will look with this text drawn in"""
        l = self.script['lines'][i] if i is not None else None
        if l and images.is_image(l):
            idx, pal = self.picture(l)
            return images.png(images.draw(idx, pal, text)[0] if text else idx, pal, scale=3)
        return translate.render_en([text], self.font)

    # --- background jobs ---------------------------------------------------
    def run_job(self, name, fn):
        if self.job['name']: raise RuntimeError(f'{self.job["name"]} is already running')
        self.job = {'name': name, 'log': [], 'result': None, 'started': time.time(), 'live': {}}
        self.stop = False
        llm.LIVE = self._live
        def log(msg): self.job['log'].append(str(msg))
        def target():
            try: self.job['result'] = fn(log)
            except Exception as e: log(f'error: {e}'); traceback.print_exc()
            finally: self.job['name'] = None
        threading.Thread(target=target, daemon=True).start()

    def _live(self, label, text):
        """Claude's output as it streams in: job['live'] = {request label: recent text}; gone when it finishes"""
        with self.live_lock:   # not self.lock: saving holds that one for a while
            live = dict(self.job.get('live', {}))
            if text is None: live.pop(label, None)
            else: live[label] = (live.get(label, '') + text)[-4000:]
            self.job['live'] = live   # replaced, never mutated: readers on other threads see a consistent dict

    def live_text(self, per=900):
        """running requests' output for display, newest text of each, one item per line"""
        def num(label):
            try: return int(label.split()[1].split('/')[0])
            except (IndexError, ValueError): return 0
        live = self.job.get('live', {})
        return '\n\n'.join(f'── {k} ──\n' + live[k].replace('}, {', '},\n{')[-per:] for k in sorted(live, key=num))

    def _key(self, key):
        if key: os.environ['ANTHROPIC_API_KEY'] = key

    def start_translate(self, n=40, glossary='', model='opus', key='', pictures_only=False):
        self._key(key)
        self.backup('before-translate')
        def job(log):
            if not pictures_only:
                translate.translate_lines(self.script, self.plugin, n, glossary, log, self.save, lambda: self.stop, model)
            if not self.stop:
                images.translate_images(self.script, self.plugin, self.pics(), n, glossary, log, self.save, lambda: self.stop, model)
        self.run_job('translate', job)

    def start_ocr(self, model='opus', key=''):
        self._key(key)
        self.run_job('ocr', lambda log: ocr.label_glyphs(self.script, self.plugin, log, self.save, lambda: self.stop, model))

    def start_read_pictures(self, model='opus', key=''):
        """find menu/notebook pictures with Japanese text; they become lines"""
        self._key(key)
        self.backup('before-pictures')
        self.run_job('pictures', lambda log: images.read_images(self.script, self.plugin, self.pics(), log, self.save, lambda: self.stop, model))

    def start_build(self, out='', labels=False):
        """labels=True: diagnostic disc where every message shows its 'file:message' label (search it to find the line)"""
        out = out or os.path.join(os.path.dirname(os.path.abspath(self.cue)), 'diagnostic' if labels else 'patched')
        name = os.path.splitext(os.path.basename(self.cue))[0] + (' (labels)' if labels else ' (English)')
        self.run_job('build', lambda log: build.make_patch(self.cue, self.script, self.plugin, out, name=name, log=log, labels=labels))
