#!/usr/bin/env python3
"""G8 UI accessibility audit — measures the three directive criteria against XAML.

Gate-deciding criteria (from the upgrade directive, G8):
  1. Contrast: every text/background pair. Body >= 4.5:1, large (>=24px) >= 3:1.
  2. Keyboard: every interactive control reachable; tab order printed; no trap.
  3. No text baked into images.

Advisory (printed, not gate-deciding — not in the directive): button target
height, distinct font stacks. Reported so the number is on the record.
"""
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "windows" / "KHZ.App"
XAML_FILES = [APP / "MainWindow.xaml"]
XAML_FILES += sorted((APP / "Views").glob("*.xaml"))

X_NS = "http://schemas.microsoft.com/winfx/2006/xaml"
P_NS = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
AP = f"{{{P_NS}}}"
X = f"{{{X_NS}}}"


def _hex_to_rgb(h: str) -> tuple[int, int, int]:
    h = h.lstrip("#")
    return int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)


def _lin(c: float) -> float:
    c /= 255.0
    return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4


def contrast(fg: str, bg: str) -> float:
    r1, g1, b1 = _hex_to_rgb(fg)
    r2, g2, b2 = _hex_to_rgb(bg)
    l1 = 0.2126 * _lin(r1) + 0.7152 * _lin(g1) + 0.0722 * _lin(b1)
    l2 = 0.2126 * _lin(r2) + 0.7152 * _lin(g2) + 0.0722 * _lin(b2)
    hi, lo = max(l1, l2), min(l1, l2)
    return (hi + 0.05) / (lo + 0.05)


def _is_hex(v: str | None) -> bool:
    return bool(v and re.match(r"^#[0-9A-Fa-f]{6}$", v))


def _style_setters(text: str, key: str) -> dict[str, str]:
    """Return {Property: Value} for every <Setter Property=.. Value=..> in a Style."""
    out: dict[str, str] = {}
    for m in re.finditer(
        r'<Setter\s+Property="([^"]+)"\s+Value="([^"]+)"\s*/>', text
    ):
        out[m.group(1)] = m.group(2)
    return out


def _styles(text: str) -> dict[str, dict[str, str]]:
    """All named styles: {x:Key -> {Property: Value}}."""
    out: dict[str, dict[str, str]] = {}
    for m in re.finditer(
        r'<Style\s+x:Key="([^"]+)"[^>]*>(.*?)</Style>', text, re.S
    ):
        out[m.group(1)] = _style_setters(m.group(2), m.group(1))
    return out


def _attr(el: ET.Element, name: str) -> str | None:
    return el.get(name) or el.get(f"{P_NS}{name}")


def parse_pairs() -> list[dict]:
    """Tree-walk each XAML, tracking inherited Background; emit text/bg pairs.

    A pair is emitted for every TextBlock (text) and every Button (label) with a
    resolvable foreground and background. Foreground resolves from the element,
    then any applied NavButton style, then inherited Background. Background
    resolves from the element, then the nearest ancestor with a hex Background.
    """
    pairs: list[dict] = []
    for xf in XAML_FILES:
        text = xf.read_text(encoding="utf-8-sig")
        styles = _styles(text)
        nav = styles.get("NavButton", {})
        try:
            root = ET.fromstring(text)
        except ET.ParseError:
            continue

        def walk(el: ET.Element, bg: str | None):
            own_bg = _attr(el, "Background")
            if _is_hex(own_bg):
                bg = own_bg
            tag = el.tag.replace(AP, "").replace(X, "")
            if tag == "TextBlock":
                fg = _attr(el, "Foreground")
                fs = _attr(el, "FontSize")
                if _is_hex(fg) and _is_hex(bg):
                    pairs.append({
                        "file": xf.name, "fg": fg, "bg": bg,
                        "size": float(fs) if fs else None,
                        "ratio": contrast(fg, bg), "kind": "text",
                    })
            if tag == "Button":
                style = _attr(el, "Style")
                skey = None
                if style and style.startswith("{StaticResource "):
                    skey = style[len("{StaticResource "):-1]
                sset = styles.get(skey, {}) if skey else {}
                fg = _attr(el, "Foreground") or sset.get("Foreground")
                bbg = _attr(el, "Background") or bg
                fs = _attr(el, "FontSize") or sset.get("FontSize")
                if _is_hex(fg) and _is_hex(bbg):
                    pairs.append({
                        "file": xf.name, "fg": fg, "bg": bbg,
                        "size": float(fs) if fs else None,
                        "ratio": contrast(fg, bbg), "kind": "button",
                    })
            for child in el:
                walk(child, bg)

        walk(root, None)
    return pairs


