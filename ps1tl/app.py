"""Desktop app (Qt): python -m ps1tl app [game.cue]   — builds to a native exe with build_app.bat / build_app.sh"""
import os, sys, time
from PySide6.QtCore import Qt, QAbstractTableModel, QModelIndex, QTimer, QSettings, QSortFilterProxyModel
from PySide6.QtGui import QPixmap, QPalette, QColor, QFont, QKeySequence, QShortcut
from PySide6.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QFormLayout, QSplitter,
                               QTableView, QHeaderView, QLineEdit, QComboBox, QLabel, QPushButton, QProgressBar,
                               QPlainTextEdit, QSpinBox, QFileDialog, QMessageBox, QGroupBox, QScrollArea, QAbstractItemView, QCheckBox)
from .project import Project
from . import tfile

FILTERS = [('All lines', 'all'), ('Untranslated', 'todo'), ('Translated', 'done'), ('Too wide', 'over'), ('Menus & notebook (pictures)', 'pictures')]
MODELS = [('Opus (best quality)', 'opus'), ('Sonnet (faster, uses less)', 'sonnet'), ('Haiku (fastest, rougher)', 'haiku')]


class LinesModel(QAbstractTableModel):
    HEAD = ['#', 'Japanese', 'English', 'Width']

    def __init__(self, project):
        super().__init__()
        self.p, self.idx = project, []

    def reload(self, flt='all', text=''):
        self.beginResetModel()
        self.idx = self.p.matches(flt, text) if self.p.script else []
        self.shared = tfile.shared_en(self.p.script) if self.p.script else {}
        self.endResetModel()

    def auto(self, l):
        return '' if l.get('en') else tfile.effective_en(l, self.shared)

    def rowCount(self, parent=QModelIndex()): return len(self.idx)
    def columnCount(self, parent=QModelIndex()): return 4

    def headerData(self, s, o, role=Qt.DisplayRole):
        if role == Qt.DisplayRole and o == Qt.Horizontal: return self.HEAD[s]

    def data(self, ix, role=Qt.DisplayRole):
        i = self.idx[ix.row()]
        l = self.p.script['lines'][i]
        en = l.get('en', '')
        auto = self.auto(l)
        if role == Qt.DisplayRole:
            if ix.column() == 0: return str(i)
            if ix.column() == 1: return l.get('ja', '') or '(not read yet)'
            if ix.column() == 2: return en or (f'= {auto}' if auto else '')
            if ix.column() == 3: return f'{self.p.en_width(en or auto)}' if en or auto else ''
        if role == Qt.ToolTipRole and ix.column() == 2 and auto:
            return 'Same Japanese as another translated line; this translation is used automatically'
        if role == Qt.ForegroundRole:
            if ix.column() == 1 and not l.get('ja'): return QColor('#7d768a')
            if ix.column() == 2 and auto: return QColor('#8fb3a0')
            if ix.column() == 3 and self.p.en_width(en) > self.p.budget: return QColor('#e46f6f')

    def refresh_row(self, line_index):
        try: r = self.idx.index(line_index)
        except ValueError: return
        self.dataChanged.emit(self.index(r, 0), self.index(r, 3))


def pixmap(png, scale=1):
    pm = QPixmap(); pm.loadFromData(png)
    return pm.scaled(pm.width() * scale, pm.height() * scale) if scale != 1 else pm


