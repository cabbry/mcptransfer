"""Render the white paper markdown to professional PDFs (reportlab).

Parses the deliberately-regular markdown subset used by the white paper:
title block, ## / ### headings, paragraphs, - bullets, ``` code fences,
| tables |, **bold**, *italic*, `code`, [text](url).

Usage:  python docs/tools/make_whitepaper_pdf.py [en|fr]   (default: both)
Output: docs/MCPTransfer-WhitePaper-v1.0.pdf
        docs/MCPTransfer-WhitePaper-v1.0.fr.pdf
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_JUSTIFY
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (
    BaseDocTemplate, Frame, HRFlowable, KeepTogether, NextPageTemplate,
    PageBreak, PageTemplate, Paragraph, Preformatted, Spacer, Table,
    TableStyle,
)
from reportlab.platypus.tableofcontents import TableOfContents

ROOT = Path(__file__).resolve().parents[2]
LANGS = {
    "en": {
        "src": ROOT / "docs" / "WHITEPAPER.md",
        "out": ROOT / "docs" / "MCPTransfer-WhitePaper-v1.0.pdf",
        "contents": "Contents",
        "footer": "MCPTransfer — White Paper v1.0",
        "pdf_title": "MCPTransfer White Paper v1.0",
        "note": "Sent for review to blockchain teams and foundations. "
                "Reference implementation and live deployment available "
                "on request.",
    },
    "fr": {
        "src": ROOT / "docs" / "WHITEPAPER.fr.md",
        "out": ROOT / "docs" / "MCPTransfer-WhitePaper-v1.0.fr.pdf",
        "contents": "Sommaire",
        "footer": "MCPTransfer — Livre blanc v1.0",
        "pdf_title": "MCPTransfer Livre blanc v1.0",
        "note": "Adressé aux équipes blockchain et aux fondations pour "
                "relecture. Implémentation de référence et déploiement live "
                "disponibles sur demande.",
    },
}

NAVY = colors.HexColor("#14213D")
BLUE = colors.HexColor("#2563EB")
GREY = colors.HexColor("#5B6472")
CODE_BG = colors.HexColor("#F3F5F9")
ROW_BG = colors.HexColor("#F7F9FC")

# ── fonts: Segoe UI + Consolas (full Unicode, shipped with Windows) ─────────
FONTS = Path(r"C:\Windows\Fonts")
pdfmetrics.registerFont(TTFont("Body", FONTS / "segoeui.ttf"))
pdfmetrics.registerFont(TTFont("Body-Bold", FONTS / "segoeuib.ttf"))
pdfmetrics.registerFont(TTFont("Body-Italic", FONTS / "segoeuii.ttf"))
pdfmetrics.registerFont(TTFont("Body-BoldItalic", FONTS / "segoeuiz.ttf"))
pdfmetrics.registerFont(TTFont("Mono", FONTS / "consola.ttf"))
pdfmetrics.registerFontFamily(
    "Body", normal="Body", bold="Body-Bold",
    italic="Body-Italic", boldItalic="Body-BoldItalic")

S = {
    "title": ParagraphStyle("title", fontName="Body-Bold", fontSize=22,
                            leading=28, textColor=NAVY, alignment=TA_CENTER),
    "subtitle": ParagraphStyle("subtitle", fontName="Body", fontSize=13,
                               leading=18, textColor=GREY, alignment=TA_CENTER),
    "meta": ParagraphStyle("meta", fontName="Body", fontSize=10.5, leading=15,
                           textColor=GREY, alignment=TA_CENTER),
    "h1": ParagraphStyle("h1", fontName="Body-Bold", fontSize=14.5, leading=18,
                         textColor=NAVY, spaceBefore=16, spaceAfter=6),
    "h2": ParagraphStyle("h2", fontName="Body-Bold", fontSize=11.5, leading=15,
                         textColor=BLUE, spaceBefore=10, spaceAfter=4),
    "body": ParagraphStyle("body", fontName="Body", fontSize=9.8, leading=14.2,
                           alignment=TA_JUSTIFY, spaceAfter=6),
    "bullet": ParagraphStyle("bullet", fontName="Body", fontSize=9.8,
                             leading=14.2, alignment=TA_JUSTIFY, spaceAfter=3,
                             leftIndent=14, bulletIndent=4),
    "code": ParagraphStyle("code", fontName="Mono", fontSize=8.2, leading=11,
                           backColor=CODE_BG, borderPadding=6, spaceAfter=8,
                           spaceBefore=2, leftIndent=4),
    "cell": ParagraphStyle("cell", fontName="Body", fontSize=8.8, leading=12),
    "cellh": ParagraphStyle("cellh", fontName="Body-Bold", fontSize=8.8,
                            leading=12, textColor=colors.white),
    "toch1": ParagraphStyle("toch1", fontName="Body", fontSize=10, leading=16,
                            leftIndent=4),
}
# Same look as h1 but a distinct style name so the TOC/bookmarks logic
# (keyed on style name "h1") does not index the "Contents" heading itself.
S["h1-unlisted"] = ParagraphStyle("h1-unlisted", parent=S["h1"])

ESC = {"&": "&amp;", "<": "&lt;", ">": "&gt;"}


def inline(md: str) -> str:
    """Markdown inline subset -> reportlab paragraph XML."""
    out = "".join(ESC.get(c, c) for c in md)
    out = re.sub(r"\[([^\]]+)\]\(([^)]+)\)",
                 r'<link href="\2" color="#2563EB"><u>\1</u></link>', out)
    out = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", out)
    out = re.sub(r"(?<![\w*])\*([^*\n]+)\*(?![\w*])", r"<i>\1</i>", out)
    out = re.sub(r"`([^`]+)`",
                 r'<font face="Mono" size="8.8" color="#1F2A44">\1</font>', out)
    return out


def make_table(rows: list[list[str]], avail: float) -> Table:
    header, body = rows[0], rows[1:]
    n = len(header)
    if n == 2:
        widths = [avail * 0.30, avail * 0.70]
    elif n == 3:
        widths = [avail * 0.22, avail * 0.33, avail * 0.45]
    else:
        widths = [avail / n] * n
    data = [[Paragraph(inline(c), S["cellh"]) for c in header]]
    data += [[Paragraph(inline(c), S["cell"]) for c in r] for r in body]
    t = Table(data, colWidths=widths, repeatRows=1)
    style = [
        ("BACKGROUND", (0, 0), (-1, 0), NAVY),
        ("GRID", (0, 0), (-1, -1), 0.4, colors.HexColor("#C9D2E0")),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("TOPPADDING", (0, 0), (-1, -1), 4),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
        ("LEFTPADDING", (0, 0), (-1, -1), 6),
        ("RIGHTPADDING", (0, 0), (-1, -1), 6),
    ]
    style += [("BACKGROUND", (0, i), (-1, i), ROW_BG)
              for i in range(2, len(data), 2)]
    t.setStyle(TableStyle(style))
    t.spaceAfter = 8
    return t


class Doc(BaseDocTemplate):
    """Two-pass build with TOC; heading flowables notify TOC entries."""

    def afterFlowable(self, flowable):
        if isinstance(flowable, Paragraph):
            if flowable.style.name == "h1":
                text = re.sub(r"<[^>]+>", "", flowable.text)
                self.notify("TOCEntry", (0, text, self.page))
                key = re.sub(r"\W+", "-", text)[:50]
                self.canv.bookmarkPage(key)
                self.canv.addOutlineEntry(text, key, 0, False)


def footer(canvas, doc):
    if doc.page == 1:
        return
    canvas.saveState()
    canvas.setFont("Body", 8)
    canvas.setFillColor(GREY)
    canvas.drawString(20 * mm, 12 * mm, doc.footer_text)
    canvas.drawRightString(A4[0] - 20 * mm, 12 * mm, f"{doc.page}")
    canvas.setStrokeColor(colors.HexColor("#D8DEE8"))
    canvas.setLineWidth(0.5)
    canvas.line(20 * mm, 16 * mm, A4[0] - 20 * mm, 16 * mm)
    canvas.restoreState()


def build_story(md: str, avail: float, lang: dict) -> list:
    lines = md.splitlines()
    story: list = []
    i = 0

    # ── title block (first "# " line + version/author lines) ──────────────
    title_line = next(l for l in lines if l.startswith("# "))
    title, _, subtitle = title_line[2:].partition(" — ")
    story += [
        Spacer(1, 48 * mm),
        Paragraph(inline(title), S["title"]),
        Spacer(1, 6 * mm),
        Paragraph(inline(subtitle), S["subtitle"]),
        Spacer(1, 14 * mm),
        HRFlowable(width="40%", thickness=1, color=BLUE, hAlign="CENTER"),
        Spacer(1, 8 * mm),
    ]
    for l in lines:
        if l.startswith("**Version"):
            story.append(Paragraph(inline(l), S["meta"]))
        elif l.startswith("**Jean-Romain"):
            story.append(Paragraph(inline(l), S["meta"]))
    story += [Spacer(1, 60 * mm),
              Paragraph(lang["note"], S["meta"]),
              PageBreak()]

    # ── table of contents ──────────────────────────────────────────────────
    story.append(Paragraph(lang["contents"], S["h1-unlisted"]))
    toc = TableOfContents()
    toc.levelStyles = [S["toch1"]]
    story += [toc, PageBreak()]

    # ── body ───────────────────────────────────────────────────────────────
    # skip everything up to and including the first "---" (title block)
    i = lines.index("---") + 1
    bullets_open: list = []

    def flush_bullets():
        nonlocal bullets_open
        story.extend(bullets_open)
        bullets_open = []

    while i < len(lines):
        line = lines[i]

        if line.startswith("```"):
            flush_bullets()
            block = []
            i += 1
            while i < len(lines) and not lines[i].startswith("```"):
                block.append(lines[i])
                i += 1
            story.append(KeepTogether(
                Preformatted("\n".join(block), S["code"])))
        elif line.startswith("|") and i + 1 < len(lines) \
                and re.match(r"^\|[\s\-|]+\|$", lines[i + 1]):
            flush_bullets()
            rows = [[c.strip() for c in line.strip("|").split("|")]]
            i += 2
            while i < len(lines) and lines[i].startswith("|"):
                rows.append([c.strip() for c in lines[i].strip("|").split("|")])
                i += 1
            i -= 1
            story.append(make_table(rows, avail))
        elif line.startswith("### "):
            flush_bullets()
            story.append(Paragraph(inline(line[4:]), S["h2"]))
        elif line.startswith("## "):
            flush_bullets()
            story.append(Paragraph(inline(line[3:]), S["h1"]))
        elif line.startswith("- "):
            bullets_open.append(Paragraph(
                inline(line[2:]), S["bullet"], bulletText="•"))
        elif line.strip() in {"---", ""}:
            flush_bullets()
        elif line.startswith("# ") or line.startswith("**Version") \
                or line.startswith("**Jean-Romain"):
            pass  # already on the title page
        else:
            flush_bullets()
            # merge soft-wrapped continuation lines into one paragraph
            para = [line]
            while (i + 1 < len(lines) and lines[i + 1].strip()
                   and not re.match(r"^(#|\||```|- |---)", lines[i + 1])):
                i += 1
                para.append(lines[i])
            story.append(Paragraph(inline(" ".join(para)), S["body"]))
        i += 1

    flush_bullets()
    return story


def build(lang: dict) -> None:
    md = lang["src"].read_text(encoding="utf-8")
    margin = 20 * mm
    avail = A4[0] - 2 * margin
    doc = Doc(str(lang["out"]), pagesize=A4,
              leftMargin=margin, rightMargin=margin,
              topMargin=18 * mm, bottomMargin=22 * mm,
              title=lang["pdf_title"],
              author="Jean-Romain Bouquet")
    doc.footer_text = lang["footer"]
    frame = Frame(margin, 22 * mm, avail, A4[1] - 40 * mm, id="main")
    doc.addPageTemplates([PageTemplate(id="page", frames=[frame],
                                       onPage=footer)])
    doc.multiBuild(build_story(md, avail, lang))
    print(f"OK -> {lang['out']}")


def main() -> int:
    wanted = sys.argv[1:] or list(LANGS)
    for code in wanted:
        build(LANGS[code])
    return 0


if __name__ == "__main__":
    sys.exit(main())