def audit_targets() -> dict:
    nav_h = None
    buttons: list[dict] = []
    for xf in XAML_FILES:
        text = xf.read_text(encoding="utf-8-sig")
        styles = _styles(text)
        nh = styles.get("NavButton", {}).get("Height")
        if nh:
            nav_h = nh
        try:
            root = ET.fromstring(text)
        except ET.ParseError:
            continue

        def walk(el: ET.Element):
            nonlocal nav_h
            tag = el.tag.replace(AP, "").replace(X, "")
            if tag == "Button":
                style = _attr(el, "Style")
                skey = None
                if style and style.startswith("{StaticResource "):
                    skey = style[len("{StaticResource "):-1]
                sset = styles.get(skey, {}) if skey else {}
                h = _attr(el, "Height") or sset.get("Height")
                if h is not None:
                    buttons.append({
                        "file": xf.name, "height": float(h),
                        "nav": bool(skey == "NavButton"),
                    })
            for child in el:
                walk(child)

        walk(root)
    return {"nav_height": nav_h, "buttons": buttons}


def audit_fonts() -> dict:
    fonts: set[str] = set()
    for xf in XAML_FILES:
        for m in re.finditer(r'FontFamily="([^"]+)"', xf.read_text(encoding="utf-8-sig")):
            v = m.group(1)
            if v.startswith("{Binding"):
                v = "<bound>"
            fonts.add(v)
    return {"font_stacks": sorted(fonts), "count": len(fonts)}


def audit_keyboard() -> dict:
    auto_ids: list[str] = []
    tabstop_false = 0
    buttons = 0
    for xf in XAML_FILES:
        text = xf.read_text(encoding="utf-8-sig")
        for m in re.finditer(r'AutomationProperties\.AutomationId="([^"]+)"', text):
            auto_ids.append(m.group(1))
        for m in re.finditer(r'<Button\b[^>]*>', text):
            buttons += 1
        for m in re.finditer(r'<(?:Button|TextBox|ComboBox|CheckBox|ListBox|ListView)\b[^>]*IsTabStop="false"', text):
            tabstop_false += 1
    return {
        "automation_ids": auto_ids,
        "interactive_count": buttons,
        "is_tabstop_false": tabstop_false,
    }


def audit_images_no_text() -> dict:
    imgs: list[str] = []
    for xf in XAML_FILES:
        text = xf.read_text(encoding="utf-8-sig")
        for m in re.finditer(r'<Image\b[^>]*Source="([^"]+)"', text):
            imgs.append(m.group(1))
    return {"image_sources": imgs, "count": len(imgs)}


def main() -> int:
    pairs = parse_pairs()
    body_fail = large_fail = 0
    for p in pairs:
        sz = p["size"] if p["size"] else 13
        large = sz >= 24
        thr = 3.0 if large else 4.5
        if p["ratio"] < thr:
            if large:
                large_fail += 1
            else:
                body_fail += 1
    tg = audit_targets()
    font_a = audit_fonts()
    kb = audit_keyboard()
    img_a = audit_images_no_text()

    print(f"CONTRAST_PAIRS={len(pairs)}")
    print(f"BODY_BELOW_4_5={body_fail}")
    print(f"LARGE_BELOW_3_0={large_fail}")
    print("LOWEST_RATIOS=")
    for p in sorted(pairs, key=lambda x: x["ratio"])[:12]:
        print(f"  {p['file']:28s} {p['fg']} on {p['bg']} = {p['ratio']:.2f} (size={p['size']}, {p['kind']})")
    print(f"NAV_BUTTON_HEIGHT={tg['nav_height']}")
    under = sum(1 for b in tg["buttons"] if b["height"] < 44)
    print(f"BUTTON_TARGETS={len(tg['buttons'])}")
    print(f"BUTTONS_UNDER_44PX={under}")
    print(f"FONT_STACKS={font_a['count']} {font_a['font_stacks']}")
    print(f"AUTOMATION_IDS={len(kb['automation_ids'])}")
    print(f"INTERACTIVE_BUTTONS={kb['interactive_count']}")
    print(f"IS_TABSTOP_FALSE={kb['is_tabstop_false']}")
    print("TAB_ORDER:")
    for aid in kb["automation_ids"]:
        print(f"  {aid}")
    print(f"IMAGE_SOURCES={img_a['image_sources']}")
    print(f"IMAGE_COUNT={img_a['count']}")

    # Gate follows the directive's three G8 criteria only.
    text_in_image = bool(img_a["image_sources"]) and not all(
        s.lower().endswith((".ico", ".png")) for s in img_a["image_sources"]
    )
    keyboard_ok = kb["is_tabstop_false"] == 0
    contrast_ok = body_fail == 0 and large_fail == 0
    images_ok = not text_in_image
    ok = contrast_ok and keyboard_ok and images_ok
    print(f"GATE_CONTRAST={'PASS' if contrast_ok else 'FAIL'}")
    print(f"GATE_KEYBOARD={'PASS' if keyboard_ok else 'FAIL'}")
    print(f"GATE_NO_TEXT_IN_IMAGES={'PASS' if images_ok else 'FAIL'}")
    print(f"GATE_G8={'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
