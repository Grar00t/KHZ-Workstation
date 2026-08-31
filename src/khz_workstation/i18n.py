from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class LocaleInfo:
    code: str
    rtl: bool
    canonical: bool = False


LOCALES = {
    "en-US": LocaleInfo("en-US", rtl=False, canonical=True),
    "ar-SA": LocaleInfo("ar-SA", rtl=True, canonical=False),
}

# Canonical English resources. Secondary catalogs can be layered without changing command,
# path, hash, code, or spreadsheet-function semantics.
_EN = {
    "app.title": "KHZ Workstation",
    "surface.home": "Home",
    "surface.files": "Files",
    "surface.documents": "Documents",
    "surface.sheets": "Sheets",
    "surface.slides": "Slides",
    "surface.pdf": "PDF",
    "surface.data": "Data",
    "surface.search": "Search",
    "surface.activity": "Activity",
    "surface.repositories": "Repositories",
    "surface.terminal": "Terminal",
    "surface.tasks": "Tasks",
    "surface.assistant": "Assistant",
    "surface.settings": "Settings",
}

_CATALOGS = {"en-US": _EN}


class Localizer:
    def __init__(self, locale: str = "en-US") -> None:
        self.locale = locale if locale in LOCALES else "en-US"

    @property
    def info(self) -> LocaleInfo:
        return LOCALES[self.locale]

    def text(self, key: str, **values: object) -> str:
        catalog = _CATALOGS.get(self.locale, _EN)
        raw = catalog.get(key, _EN.get(key, key))
        return raw.format(**values) if values else raw
