"""把 docs/SOP*.md 编译成一份可直接分发的 Word 手册。

用法: python3 scripts/build_sop_manual.py
输出: docs/SOP检测系统使用与调参手册.docx
"""
import re
from pathlib import Path

from docx import Document
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Cm, Pt, RGBColor

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs" / "SOP检测系统使用与调参手册.docx"

SOURCES = [
    ROOT / "docs" / "SOP使用指南.md",
    ROOT / "docs" / "SOP_01_使用手册.md",
    ROOT / "docs" / "SOP_02_新建SOP检测.md",
    ROOT / "docs" / "SOP_03_模型训练.md",
    ROOT / "docs" / "SOP_04_调参指南.md",
]

CODE_FONT = "Consolas"
CN_FONT = "微软雅黑"


def set_font(run, name=CN_FONT, size=None, bold=None, color=None):
    run.font.name = name
    run._element.rPr.rFonts.set(qn("w:eastAsia"), CN_FONT if name != CODE_FONT else "等线")
    if size is not None:
        run.font.size = Pt(size)
    if bold is not None:
        run.bold = bold
    if color is not None:
        run.font.color.rgb = RGBColor(*color)


def shade(element, hex_fill):
    shd = OxmlElement("w:shd")
    shd.set(qn("w:val"), "clear")
    shd.set(qn("w:color"), "auto")
    shd.set(qn("w:fill"), hex_fill)
    element.append(shd)


def add_inline(par, text):
    """处理 **粗体** 与 `行内代码`。"""
    for token in re.split(r"(\*\*.+?\*\*|`[^`]+`)", text):
        if not token:
            continue
        if token.startswith("**") and token.endswith("**"):
            set_font(par.add_run(token[2:-2]), bold=True)
        elif token.startswith("`") and token.endswith("`"):
            run = par.add_run(token[1:-1])
            set_font(run, name=CODE_FONT, size=9, color=(0x1F, 0x3A, 0x5F))
            shade(run._element.get_or_add_rPr(), "F2F4F7")
        else:
            par.add_run(token)


def add_code_block(doc, lines):
    table = doc.add_table(rows=1, cols=1)
    table.alignment = WD_TABLE_ALIGNMENT.CENTER
    cell = table.cell(0, 0)
    shade(cell._tc.get_or_add_tcPr(), "F5F6F8")
    par = cell.paragraphs[0]
    par.paragraph_format.space_before = Pt(2)
    par.paragraph_format.space_after = Pt(2)
    run = par.add_run("\n".join(lines))
    run.font.name = CODE_FONT
    run._element.rPr.rFonts.set(qn("w:eastAsia"), "等线")
    run.font.size = Pt(8.5)
    doc.add_paragraph()


def add_table(doc, rows):
    header, body = rows[0], rows[1:]
    table = doc.add_table(rows=len(rows), cols=len(header))
    table.style = "Table Grid"
    table.alignment = WD_TABLE_ALIGNMENT.CENTER
    for c, text in enumerate(header):
        cell = table.cell(0, c)
        cell.text = ""
        set_font(cell.paragraphs[0].add_run(text.strip()), size=9, bold=True)
        shade(cell._tc.get_or_add_tcPr(), "E8F0F8")
    for r, row in enumerate(body, start=1):
        for c in range(len(header)):
            text = row[c] if c < len(row) else ""
            cell = table.cell(r, c)
            cell.text = ""
            add_inline(cell.paragraphs[0], text.strip())
            for run in cell.paragraphs[0].runs:
                run.font.size = Pt(9)
    doc.add_paragraph()


def is_table_sep(line):
    return bool(re.fullmatch(r"\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?", line.strip()))


def split_row(line):
    line = line.strip()
    if line.startswith("|"):
        line = line[1:]
    if line.endswith("|"):
        line = line[:-1]
    return [c.replace("\\|", "|") for c in re.split(r"(?<!\\)\|", line)]


def add_paragraph(doc, text):
    par = doc.add_paragraph()
    add_inline(par, text)
    return par


