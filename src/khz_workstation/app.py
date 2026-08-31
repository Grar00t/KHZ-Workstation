from __future__ import annotations

import getpass
import os
import subprocess
import sys
import time
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk

from .ai.policy import AIPolicy
from .backup import BackupService
from .data_service import DataWorkspaceService
from .fileops import FileService, OFFICE_KIND
from .gittools import GitService
from .i18n import Localizer
from .office import OfficeRegistry
from .search import LocalSearch
from .settings import AppSettings, SettingsStore
from .security.session import SessionLockService
from .terminal import TerminalService
from .workspace import Workspace
from .workflows import document_outline_to_slides, document_table_to_sheet, sheet_range_to_document_table


APP_TITLE = "KHZ Workstation"
SURFACES = [
    "Home", "Files", "Documents", "Sheets", "Slides", "PDF", "Data", "Search", "Activity",
    "Repositories", "Terminal", "Tasks", "Assistant", "Settings",
]


class KHZApp(tk.Tk):
    def __init__(self, initial_workspace: Path | None = None) -> None:
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("1280x820")
        self.minsize(960, 620)
        self.settings_store = SettingsStore()
        self.settings = self.settings_store.load()
        self.workspace: Workspace | None = None
        self.office = OfficeRegistry()
        self.localizer = Localizer(self.settings.locale)
        self.ai_policy = AIPolicy(enabled=self.settings.ai_enabled)
        self.session_lock = SessionLockService()
        self._last_activity = time.monotonic()
        self.status_var = tk.StringVar(value="Ready")
        self.surface_var = tk.StringVar(value="Home")
        self._configure_style()
        self._build_shell()
        self.bind_all("<Control-k>", self.open_command_palette)
        self.bind_all("<Control-K>", self.open_command_palette)
        self.bind_all("<KeyPress>", self._mark_activity, add="+")
        self.bind_all("<Button>", self._mark_activity, add="+")
        self.bind_all("<Motion>", self._mark_activity, add="+")
        self.after(30_000, self._session_watchdog)
        if initial_workspace:
            try:
                self.open_workspace(initial_workspace, create_if_missing=False)
            except Exception as exc:
                self.status_var.set(f"Workspace open failed: {exc}")
        self.show_surface("Home")

    def _mark_activity(self, _event=None) -> None:
        self._last_activity = time.monotonic()

    def _session_watchdog(self) -> None:
        try:
            if self.settings.healthcare_hardened and self.settings.session_timeout_minutes > 0:
                timeout = self.settings.session_timeout_minutes * 60
                if time.monotonic() - self._last_activity >= timeout:
                    locked = self.session_lock.lock_now()
                    self._last_activity = time.monotonic()
                    self.status_var.set("Windows session lock requested" if locked else "Session timeout reached; OS lock is unavailable on this platform")
        finally:
            self.after(30_000, self._session_watchdog)

    def lock_workstation(self) -> None:
        if self.session_lock.lock_now():
            self.status_var.set("Windows session lock requested")
        else:
            messagebox.showinfo("Lock Workstation", "OS workstation locking is available on Windows. No weaker KHZ password fallback is implemented.")

    def _configure_style(self) -> None:
        style = ttk.Style(self)
        if "vista" in style.theme_names():
            style.theme_use("vista")
        elif "clam" in style.theme_names():
            style.theme_use("clam")
        style.configure("Nav.TButton", anchor="w", padding=(12, 7))
        style.configure("Header.TLabel", font=("Segoe UI", 15, "bold"))
        style.configure("Sub.TLabel", font=("Segoe UI", 9))
        style.configure("Dense.Treeview", rowheight=24, font=("Segoe UI", 9))
        style.configure("Dense.Treeview.Heading", font=("Segoe UI", 9, "bold"))

    def _build_shell(self) -> None:
        self.columnconfigure(1, weight=1)
        self.rowconfigure(1, weight=1)

        top = ttk.Frame(self, padding=(10, 7))
        top.grid(row=0, column=0, columnspan=2, sticky="ew")
        top.columnconfigure(2, weight=1)
        ttk.Label(top, text="KHZ WORKSTATION", font=("Segoe UI", 11, "bold")).grid(row=0, column=0, sticky="w")
        ttk.Separator(top, orient="vertical").grid(row=0, column=1, sticky="ns", padx=10)
        self.workspace_label = ttk.Label(top, text="No workspace")
        self.workspace_label.grid(row=0, column=2, sticky="w")
        ttk.Button(top, text="Open Workspace", command=self.choose_workspace).grid(row=0, column=3, padx=4)
        ttk.Button(top, text="Ctrl+K", command=self.open_command_palette, width=8).grid(row=0, column=4, padx=4)

        nav = ttk.Frame(self, padding=(6, 8))
        nav.grid(row=1, column=0, sticky="nsw")
        nav_row = 0
        for name in SURFACES:
            if name == "Assistant":
                ttk.Separator(nav).grid(row=nav_row, column=0, sticky="ew", pady=(7, 5))
                nav_row += 1
            button = ttk.Button(nav, text=name, style="Nav.TButton", width=22, command=lambda n=name: self.show_surface(n))
            button.grid(row=nav_row, column=0, sticky="ew", pady=1)
            nav_row += 1

        self.content = ttk.Frame(self, padding=(14, 10))
        self.content.grid(row=1, column=1, sticky="nsew")
        self.content.columnconfigure(0, weight=1)
        self.content.rowconfigure(1, weight=1)

        status = ttk.Frame(self, padding=(8, 4))
        status.grid(row=2, column=0, columnspan=2, sticky="ew")
        status.columnconfigure(0, weight=1)
        ttk.Label(status, textvariable=self.status_var).grid(row=0, column=0, sticky="w")
        self.policy_label = ttk.Label(status, text=self._policy_text())
        self.policy_label.grid(row=0, column=1, sticky="e")

    def _policy_text(self) -> str:
        ai = "AI OFF" if not self.settings.ai_enabled else "AI ON"
        return f"{self.settings.profile} | {ai} | Network {self.settings.network_mode}"

    def _clear_content(self) -> None:
        for child in self.content.winfo_children():
            child.destroy()

    def _header(self, title: str, subtitle: str = "") -> ttk.Frame:
        header = ttk.Frame(self.content)
        header.grid(row=0, column=0, sticky="ew", pady=(0, 10))
        header.columnconfigure(0, weight=1)
        ttk.Label(header, text=title, style="Header.TLabel").grid(row=0, column=0, sticky="w")
        if subtitle:
            ttk.Label(header, text=subtitle, style="Sub.TLabel").grid(row=1, column=0, sticky="w", pady=(2, 0))
        return header

    def choose_workspace(self) -> None:
        directory = filedialog.askdirectory(title="Open or create KHZ Workspace")
        if directory:
            root = Path(directory)
            create = not (root / ".khz" / "workspace.json").exists()
            if create and not messagebox.askyesno("Create Workspace", f"Create a KHZ workspace in:\n{root}?"):
                return
            self.open_workspace(root, create_if_missing=create)

    def open_workspace(self, root: Path, create_if_missing: bool) -> None:
        self.workspace = Workspace.create(root) if create_if_missing else Workspace.open(root)
        self.workspace_label.config(text=f"{self.workspace.info.name}  |  {self.workspace.root}")
        FileService(self.workspace).scan()
        self.status_var.set(f"Workspace ready: {self.workspace.info.name}")
        self.show_surface(self.surface_var.get())

    def require_workspace(self) -> Workspace | None:
        if not self.workspace:
            messagebox.showinfo("Workspace Required", "Open or create a workspace first.")
            return None
        return self.workspace

    def show_surface(self, name: str) -> None:
        self.surface_var.set(name)
        self._clear_content()
        handler = getattr(self, f"surface_{name.lower().replace(' ', '_')}", None)
        if handler:
            handler()
        else:
            self._header(name)
            ttk.Label(self.content, text="This surface is not implemented.").grid(row=1, column=0, sticky="nw")

    def surface_home(self) -> None:
        self._header("Home", "Local-first work. AI is optional and does not own the workspace.")
        body = ttk.Frame(self.content)
        body.grid(row=1, column=0, sticky="nsew")
        body.columnconfigure(0, weight=1)
        row = 0
        if self.workspace:
            ttk.Label(body, text=self.workspace.info.name, font=("Segoe UI", 12, "bold")).grid(row=row, column=0, sticky="w")
            row += 1
            ttk.Label(body, text=str(self.workspace.root)).grid(row=row, column=0, sticky="w", pady=(2, 10))
            row += 1
            actions = ttk.Frame(body)
            actions.grid(row=row, column=0, sticky="w")
            ttk.Button(actions, text="Files", command=lambda: self.show_surface("Files")).pack(side="left", padx=(0, 5))
            ttk.Button(actions, text="New Document", command=lambda: self.new_office_file("document")).pack(side="left", padx=5)
            ttk.Button(actions, text="New Sheet", command=lambda: self.new_office_file("sheet")).pack(side="left", padx=5)
            ttk.Button(actions, text="New Presentation", command=lambda: self.new_office_file("slides")).pack(side="left", padx=5)
            ttk.Button(actions, text="Search", command=lambda: self.show_surface("Search")).pack(side="left", padx=5)
            ttk.Button(actions, text="Create Backup", command=self.create_backup_ui).pack(side="left", padx=5)
            row += 1
        else:
            ttk.Label(body, text="Open an existing folder or create a KHZ workspace without account creation.").grid(row=row, column=0, sticky="w")
            row += 1
            ttk.Button(body, text="Open Workspace", command=self.choose_workspace).grid(row=row, column=0, sticky="w", pady=8)
            row += 1
        engine = self.office.selected()
        info = engine.info() if engine else None
        ttk.Separator(body).grid(row=row, column=0, sticky="ew", pady=16)
        row += 1
        ttk.Label(body, text="Runtime status", font=("Segoe UI", 10, "bold")).grid(row=row, column=0, sticky="w")
        row += 1
        status_lines = [
            f"AI: {'OFF' if not self.settings.ai_enabled else 'ON'}",
            f"Healthcare Hardened: {'ON' if self.settings.healthcare_hardened else 'OFF'}",
            f"Office engine: {info.engine + ' | ' + (info.version or 'version unknown') if info else 'NOT DETECTED'}",
            "Office integration: external mature editor + deterministic adapter; not a KHZ-written clone",
        ]
        ttk.Label(body, text="\n".join(status_lines), justify="left").grid(row=row, column=0, sticky="w", pady=(4, 0))

    def surface_files(self) -> None:
        ws = self.require_workspace()
        self._header("Files", "Filesystem-backed workspace items. Rename, copy, move, trash, reveal, and properties are deterministic local operations.")
        if not ws:
            return
        toolbar = ttk.Frame(self.content)
        toolbar.grid(row=1, column=0, sticky="ew", pady=(0, 6))
        tree = ttk.Treeview(self.content, columns=("type", "size"), show="tree headings", style="Dense.Treeview")
        tree.heading("#0", text="Name")
        tree.heading("type", text="Type")
        tree.heading("size", text="Size")
        tree.column("#0", width=560)
        tree.column("type", width=120)
        tree.column("size", width=100, anchor="e")
        tree.grid(row=2, column=0, sticky="nsew")
        self.content.rowconfigure(2, weight=1)

        def populate() -> None:
            tree.delete(*tree.get_children())

            def add_dir(parent_iid: str, directory: Path) -> None:
                for p in sorted(directory.iterdir(), key=lambda x: (not x.is_dir(), x.name.casefold())):
                    if p.name == ws.META_DIR:
                        continue
                    kind = "Folder" if p.is_dir() else OFFICE_KIND.get(p.suffix.lower(), "File")
                    size = "" if p.is_dir() else self._format_size(p.stat().st_size)
                    iid = str(p)
                    tree.insert(parent_iid, "end", iid=iid, text=p.name, values=(kind, size), open=False)
                    if p.is_dir():
                        try:
                            add_dir(iid, p)
                        except OSError:
                            pass

            add_dir("", ws.root)

        def selected_path() -> Path | None:
            sel = tree.selection()
            return Path(sel[0]) if sel else None

        def open_selected() -> None:
            p = selected_path()
            if not p:
                return
            if p.is_dir():
                tree.item(str(p), open=not bool(tree.item(str(p), "open")))
            else:
                self._open_file(p)

        def delete_selected() -> None:
            p = selected_path()
            if not p:
                return
            rel = str(p.relative_to(ws.root))
            noun = "folder" if p.is_dir() else "file"
            if messagebox.askyesno("Safe Delete", f"Move {noun} {rel} to workspace trash?"):
                FileService(ws).safe_delete(rel)
                populate()

        def new_folder() -> None:
            name = simpledialog.askstring("New Folder", "Workspace-relative folder path:", initialvalue="New Folder")
            if not name:
                return
            try:
                path = ws.paths.resolve(name)
                path.mkdir(parents=True, exist_ok=False)
                ws.audit.append(who=getpass.getuser(), what="folder.created", target=str(path.relative_to(ws.root)), approval="USER", result="CREATED")
                populate()
            except Exception as exc:
                messagebox.showerror("New Folder", str(exc))

        def rename_selected() -> None:
            p = selected_path()
            if not p:
                return
            old_rel = str(p.relative_to(ws.root))
            new_name = simpledialog.askstring("Rename", "New name:", initialvalue=p.name)
            if not new_name or new_name == p.name:
                return
            try:
                new_rel = str(Path(old_rel).with_name(new_name))
                FileService(ws).move(old_rel, new_rel)
                populate()
            except Exception as exc:
                messagebox.showerror("Rename", str(exc))

        def move_selected() -> None:
            p = selected_path()
            if not p:
                return
            old_rel = str(p.relative_to(ws.root))
            new_rel = simpledialog.askstring("Move", "Destination workspace-relative path:", initialvalue=old_rel)
            if not new_rel or new_rel == old_rel:
                return
            try:
                FileService(ws).move(old_rel, new_rel)
                populate()
            except Exception as exc:
                messagebox.showerror("Move", str(exc))

        def copy_selected() -> None:
            p = selected_path()
            if not p:
                return
            source_rel = str(p.relative_to(ws.root))
            default = str(Path(source_rel).with_name(p.stem + " - Copy" + p.suffix)) if p.is_file() else str(Path(source_rel).with_name(p.name + " - Copy"))
            dest_rel = simpledialog.askstring("Copy", "Destination workspace-relative path:", initialvalue=default)
            if not dest_rel:
                return
            try:
                FileService(ws).copy(source_rel, dest_rel)
                populate()
            except Exception as exc:
                messagebox.showerror("Copy", str(exc))

        def properties() -> None:
            p = selected_path()
            if not p:
                return
            stat = p.stat()
            lines = [
                f"Path: {p}",
                f"Type: {'Folder' if p.is_dir() else OFFICE_KIND.get(p.suffix.lower(), 'File')}",
                f"Modified: {time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(stat.st_mtime))}",
            ]
            if p.is_file():
                from .fileops import sha256_file
                lines.extend([f"Size: {stat.st_size} bytes", f"SHA-256: {sha256_file(p)}"])
            messagebox.showinfo("Properties", "\n".join(lines))

        ttk.Button(toolbar, text="Refresh", command=populate).pack(side="left", padx=(0, 3))
        ttk.Button(toolbar, text="Open", command=open_selected).pack(side="left", padx=3)
        ttk.Button(toolbar, text="Open With", command=lambda: self._open_with(selected_path())).pack(side="left", padx=3)
        ttk.Button(toolbar, text="New Folder", command=new_folder).pack(side="left", padx=3)
        ttk.Button(toolbar, text="Rename", command=rename_selected).pack(side="left", padx=3)
        ttk.Button(toolbar, text="Copy", command=copy_selected).pack(side="left", padx=3)
        ttk.Button(toolbar, text="Move", command=move_selected).pack(side="left", padx=3)
        ttk.Button(toolbar, text="Safe Delete", command=delete_selected).pack(side="left", padx=3)
        ttk.Button(toolbar, text="Versions", command=lambda: self.show_versions_ui(selected_path())).pack(side="left", padx=3)
        ttk.Button(toolbar, text="Properties", command=properties).pack(side="left", padx=3)
        ttk.Button(toolbar, text="Reveal", command=lambda: self._reveal(selected_path())).pack(side="left", padx=3)
        tree.bind("<Double-1>", lambda _e: open_selected())
        populate()

    def _office_surface(self, title: str, kind: str, extensions: set[str]) -> None:
        ws = self.require_workspace()
        engine = self.office.selected()
        engine_text = engine.info().engine + " | " + (engine.info().version or "version unknown") if engine else "No supported Office engine detected"
        self._header(title, f"Engine: {engine_text}. Editing is delegated to a mature local Office process.")
        if not ws:
            return
        top = ttk.Frame(self.content)
        top.grid(row=1, column=0, sticky="ew", pady=(0, 6))
        listbox = tk.Listbox(self.content, activestyle="dotbox", font=("Segoe UI", 10))
        listbox.grid(row=2, column=0, sticky="nsew")
        self.content.rowconfigure(2, weight=1)
        files = [p for p in ws.root.rglob("*") if p.is_file() and p.suffix.lower() in extensions and ws.META_DIR not in p.parts]
        for p in sorted(files):
            listbox.insert("end", str(p.relative_to(ws.root)))

        def get_selected() -> Path | None:
            sel = listbox.curselection()
            return ws.root / listbox.get(sel[0]) if sel else None

        def open_edit() -> None:
            p = get_selected()
            if not p:
                return
            try:
                FileService(ws).snapshot(str(p.relative_to(ws.root)))
                self.office.open_registered_or_system(p)
                ws.audit.append(who=getpass.getuser(), what="office.open_for_edit", target=str(p.relative_to(ws.root)), approval="USER", execution=engine.info().engine if engine else "OS", result="PROCESS_STARTED")
                self.status_var.set(f"Opened in local editor: {p.name}")
            except Exception as exc:
                messagebox.showerror("Office Engine", str(exc))

        def export_pdf() -> None:
            p = get_selected()
            if not p or not engine:
                return
            out = filedialog.askdirectory(title="Export PDF to folder")
            if not out:
                return
            try:
                pdf = engine.convert_to_pdf(p, Path(out))
                ws.audit.append(who=getpass.getuser(), what="office.export_pdf", target=str(p.relative_to(ws.root)), approval="USER", execution=engine.info().engine, result=str(pdf), verification="output file exists")
                self.status_var.set(f"PDF exported: {pdf}")
            except Exception as exc:
                messagebox.showerror("Export PDF", str(exc))

        if kind in {"document", "sheet", "slides"}:
            ttk.Button(top, text="New", command=lambda: self.new_office_file(kind)).pack(side="left", padx=(0, 4))
        ttk.Button(top, text="Open / Edit", command=open_edit).pack(side="left", padx=4)
        if kind != "pdf":
            ttk.Button(top, text="Export PDF", command=export_pdf).pack(side="left", padx=4)
        ttk.Label(top, text="KHZ preserves a pre-edit snapshot before launching the editor.").pack(side="left", padx=12)
        listbox.bind("<Double-1>", lambda _e: open_edit())

    def surface_documents(self) -> None:
        self._office_surface("Documents", "document", {".docx", ".odt", ".rtf", ".txt"})

    def surface_sheets(self) -> None:
        self._office_surface("Sheets", "sheet", {".xlsx", ".xlsm", ".ods", ".csv"})

    def surface_slides(self) -> None:
        self._office_surface("Slides", "slides", {".pptx", ".odp"})

    def surface_pdf(self) -> None:
        self._office_surface("PDF", "pdf", {".pdf"})

    def surface_data(self) -> None:
        ws = self.require_workspace()
        self._header("Data", "Typed local SQLite tables, filters, and deterministic CSV/XLSX import/export. Separate from spreadsheets.")
        if not ws:
            return
        top = ttk.Frame(self.content)
        top.grid(row=1, column=0, sticky="ew", pady=(0, 6))
        tables = ttk.Combobox(top, state="readonly", width=28)
        tables.pack(side="left", padx=(0, 5))
        filter_entry = ttk.Entry(top, width=24)
        filter_entry.insert(0, "column=value")
        filter_entry.pack(side="left", padx=4)
        sort_entry = ttk.Entry(top, width=18)
        sort_entry.insert(0, "sort column")
        sort_entry.pack(side="left", padx=4)
        grid = ttk.Treeview(self.content, show="headings", style="Dense.Treeview")
        grid.grid(row=2, column=0, sticky="nsew")
        self.content.rowconfigure(2, weight=1)
        mapping: dict[str, str] = {}

        def refresh_tables() -> None:
            rows = ws.store.list_data_tables()
            mapping.clear()
            for row in rows:
                mapping[row["name"]] = row["table_id"]
            tables["values"] = list(mapping)
            if mapping and not tables.get():
                tables.current(0)
            load_table()

        def parsed_filter() -> dict[str, object]:
            raw = filter_entry.get().strip()
            if not raw or raw == "column=value":
                return {}
            if "=" not in raw:
                raise ValueError("Filter must be column=value.")
            key, value = raw.split("=", 1)
            return {key.strip(): value.strip()}

        def load_table(_event=None) -> None:
            name = tables.get()
            if name not in mapping:
                grid.delete(*grid.get_children())
                return
            try:
                sort_by = sort_entry.get().strip()
                if sort_by == "sort column":
                    sort_by = ""
                cols, rows = ws.store.query_data(mapping[name], filters=parsed_filter(), sort_by=sort_by or None)
            except Exception as exc:
                messagebox.showerror("Data Query", str(exc)); return
            grid.delete(*grid.get_children())
            grid["columns"] = cols
            for c in cols:
                grid.heading(c, text=c)
                grid.column(c, width=140)
            for row in rows:
                grid.insert("", "end", values=[row[c] for c in cols])
            self.status_var.set(f"Data rows: {len(rows)}")

        def new_table() -> None:
            name = simpledialog.askstring("New Data Table", "Table name (letters, numbers, underscore):")
            if not name:
                return
            spec = simpledialog.askstring("Columns", "Columns as name:TYPE, e.g. Name:TEXT,Amount:REAL,Year:INTEGER")
            if not spec:
                return
            try:
                cols = []
                for part in spec.split(","):
                    col, typ = part.strip().split(":", 1)
                    cols.append((col.strip(), typ.strip().upper()))
                ws.store.create_data_table(name.strip(), cols)
                ws.audit.append(who=getpass.getuser(), what="data.table_created", target=name.strip(), approval="USER", result="CREATED")
                refresh_tables()
            except Exception as exc:
                messagebox.showerror("Data Table", str(exc))

        def add_row() -> None:
            name = tables.get()
            if name not in mapping:
                return
            table_id = mapping[name]
            meta = next(x for x in ws.store.list_data_tables() if x["table_id"] == table_id)
            import json
            schema = json.loads(meta["schema_json"])
            values = {}
            for col, typ in schema:
                raw = simpledialog.askstring("Add Row", f"{col} ({typ}) - blank for NULL:")
                if raw in (None, ""):
                    continue
                if typ == "INTEGER":
                    values[col] = int(raw)
                elif typ == "REAL":
                    values[col] = float(raw)
                else:
                    values[col] = raw
            ws.store.add_data_row(table_id, values)
            ws.audit.append(who=getpass.getuser(), what="data.row_added", target=name, approval="USER", result="INSERTED")
            load_table()

        def import_data(kind: str) -> None:
            patterns = [("CSV", "*.csv")] if kind == "csv" else [("Excel Workbook", "*.xlsx")]
            source = filedialog.askopenfilename(title=f"Import {kind.upper()} into Data", filetypes=patterns)
            if not source:
                return
            name = simpledialog.askstring("Import Data", "Destination table name:", initialvalue=Path(source).stem.replace(" ", "_"))
            if not name:
                return
            try:
                service = DataWorkspaceService(ws)
                if kind == "csv":
                    service.import_csv(Path(source), name)
                else:
                    service.import_xlsx(Path(source), name)
                refresh_tables()
                self.status_var.set(f"Imported {Path(source).name} into Data")
            except Exception as exc:
                messagebox.showerror("Import Data", str(exc))

        def export_data(kind: str) -> None:
            name = tables.get()
            if name not in mapping:
                return
            ext = ".csv" if kind == "csv" else ".xlsx"
            dest = filedialog.asksaveasfilename(title=f"Export Data to {kind.upper()}", defaultextension=ext, initialfile=name + ext)
            if not dest:
                return
            try:
                service = DataWorkspaceService(ws)
                if kind == "csv":
                    service.export_csv(mapping[name], Path(dest))
                else:
                    service.export_xlsx(mapping[name], Path(dest))
                self.status_var.set(f"Exported Data table: {dest}")
            except Exception as exc:
                messagebox.showerror("Export Data", str(exc))

        ttk.Button(top, text="New Table", command=new_table).pack(side="left", padx=3)
        ttk.Button(top, text="Add Row", command=add_row).pack(side="left", padx=3)
        ttk.Button(top, text="Apply Filter/Sort", command=load_table).pack(side="left", padx=3)
        ttk.Button(top, text="Import CSV", command=lambda: import_data("csv")).pack(side="left", padx=3)
        ttk.Button(top, text="Import XLSX", command=lambda: import_data("xlsx")).pack(side="left", padx=3)
        ttk.Button(top, text="Export CSV", command=lambda: export_data("csv")).pack(side="left", padx=3)
        ttk.Button(top, text="Export XLSX", command=lambda: export_data("xlsx")).pack(side="left", padx=3)
        tables.bind("<<ComboboxSelected>>", load_table)
        refresh_tables()

    def surface_search(self) -> None:
        ws = self.require_workspace()
        self._header("Search", "Local, workspace-scoped search. No embeddings are required.")
        if not ws:
            return
        top = ttk.Frame(self.content)
        top.grid(row=1, column=0, sticky="ew", pady=(0, 6))
        top.columnconfigure(0, weight=1)
        query = ttk.Entry(top)
        query.grid(row=0, column=0, sticky="ew")
        results = ttk.Treeview(self.content, columns=("reason",), show="tree headings", style="Dense.Treeview")
        results.heading("#0", text="Path")
        results.heading("reason", text="Matched")
        results.grid(row=2, column=0, sticky="nsew")
        self.content.rowconfigure(2, weight=1)

        def run_search(_event=None) -> None:
            results.delete(*results.get_children())
            service = LocalSearch(ws, content_enabled=self.settings.content_indexing_enabled)
            for result in service.query(query.get()):
                results.insert("", "end", text=result.relative_path, values=(result.reason,))
        ttk.Button(top, text="Search", command=run_search).grid(row=0, column=1, padx=(6, 0))
        query.bind("<Return>", run_search)
        query.focus_set()

    def surface_activity(self) -> None:
        ws = self.require_workspace()
        self._header("Activity", "Append-oriented audit metadata. Hash chaining is an integrity signal, not proof of event truth.")
        if not ws:
            return
        rows = ws.audit.read(limit=300)
        tree = ttk.Treeview(self.content, columns=("when", "what", "target", "result"), show="headings", style="Dense.Treeview")
        for col, width in (("when", 210), ("what", 180), ("target", 380), ("result", 220)):
            tree.heading(col, text=col.upper())
            tree.column(col, width=width)
        tree.grid(row=1, column=0, sticky="nsew")
        for row in reversed(rows):
            tree.insert("", "end", values=(row.get("when"), row.get("what"), row.get("target"), row.get("result")))
        ok, detail = ws.audit.verify_chain()
        self.status_var.set(f"Audit chain: {'VERIFIED' if ok else 'FAILED'} | {detail}")

    def surface_repositories(self) -> None:
        ws = self.require_workspace()
        self._header("Repositories", "Local Git inspection is read-only by default; opening a repository does not use the network.")
        if not ws:
            return
        output = tk.Text(self.content, wrap="none", font=("Consolas", 9))
        output.grid(row=2, column=0, sticky="nsew")
        self.content.rowconfigure(2, weight=1)
        top = ttk.Frame(self.content)
        top.grid(row=1, column=0, sticky="ew", pady=(0, 6))
        git = GitService(ws.root)

        def show(name: str, result) -> None:
            output.delete("1.0", "end")
            output.insert("end", f"[{name}] exit={result.exit_code}\n\n{result.stdout}{result.stderr}")

        ttk.Button(top, text="Status", command=lambda: show("status", git.status())).pack(side="left", padx=(0, 4))
        ttk.Button(top, text="Diff", command=lambda: show("diff", git.diff())).pack(side="left", padx=4)
        ttk.Button(top, text="History", command=lambda: show("history", git.history())).pack(side="left", padx=4)
        if not git.is_repository():
            output.insert("end", "Workspace root is not a Git repository. No network activity was attempted.\n")

    def surface_terminal(self) -> None:
        ws = self.require_workspace()
        self._header("Terminal", "Explicit local execution. Commands are never accepted directly from model text.")
        if not ws:
            return
        if not self.settings.terminal_enabled or self.settings.healthcare_hardened:
            ttk.Label(self.content, text="Terminal is disabled by policy in the current profile.").grid(row=1, column=0, sticky="nw")
            return
        top = ttk.Frame(self.content)
        top.grid(row=1, column=0, sticky="ew", pady=(0, 6))
        top.columnconfigure(0, weight=1)
        entry = ttk.Entry(top, font=("Consolas", 10))
        entry.grid(row=0, column=0, sticky="ew")
        output = tk.Text(self.content, wrap="none", font=("Consolas", 9))
        output.grid(row=2, column=0, sticky="nsew")
        self.content.rowconfigure(2, weight=1)

        def run(_event=None) -> None:
            command = entry.get().strip()
            if not command:
                return
            if not messagebox.askyesno("Authorize Command", f"Working directory:\n{ws.root}\n\nCommand:\n{command}\n\nRun this command?"):
                return
            result = TerminalService(ws.root, enabled=True).run(command, authorized=True, timeout=120)
            output.insert("end", f"> {command}\n{result.stdout}{result.stderr}\n[exit {result.exit_code}]\n\n")
            output.see("end")
            ws.audit.append(who=getpass.getuser(), what="tool.executed", target="terminal", intent=command, approval="USER", execution="LOCAL_PROCESS", result=f"exit={result.exit_code}", verification="exit code captured")
            entry.delete(0, "end")
        ttk.Button(top, text="Run", command=run).grid(row=0, column=1, padx=(6, 0))
        entry.bind("<Return>", run)
        entry.focus_set()

    def surface_tasks(self) -> None:
        self._header("Tasks", "Deterministic automation belongs here before AI orchestration.")
        body = ttk.Frame(self.content)
        body.grid(row=1, column=0, sticky="nw")
        ttk.Button(body, text="Create Workspace Backup", command=self.create_backup_ui).grid(row=0, column=0, sticky="w", pady=3)
        ttk.Button(body, text="Restore Workspace Backup", command=self.restore_backup_ui).grid(row=1, column=0, sticky="w", pady=3)
        ttk.Button(body, text="Export Selected Office File to PDF", command=lambda: self.show_surface("Documents")).grid(row=2, column=0, sticky="w", pady=3)
        ttk.Separator(body).grid(row=3, column=0, sticky="ew", pady=8)
        ttk.Button(body, text="Sheet Range -> Document Table", command=self.workflow_sheet_to_doc_ui).grid(row=4, column=0, sticky="w", pady=3)
        ttk.Button(body, text="Document Table -> Sheet", command=self.workflow_doc_to_sheet_ui).grid(row=5, column=0, sticky="w", pady=3)
        ttk.Button(body, text="Document Outline -> Slides", command=self.workflow_outline_to_slides_ui).grid(row=6, column=0, sticky="w", pady=3)
        ttk.Label(body, text="Cross-Office tasks are deterministic and record provenance in the audit log. Optional pinned automation dependencies are required.").grid(row=7, column=0, sticky="w", pady=(12, 0))

    def surface_assistant(self) -> None:
        self._header("Assistant", "Optional, untrusted compute. It is not the authorization or verification boundary.")
        text = [
            f"AI enabled: {self.settings.ai_enabled}",
            f"Remote AI enabled: {self.settings.remote_ai_enabled}",
            f"Embeddings enabled: {self.settings.embeddings_enabled}",
            "Bundled model weights: NO",
            "Direct model shell access: NO",
            "Direct model filesystem access: NO",
            "Direct model network access: NO",
            "Configured provider in this build: NONE",
        ]
        ttk.Label(self.content, text="\n".join(text), justify="left").grid(row=1, column=0, sticky="nw")

    def surface_settings(self) -> None:
        self._header("Settings", "Office, Developer, Healthcare Hardened, and Full Workstation are policy profiles of one application.")
        body = ttk.Frame(self.content)
        body.grid(row=1, column=0, sticky="nw")
        profile_var = tk.StringVar(value=self.settings.profile)
        ai_var = tk.BooleanVar(value=self.settings.ai_enabled)
        index_var = tk.BooleanVar(value=self.settings.content_indexing_enabled)
        timeout_var = tk.IntVar(value=self.settings.session_timeout_minutes)

        ttk.Label(body, text="Workspace mode").grid(row=0, column=0, sticky="w", pady=3)
        profile = ttk.Combobox(body, state="readonly", textvariable=profile_var, values=AppSettings.PROFILES, width=26)
        profile.grid(row=0, column=1, sticky="w", padx=(8, 0), pady=3)
        ttk.Checkbutton(body, text="AI enabled", variable=ai_var).grid(row=1, column=0, columnspan=2, sticky="w", pady=3)
        ttk.Checkbutton(body, text="Local text content indexing", variable=index_var).grid(row=2, column=0, columnspan=2, sticky="w", pady=3)
        ttk.Label(body, text="Healthcare idle lock (minutes)").grid(row=3, column=0, sticky="w", pady=3)
        ttk.Spinbox(body, from_=1, to=240, textvariable=timeout_var, width=8).grid(row=3, column=1, sticky="w", padx=(8, 0), pady=3)

        def save() -> None:
            try:
                self.settings.content_indexing_enabled = index_var.get()
                self.settings.session_timeout_minutes = max(1, min(int(timeout_var.get()), 240))
                requested_ai = ai_var.get()
                self.settings.apply_profile(profile_var.get())
                if not self.settings.healthcare_hardened:
                    self.settings.ai_enabled = requested_ai
                self.ai_policy.enabled = self.settings.ai_enabled
                self.settings_store.save(self.settings)
                self.policy_label.config(text=self._policy_text())
                self.status_var.set("Settings saved")
                self.show_surface("Settings")
            except Exception as exc:
                messagebox.showerror("Settings", str(exc))

        ttk.Button(body, text="Save Policy", command=save).grid(row=4, column=0, sticky="w", pady=(12, 0))
        ttk.Button(body, text="Lock Windows Session Now", command=self.lock_workstation).grid(row=4, column=1, sticky="w", padx=(8, 0), pady=(12, 0))
        ttk.Label(body, text="Healthcare Hardened forces AI/remote AI/embeddings/telemetry/Git network/plugins/macros/updates OFF, Terminal OFF, and LOOPBACK_ONLY policy.", wraplength=760).grid(row=5, column=0, columnspan=2, sticky="w", pady=(12, 0))
        ttk.Label(body, text="Idle locking delegates to the Windows session lock. KHZ does not implement a weaker local password fallback. Third-party process egress is not claimed to be centrally enforced by this in-process policy.", wraplength=760).grid(row=6, column=0, columnspan=2, sticky="w", pady=(5, 0))

    def workflow_sheet_to_doc_ui(self) -> None:
        ws = self.require_workspace()
        if not ws:
            return
        source = filedialog.askopenfilename(title="Select XLSX source", initialdir=ws.root, filetypes=[("Excel workbook", "*.xlsx"), ("Macro workbook", "*.xlsm")])
        if not source:
            return
        try:
            rel = str(Path(source).resolve().relative_to(ws.root))
        except ValueError:
            messagebox.showerror("Workflow", "Source must be inside the active workspace."); return
        sheet = simpledialog.askstring("Sheet Range", "Worksheet name:", initialvalue="Summary")
        cell_range = simpledialog.askstring("Sheet Range", "Range:", initialvalue="A1:B10")
        dest = simpledialog.askstring("Sheet Range", "Destination DOCX:", initialvalue="Sheet-Range.docx")
        if not sheet or not cell_range or not dest:
            return
        try:
            out = sheet_range_to_document_table(ws, rel, sheet, cell_range, dest)
            self.status_var.set(f"Created {out.name} from {sheet}!{cell_range}")
        except Exception as exc:
            messagebox.showerror("Workflow Failed", str(exc))

    def workflow_doc_to_sheet_ui(self) -> None:
        ws = self.require_workspace()
        if not ws:
            return
        source = filedialog.askopenfilename(title="Select DOCX source", initialdir=ws.root, filetypes=[("Word document", "*.docx")])
        if not source:
            return
        try:
            rel = str(Path(source).resolve().relative_to(ws.root))
        except ValueError:
            messagebox.showerror("Workflow", "Source must be inside the active workspace."); return
        index = simpledialog.askinteger("Document Table", "Zero-based table index:", initialvalue=0, minvalue=0)
        dest = simpledialog.askstring("Document Table", "Destination XLSX:", initialvalue="Document-Table.xlsx")
        if index is None or not dest:
            return
        try:
            out = document_table_to_sheet(ws, rel, index, dest)
            self.status_var.set(f"Created {out.name} from table {index}")
        except Exception as exc:
            messagebox.showerror("Workflow Failed", str(exc))

    def workflow_outline_to_slides_ui(self) -> None:
        ws = self.require_workspace()
        if not ws:
            return
        source = filedialog.askopenfilename(title="Select DOCX outline source", initialdir=ws.root, filetypes=[("Word document", "*.docx")])
        if not source:
            return
        try:
            rel = str(Path(source).resolve().relative_to(ws.root))
        except ValueError:
            messagebox.showerror("Workflow", "Source must be inside the active workspace."); return
        dest = simpledialog.askstring("Document Outline", "Destination PPTX:", initialvalue="Outline-Draft.pptx")
        if not dest:
            return
        try:
            out = document_outline_to_slides(ws, rel, dest)
            self.status_var.set(f"Created {out.name} from document headings")
        except Exception as exc:
            messagebox.showerror("Workflow Failed", str(exc))

    def new_office_file(self, kind: str) -> None:
        ws = self.require_workspace()
        if not ws:
            return
        templates = {
            "document": ("BlankDocument.docx", "Untitled.docx", "Documents"),
            "sheet": ("BlankWorkbook.xlsx", "Untitled.xlsx", "Sheets"),
            "slides": ("BlankPresentation.pptx", "Untitled.pptx", "Slides"),
        }
        if kind not in templates:
            raise ValueError("Unsupported Office artifact kind")
        template_name, default_name, surface = templates[kind]
        name = simpledialog.askstring(f"New {surface}", "File name:", initialvalue=default_name)
        if not name:
            return
        suffix = Path(default_name).suffix
        if not name.lower().endswith(suffix):
            name += suffix
        template = Path(__file__).resolve().parent / "assets" / "templates" / template_name
        try:
            path = FileService(ws).import_file(template, name)
            self.status_var.set(f"Created {name}")
            self.show_surface(surface)
            try:
                self.office.open_registered_or_system(path)
            except Exception:
                pass
        except Exception as exc:
            messagebox.showerror("Create Office File", str(exc))

    def show_versions_ui(self, path: Path | None) -> None:
        ws = self.require_workspace()
        if not ws or not path or not path.is_file():
            return
        rel = str(path.relative_to(ws.root))
        try:
            item_id = FileService(ws).index_file(rel)
        except Exception as exc:
            messagebox.showerror("Versions", str(exc)); return
        version_dir = ws.root / ".khz" / "versions" / item_id
        versions = sorted(version_dir.glob("*"), reverse=True) if version_dir.exists() else []
        popup = tk.Toplevel(self); popup.title(f"Versions - {path.name}"); popup.geometry("700x380"); popup.transient(self)
        box = tk.Listbox(popup, font=("Consolas", 9)); box.pack(fill="both", expand=True, padx=10, pady=10)
        for v in versions: box.insert("end", v.name)
        def restore_selected():
            sel = box.curselection()
            if not sel: return
            version = versions[sel[0]]
            if not messagebox.askyesno("Restore Version", f"Restore {version.name} over {rel}? Current content will be snapshotted first."):
                return
            FileService(ws).atomic_write(rel, version.read_bytes(), preserve_version=True)
            ws.audit.append(who=getpass.getuser(), what="file.version_restored", target=rel, approval="USER", result=version.name, verification="atomic write completed")
            self.status_var.set(f"Restored version: {version.name}"); popup.destroy()
        ttk.Button(popup, text="Restore Selected", command=restore_selected).pack(pady=(0,10))
        if not versions: box.insert("end", "No snapshots yet. KHZ creates a snapshot before Office edits and significant direct writes.")

    def new_text_document(self) -> None:
        ws = self.require_workspace()
        if not ws:
            return
        name = simpledialog.askstring("New Text Document", "File name:", initialvalue="Untitled.txt")
        if not name:
            return
        if not name.lower().endswith(".txt"):
            name += ".txt"
        try:
            FileService(ws).atomic_write(name, b"", preserve_version=False)
            self.status_var.set(f"Created {name}")
            self.show_surface("Documents")
        except Exception as exc:
            messagebox.showerror("Create Document", str(exc))

    def create_backup_ui(self) -> None:
        ws = self.require_workspace()
        if not ws:
            return
        dest = filedialog.asksaveasfilename(title="Create Workspace Backup", defaultextension=".khzbackup.zip", filetypes=[("KHZ Backup", "*.khzbackup.zip"), ("ZIP", "*.zip")])
        if not dest:
            return
        try:
            backup = BackupService(ws).create(Path(dest))
            self.status_var.set(f"Backup verified and published: {backup}")
        except Exception as exc:
            messagebox.showerror("Backup Failed", str(exc))

    def restore_backup_ui(self) -> None:
        backup = filedialog.askopenfilename(title="Restore KHZ Workspace Backup", filetypes=[("KHZ Backup", "*.zip"), ("All files", "*.*")])
        if not backup:
            return
        destination = filedialog.askdirectory(title="Select parent folder for restored workspace")
        if not destination:
            return
        name = simpledialog.askstring("Restore Workspace", "Restored workspace folder name:", initialvalue="Restored-Workspace")
        if not name:
            return
        target = Path(destination) / name
        if target.exists() and not messagebox.askyesno("Restore Workspace", "Destination exists. KHZ will preserve it before atomic replacement. Continue?"):
            return
        try:
            restored, preserved = BackupService.restore(Path(backup), target, preserve_existing=True)
            self.status_var.set(f"Restore validated: {restored}" + (f" | previous preserved: {preserved}" if preserved else ""))
        except Exception as exc:
            messagebox.showerror("Restore Failed", str(exc))

    def _open_file(self, path: Path) -> None:
        if path.suffix.lower() in OFFICE_KIND:
            try:
                self.office.open_registered_or_system(path)
                return
            except Exception:
                pass
        if os.name == "nt":
            os.startfile(path)  # type: ignore[attr-defined]
        else:
            subprocess.Popen(["xdg-open", str(path)])

    def _open_with(self, path: Path | None) -> None:
        if not path or not path.is_file():
            return
        if os.name == "nt":
            subprocess.Popen(["rundll32.exe", "shell32.dll,OpenAs_RunDLL", str(path)])
        else:
            self._open_file(path)

    def _open_explorer(self, path: Path) -> None:
        if os.name == "nt":
            subprocess.Popen(["explorer.exe", str(path)])
        else:
            subprocess.Popen(["xdg-open", str(path)])

    def _reveal(self, path: Path | None) -> None:
        if not path:
            return
        if os.name == "nt":
            subprocess.Popen(["explorer.exe", "/select,", str(path)])
        else:
            self._open_explorer(path.parent)

    @staticmethod
    def _format_size(size: int) -> str:
        units = ["B", "KB", "MB", "GB"]
        value = float(size)
        for unit in units:
            if value < 1024 or unit == units[-1]:
                return f"{value:.0f} {unit}" if unit == "B" else f"{value:.1f} {unit}"
            value /= 1024
        return str(size)

    def open_command_palette(self, _event=None) -> str:
        popup = tk.Toplevel(self)
        popup.title("Command Palette")
        popup.transient(self)
        popup.geometry("520x430")
        popup.grab_set()
        query = ttk.Entry(popup, font=("Segoe UI", 11))
        query.pack(fill="x", padx=10, pady=(10, 6))
        listbox = tk.Listbox(popup, font=("Segoe UI", 10), activestyle="dotbox")
        listbox.pack(fill="both", expand=True, padx=10, pady=(0, 10))
        commands = {
            "Open Workspace": self.choose_workspace,
            "Open Files": lambda: self.show_surface("Files"),
            "New Document": lambda: self.new_office_file("document"),
            "New Sheet": lambda: self.new_office_file("sheet"),
            "New Presentation": lambda: self.new_office_file("slides"),
            "Open Sheets": lambda: self.show_surface("Sheets"),
            "Open Presentations": lambda: self.show_surface("Slides"),
            "Search": lambda: self.show_surface("Search"),
            "Open Repository": lambda: self.show_surface("Repositories"),
            "Open Terminal": lambda: self.show_surface("Terminal"),
            "Create Backup": self.create_backup_ui,
            "Restore Backup": self.restore_backup_ui,
            "Lock Workstation": self.lock_workstation,
            "Settings": lambda: self.show_surface("Settings"),
        }

        def refresh(_event=None) -> None:
            needle = query.get().casefold()
            listbox.delete(0, "end")
            for name in commands:
                if needle in name.casefold():
                    listbox.insert("end", name)

        def execute(_event=None) -> None:
            sel = listbox.curselection()
            if not sel:
                return
            name = listbox.get(sel[0])
            popup.destroy()
            commands[name]()
        query.bind("<KeyRelease>", refresh)
        query.bind("<Return>", execute)
        listbox.bind("<Double-1>", execute)
        popup.bind("<Escape>", lambda _e: popup.destroy())
        refresh()
        query.focus_set()
        return "break"


def main() -> None:
    initial = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    app = KHZApp(initial)
    app.mainloop()


if __name__ == "__main__":
    main()