class Main(QMainWindow):
    def __init__(self, cue=None):
        super().__init__()
        self.p = Project()
        self.cfg = QSettings('ps1tl', 'ps1tl')
        self.current = None
        self.seen_log = 0
        self.setWindowTitle('ps1tl')
        self.resize(1400, 860)

        # top bar
        top = QWidget(); tl = QHBoxLayout(top); tl.setContentsMargins(8, 6, 8, 6)
        self.open_btn = QPushButton('Open rom…'); self.open_btn.clicked.connect(self.choose_rom)
        self.game_lbl = QLabel('No game open')
        self.prog = QProgressBar(); self.prog.setFixedWidth(220); self.prog.setFormat('%v / %m translated')
        self.ja_lbl = QLabel('')
        for w in (self.open_btn, self.game_lbl): tl.addWidget(w)
        tl.addStretch(1); tl.addWidget(self.ja_lbl); tl.addWidget(self.prog)

        # left: list
        left = QWidget(); ll = QVBoxLayout(left); ll.setContentsMargins(8, 0, 4, 8)
        bar = QHBoxLayout()
        self.filter = QComboBox(); [self.filter.addItem(t, v) for t, v in FILTERS]
        self.search = QLineEdit(); self.search.setPlaceholderText('Search Japanese, English, notes — or paste a label like 8/137/18:6')
        self.count_lbl = QLabel('')
        bar.addWidget(self.filter); bar.addWidget(self.search, 1); bar.addWidget(self.count_lbl)
        self.model = LinesModel(self.p)
        self.table = QTableView(); self.table.setModel(self.model)
        self.table.setSelectionBehavior(QAbstractItemView.SelectRows); self.table.setSelectionMode(QAbstractItemView.SingleSelection)
        self.table.verticalHeader().hide()
        h = self.table.horizontalHeader()
        h.setSectionResizeMode(0, QHeaderView.ResizeToContents); h.setSectionResizeMode(1, QHeaderView.Stretch)
        h.setSectionResizeMode(2, QHeaderView.Stretch); h.setSectionResizeMode(3, QHeaderView.ResizeToContents)
        ll.addLayout(bar); ll.addWidget(self.table, 1)

        # middle: editor
        ed = QWidget(); el = QVBoxLayout(ed); el.setContentsMargins(4, 0, 4, 8)
        self.line_lbl = QLabel('Select a line'); self.line_lbl.setObjectName('h')
        self.jp_img = QLabel(); self.jp_img.setMinimumHeight(48); self.jp_img.setAlignment(Qt.AlignLeft | Qt.AlignVCenter)
        self.ja_edit = QLineEdit(); self.ja_edit.setPlaceholderText('Japanese transcription')
        self.en_edit = QLineEdit(); self.en_edit.setPlaceholderText('English (Enter = save and next line)')
        f = self.en_edit.font(); f.setPointSize(f.pointSize() + 2); self.en_edit.setFont(f)
        self.en_img = QLabel(); self.en_img.setMinimumHeight(48)
        self.meter = QProgressBar(); self.meter.setTextVisible(True)
        self.miss_lbl = QLabel(''); self.miss_lbl.setObjectName('warn')
        self.notes_edit = QLineEdit(); self.notes_edit.setPlaceholderText('notes')
        self.refs_lbl = QLabel(''); self.refs_lbl.setObjectName('dim')
        for w in (self.line_lbl, QLabel('Original'), self.jp_img, self.ja_edit, QLabel('English'), self.en_edit,
                  QLabel('In-game preview'), self.en_img, self.meter, self.miss_lbl, self.notes_edit, self.refs_lbl):
            el.addWidget(w)
        el.addStretch(1)
        for w in (self.ja_edit, self.en_edit, self.notes_edit): w.setEnabled(False)

        # right: tools
        tools = QWidget(); rl = QVBoxLayout(tools); rl.setContentsMargins(4, 0, 8, 8)
        g1 = QGroupBox('Translation file'); g1l = QVBoxLayout(g1)
        b = QPushButton('Save translation file (CSV)…'); b.clicked.connect(self.save_tl); g1l.addWidget(b)
        b = QPushButton('Load translation file…'); b.clicked.connect(self.load_tl); g1l.addWidget(b)
        hint = QLabel('Columns: key, ja, en, notes. Edit in any spreadsheet, load it back.'); hint.setWordWrap(True); hint.setObjectName('dim')
        g1l.addWidget(hint)
        g2 = QGroupBox('AI'); g2l = QFormLayout(g2)
        self.backend_lbl = QLabel(''); self.backend_lbl.setWordWrap(True); self.backend_lbl.setObjectName('dim')
        self.model_box = QComboBox(); [self.model_box.addItem(t, v) for t, v in MODELS]
        self.key_edit = QLineEdit(); self.key_edit.setEchoMode(QLineEdit.Password); self.key_edit.setPlaceholderText('optional; blank = Claude Code login')
        self.gloss = QPlainTextEdit(); self.gloss.setPlaceholderText('Glossary, e.g. ヒラウチ = Hirauchi'); self.gloss.setFixedHeight(70)
        self.n_spin = QSpinBox(); self.n_spin.setRange(1, 100000); self.n_spin.setSingleStep(40); self.n_spin.setValue(40)
        self.tr_btn = QPushButton('Translate next untranslated lines'); self.tr_btn.clicked.connect(self.translate)
        self.ocr_btn = QPushButton('Read Japanese (label all glyphs)'); self.ocr_btn.clicked.connect(self.read_jp)
        self.pic_btn = QPushButton('Find menu text (pictures)'); self.pic_btn.clicked.connect(self.read_pics)
        self.pic_btn.setToolTip('Menus, the notebook and map labels are pictures; this finds the ones with Japanese text and adds them as lines')
        g2l.addRow(self.backend_lbl); g2l.addRow('Model', self.model_box); g2l.addRow('API key', self.key_edit)
        g2l.addRow('Glossary', self.gloss); g2l.addRow('Lines', self.n_spin); g2l.addRow(self.tr_btn); g2l.addRow(self.ocr_btn); g2l.addRow(self.pic_btn)
        g3 = QGroupBox('Build patch'); g3l = QVBoxLayout(g3)
        orow = QHBoxLayout(); self.out_edit = QLineEdit(); self.out_edit.setPlaceholderText('blank = "patched" next to the rom')
        ob = QPushButton('…'); ob.setFixedWidth(30); ob.clicked.connect(self.choose_out); orow.addWidget(self.out_edit); orow.addWidget(ob)
        self.labels_box = QCheckBox('Diagnostic labels (each message shows its file:message id)')
        self.labels_box.setToolTip('Builds a test disc where every message shows where it comes from.\nPaste a label into the search box to jump to that line.')
        g3l.addWidget(self.labels_box)
        self.build_btn = QPushButton('Build patched disc + patch file'); self.build_btn.setObjectName('primary'); self.build_btn.clicked.connect(self.build)
        self.stop_btn = QPushButton('Stop'); self.stop_btn.setEnabled(False); self.stop_btn.clicked.connect(lambda: setattr(self.p, 'stop', True))
        g3l.addLayout(orow); g3l.addWidget(self.build_btn); g3l.addWidget(self.stop_btn)
        self.busy_bar = QProgressBar(); self.busy_bar.setRange(0, 0); self.busy_bar.setTextVisible(False); self.busy_bar.setFixedHeight(6)
        self.busy_lbl = QLabel(); self.busy_lbl.setWordWrap(True)
        self.busy_bar.hide(); self.busy_lbl.hide()
        g3l.addWidget(self.busy_bar); g3l.addWidget(self.busy_lbl)
        self.log = QPlainTextEdit(); self.log.setReadOnly(True); self.log.setFont(QFont('Consolas' if sys.platform == 'win32' else 'Monospace', 9))
        for w in (g1, g2, g3): rl.addWidget(w)
        self.live_lbl = QLabel('Claude output (live)')
        self.live = QPlainTextEdit(); self.live.setReadOnly(True); self.live.setFont(self.log.font()); self.live.setMinimumHeight(180)
        self.live_lbl.hide(); self.live.hide()
        rl.addWidget(self.live_lbl); rl.addWidget(self.live, 1)
        rl.addWidget(QLabel('Log')); rl.addWidget(self.log, 1)
        scroll = QScrollArea(); scroll.setWidget(tools); scroll.setWidgetResizable(True); scroll.setMinimumWidth(320)

        split = QSplitter(); split.addWidget(left); split.addWidget(ed); split.addWidget(scroll)
        split.setSizes([620, 460, 340])
        root = QWidget(); rv = QVBoxLayout(root); rv.setContentsMargins(0, 0, 0, 0); rv.addWidget(top); rv.addWidget(split, 1)
        self.setCentralWidget(root)

        # wiring
        self.filter.currentIndexChanged.connect(self.reload)
        self.search_timer = QTimer(singleShot=True, interval=250, timeout=self.reload)
        self.search.textChanged.connect(lambda: self.search_timer.start())
        self.table.selectionModel().currentRowChanged.connect(lambda cur, prev: self.select(cur.row()))
        self.save_timer = QTimer(singleShot=True, interval=400, timeout=self.commit)
        for w in (self.ja_edit, self.en_edit, self.notes_edit): w.textEdited.connect(lambda _=None: self.save_timer.start())
        self.en_edit.textEdited.connect(self.preview)
        self.en_edit.returnPressed.connect(self.next_line)
        QShortcut(QKeySequence('Ctrl+F'), self, activated=lambda: self.search.setFocus())
        self.job_timer = QTimer(interval=500, timeout=self.poll_job); self.job_timer.start()

        self.gloss.setPlainText(self.cfg.value('glossary', ''))
        self.model_box.setCurrentIndex(max(0, self.model_box.findData(self.cfg.value('model', 'opus'))))
        cue = cue or self.cfg.value('cue', '')
        if cue and os.path.exists(cue): QTimer.singleShot(0, lambda: self.open_rom(cue))
        self.update_stats()

    # --- project -----------------------------------------------------------
    def choose_rom(self):
        path, _ = QFileDialog.getOpenFileName(self, 'Open PS1 rom', os.path.dirname(self.cfg.value('cue', '')), 'Cue sheets (*.cue)')
        if path: self.open_rom(path)

    def open_rom(self, cue):
        self.say(f'opening {cue} …')
        QApplication.setOverrideCursor(Qt.WaitCursor)
        try:
            self.p.open(cue)
        except (SystemExit, Exception) as e:
            QMessageBox.warning(self, 'Could not open', str(e)); self.say(f'error: {e}'); return
        finally:
            QApplication.restoreOverrideCursor()
        self.cfg.setValue('cue', cue)
        self.game_lbl.setText(f'<b>{self.p.script["game"]}</b>')
        self.say(f'opened {self.p.script["game"]}: {len(self.p.script["lines"])} lines')
        self.reload(); self.update_stats()

    def reload(self):
        keep = self.current
        self.model.reload(self.filter.currentData(), self.search.text())
        self.count_lbl.setText(f'{len(self.model.idx):,} lines')
        if self.p.find_label(self.search.text()) is not None and self.model.idx: self.table.selectRow(0)
        elif keep in self.model.idx: self.table.selectRow(self.model.idx.index(keep))

    def update_stats(self):
        s = self.p.summary()
        busy = bool(self.p.job['name'])
        for w in (self.tr_btn, self.ocr_btn, self.pic_btn, self.build_btn): w.setEnabled(s['open'] and not busy)
        self.stop_btn.setEnabled(busy)
        b = s.get('backend') if s['open'] else None
        self.backend_lbl.setText({'api': 'Using: Anthropic API key', 'cli': 'Using: your Claude Code login (no API account needed)'}.get(
            b, 'No Claude access: install Claude Code or enter an API key') if s['open'] else '')
        if not s['open']: return
        self.prog.setMaximum(s['total']); self.prog.setValue(s['translated'])
        self.ja_lbl.setText(f'Japanese read: {s["with_ja"]:,} lines · {s["labelled"]:,}/{s["glyphs"]:,} glyphs')

    def say(self, msg):
        self.log.appendPlainText(str(msg))

    # --- editing -----------------------------------------------------------
    def select(self, row):
        self.commit()
        if row < 0 or row >= len(self.model.idx): return
        i = self.current = self.model.idx[row]
        l = self.p.script['lines'][i]
        self.line_lbl.setText(f'<b>Line {i}</b>')
        self.jp_img.setPixmap(pixmap(self.p.jp_png(i)))
        for w, k in ((self.ja_edit, 'ja'), (self.en_edit, 'en'), (self.notes_edit, 'notes')):
            w.setEnabled(True); w.setText(l.get(k, ''))
        auto = self.model.auto(l)
        self.en_edit.setPlaceholderText(f'= {auto}   (same Japanese as another line; type to override)' if auto else 'English (Enter = save and next line)')
        self.refs_lbl.setText(f'used {len(l["refs"])}× in the game')
        self.preview(); self.en_edit.setFocus()

    def preview(self, *_):
        t = self.en_edit.text() or (self.model.auto(self.p.script['lines'][self.current]) if self.current is not None else '')
        self.en_img.setPixmap(pixmap(self.p.en_png(t, self.current)) if t else QPixmap())
        w = self.p.en_width(t)
        self.meter.setMaximum(max(self.p.budget, 1)); self.meter.setValue(min(w, self.p.budget))
        self.meter.setFormat(f'{w} / {self.p.budget} px' + ('  — too wide' if w > self.p.budget else ''))
        self.meter.setProperty('over', w > self.p.budget); self.meter.style().polish(self.meter)

    def commit(self):
        """save the editor fields into the project if they changed"""
        self.save_timer.stop()
        if self.current is None or not self.p.script: return
        l = self.p.script['lines'][self.current]
        new = {'ja': self.ja_edit.text(), 'en': self.en_edit.text(), 'notes': self.notes_edit.text()}
        if all(new[k].strip() == l.get(k, '') for k in new): return
        try:
            _, missing, _ = self.p.set_line(self.current, **new)
        except OSError as e:
            self.say(f'save failed: {e}'); return
        self.miss_lbl.setText(f'not in font: {" ".join(missing)}' if missing else '')
        self.model.refresh_row(self.current); self.update_stats()

    def next_line(self):
        self.commit()
        r = self.table.currentIndex().row() + 1
        if r < len(self.model.idx): self.table.selectRow(r)

    # --- translation file --------------------------------------------------
    def save_tl(self):
        if not self.p.script: return
        path, _ = QFileDialog.getSaveFileName(self, 'Save translation file', 'translation.csv', 'CSV (*.csv)')
        if path: self.p.export_tl(path); self.say(f'saved {path}')

    def load_tl(self):
        if not self.p.script: return
        path, _ = QFileDialog.getOpenFileName(self, 'Load translation file', '', 'CSV (*.csv)')
        if not path: return
        self.commit()
        updated, unknown = self.p.import_tl(path)
        self.say(f'loaded {os.path.basename(path)}: {updated} lines updated, {unknown} unknown keys')
        self.reload(); self.update_stats()
        if self.current is not None: self.select(self.table.currentIndex().row())

    # --- jobs --------------------------------------------------------------
    def start(self, fn):
        self.commit()
        try: fn()
        except RuntimeError as e: self.say(f'error: {e}'); return
        self.seen_log = 0; self.update_stats()

    def translate(self):
        self.cfg.setValue('glossary', self.gloss.toPlainText()); self.cfg.setValue('model', self.model_box.currentData())
        self.start(lambda: self.p.start_translate(self.n_spin.value(), self.gloss.toPlainText(), self.model_box.currentData(), self.key_edit.text().strip(),
                                                      self.filter.currentData() == 'pictures'))   # menus filter: picture text only

    def read_jp(self):
        self.cfg.setValue('model', self.model_box.currentData())
        self.start(lambda: self.p.start_ocr(self.model_box.currentData(), self.key_edit.text().strip()))

    def read_pics(self):
        self.cfg.setValue('model', self.model_box.currentData())
        self.start(lambda: self.p.start_read_pictures(self.model_box.currentData(), self.key_edit.text().strip()))

    def choose_out(self):
        d = QFileDialog.getExistingDirectory(self, 'Output folder')
        if d: self.out_edit.setText(d)

    def build(self):
        self.start(lambda: self.p.start_build(self.out_edit.text().strip(), self.labels_box.isChecked()))

    def poll_job(self):
        j = self.p.job
        busy = bool(j['name'])
        self.busy_bar.setVisible(busy); self.busy_lbl.setVisible(busy)
        if busy:   # moving bar + clock, so a long Claude request never looks frozen
            t = int(time.time() - j.get('started', time.time()))
            self.busy_lbl.setText(f'{j["name"].capitalize()} running · {t // 60}:{t % 60:02d}' + (' · stopping after current requests' if self.p.stop else ''))
            text = self.p.live_text() or 'Waiting for Claude to start writing…'
            if text != self.live.toPlainText():
                sb = self.live.verticalScrollBar(); at_end = sb.value() >= sb.maximum() - 4
                self.live.setPlainText(text)
                if at_end: sb.setValue(sb.maximum())
        self.live_lbl.setVisible(busy); self.live.setVisible(busy)
        for m in j['log'][self.seen_log:]: self.say(m)
        changed = len(j['log']) > self.seen_log
        self.seen_log = len(j['log'])
        if changed and j['name'] in ('translate', 'ocr', 'pictures'):
            self.model.layoutChanged.emit(); self.update_stats()
        if not j['name'] and self.stop_btn.isEnabled():   # just finished
            r = j['result'] or {}
            if r.get('error'): self.say('error: ' + r['error'])
            if r.get('patch'): self.say(f'patch ready:\n  {r["cue"]}\n  {r["patch"]}')
            if r.get('files_skipped'):
                reasons = sorted(set(r['files_skipped'].values()))[:3]
                self.say(f'{len(r["files_skipped"])} scene files kept in Japanese: ' + '; '.join(reasons))
            self.reload(); self.update_stats()
            if self.current is not None and self.table.currentIndex().row() >= 0: self.select(self.table.currentIndex().row())

    def closeEvent(self, e):
        self.commit()
        super().closeEvent(e)