def convert(doc, md_path):
    lines = md_path.read_text(encoding="utf-8").splitlines()
    i = 0
    in_code = False
    code_buf = []
    while i < len(lines):
        line = lines[i]

        if line.strip().startswith("```"):
            if not in_code:
                in_code, code_buf = True, []
                i += 1
                continue
            add_code_block(doc, code_buf)
            in_code = False
            i += 1
            continue
        if in_code:
            code_buf.append(line)
            i += 1
            continue

        stripped = line.strip()
        if not stripped:
            i += 1
            continue
        if stripped == "---":
            i += 1
            continue

        m = re.match(r"^(#{1,4})\s+(.*)$", line)
        if m:
            level = len(m.group(1))
            text = m.group(2).strip()
            # 文档内一级标题降为章级（H1），wiki 内二级降为节
            h = doc.add_heading(level=min(level, 3)) if False else None
            par = doc.add_paragraph(style=f"Heading {min(level, 3)}")
            run = par.add_run(text)
            set_font(run, size={1: 16, 2: 13, 3: 11.5, 4: 10.5}[level], bold=True,
                     color=(0x1F, 0x4E, 0x79))
            i += 1
            continue

        if stripped.startswith(">"):
            par = doc.add_paragraph()
            par.paragraph_format.left_indent = Cm(0.5)
            run = par.add_run(stripped.lstrip("> ").strip())
            set_font(run, size=9, color=(0x6B, 0x6B, 0x6B))
            i += 1
            continue

        if stripped.startswith("|") and i + 1 < len(lines) and is_table_sep(lines[i + 1]):
            rows = [split_row(stripped)]
            i += 2
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append(split_row(lines[i]))
                i += 1
            add_table(doc, rows)
            continue

        m = re.match(r"^(\s*)([-*]|\d+\.)\s+(.*)$", line)
        if m:
            indent = len(m.group(1))
            level = min(indent // 2, 2)
            ordered = m.group(2)[0].isdigit()
            style = "List Number" if ordered else "List Bullet"
            if level > 0:
                style = f"{style} {level + 1}"
            par = doc.add_paragraph(style=style)
            add_inline(par, m.group(3))
            for run in par.runs:
                run.font.size = Pt(10)
            i += 1
            continue

        add_paragraph(doc, stripped)
        i += 1


def build():
    doc = Document()
    sec = doc.sections[0]
    sec.page_width, sec.page_height = Cm(21), Cm(29.7)
    sec.left_margin = sec.right_margin = Cm(2)
    sec.top_margin = sec.bottom_margin = Cm(1.8)

    normal = doc.styles["Normal"]
    normal.font.name = CN_FONT
    normal.font.size = Pt(10)
    normal.element.rPr.rFonts.set(qn("w:eastAsia"), CN_FONT)
    normal.paragraph_format.space_after = Pt(4)
    normal.paragraph_format.line_spacing = 1.15

    title = doc.add_paragraph()
    title.alignment = WD_ALIGN_PARAGRAPH.CENTER
    set_font(title.add_run("视觉检测系统 · SOP 作业合规检测"), size=22, bold=True,
             color=(0x1F, 0x4E, 0x79))
    sub = doc.add_paragraph()
    sub.alignment = WD_ALIGN_PARAGRAPH.CENTER
    set_font(sub.add_run("使用手册 · 新建 SOP 检测 · 模型训练 · 调参指南"), size=13,
             color=(0x55, 0x55, 0x55))
    meta = doc.add_paragraph()
    meta.alignment = WD_ALIGN_PARAGRAPH.CENTER
    set_font(meta.add_run("VisionInspectionSystem · 汇编自 docs/SOP*.md"), size=9,
             color=(0x88, 0x88, 0x88))

    toc = doc.add_paragraph()
    set_font(toc.add_run("目录"), size=14, bold=True, color=(0x1F, 0x4E, 0x79))
    for n, src in enumerate(SOURCES, 1):
        first = next((ln for ln in src.read_text(encoding="utf-8").splitlines()
                      if ln.startswith("# ")), "")
        par = doc.add_paragraph()
        set_font(par.add_run(f"{n}. {first.lstrip('# ').strip()}"), size=10.5)

    for n, src in enumerate(SOURCES, 1):
        doc.add_page_break()
        heading = doc.add_paragraph(style="Heading 1")
        first = next((ln for ln in src.read_text(encoding="utf-8").splitlines()
                      if ln.startswith("# ")), src.stem)
        run = heading.add_run(f"第{n}部分　{first.lstrip('# ').strip()}")
        set_font(run, size=18, bold=True, color=(0x1F, 0x4E, 0x79))
        # 跳过原文一级标题，避免重复
        text = src.read_text(encoding="utf-8")
        text = re.sub(r"^# .*\n", "", text, count=1)
        tmp = doc._body
        # 用临时文件流式转换剩余内容
        tmp_md = OUT.parent / f".{src.stem}.tmp.md"
        tmp_md.write_text(text, encoding="utf-8")
        convert(doc, tmp_md)
        tmp_md.unlink()

    doc.save(OUT)
    print(f"[OK] {OUT}  ({OUT.stat().st_size/1024:.0f} KB)")


if __name__ == "__main__":
    build()