STYLE = """
* { font-size: 10pt; }
QLabel#h { font-size: 12pt; color: #e8a35c; }
QLabel#dim { color: #9a93a6; }
QLabel#warn { color: #e46f6f; }
QGroupBox { border: 1px solid #2d2936; border-radius: 6px; margin-top: 10px; padding-top: 8px; }
QGroupBox::title { subcontrol-origin: margin; left: 8px; color: #9a93a6; }
QPushButton { padding: 6px 10px; border: 1px solid #2d2936; border-radius: 5px; background: #2a2533; }
QPushButton:hover { border-color: #e8a35c; }
QPushButton:disabled { color: #6b6577; }
QPushButton#primary { background: #e8a35c; color: #1a1208; font-weight: bold; border-color: #e8a35c; }
QPushButton#primary:disabled { background: #5a4630; color: #2a2016; }
QLineEdit, QPlainTextEdit, QComboBox, QSpinBox { padding: 4px 6px; border: 1px solid #2d2936; border-radius: 5px; background: #121017; }
QLineEdit:focus, QPlainTextEdit:focus { border-color: #e8a35c; }
QTableView { background: #121017; alternate-background-color: #17141d; gridline-color: #2d2936; selection-background-color: #4a3a28; }
QHeaderView::section { background: #1e1b25; color: #9a93a6; border: 0; border-bottom: 1px solid #2d2936; padding: 4px; }
QProgressBar { border: 1px solid #2d2936; border-radius: 4px; background: #121017; text-align: center; height: 16px; }
QProgressBar::chunk { background: #7fc8a0; border-radius: 3px; }
QProgressBar[over="true"]::chunk { background: #e46f6f; }
"""


def dark(app):
    app.setStyle('Fusion')
    pal = QPalette()
    for role, c in ((QPalette.Window, '#15131a'), (QPalette.WindowText, '#e9e4ef'), (QPalette.Base, '#121017'),
                    (QPalette.AlternateBase, '#17141d'), (QPalette.Text, '#e9e4ef'), (QPalette.Button, '#2a2533'),
                    (QPalette.ButtonText, '#e9e4ef'), (QPalette.Highlight, '#e8a35c'), (QPalette.HighlightedText, '#1a1208'),
                    (QPalette.PlaceholderText, '#7d768a'), (QPalette.ToolTipBase, '#1e1b25'), (QPalette.ToolTipText, '#e9e4ef')):
        pal.setColor(role, QColor(c))
    app.setPalette(pal); app.setStyleSheet(STYLE)


def main(argv=None):
    argv = sys.argv if argv is None else argv
    app = QApplication(argv)
    app.setApplicationName('ps1tl')
    dark(app)
    w = Main(argv[1] if len(argv) > 1 else None)
    w.table.setAlternatingRowColors(True)
    w.show()
    return app.exec()


if __name__ == '__main__':
    sys.exit(main())
