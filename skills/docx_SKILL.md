---
name: docx
description: "Use this skill whenever the user wants to create, read, edit, or manipulate Word documents (.docx files). Triggers include: any mention of 'Word doc', 'word document', '.docx', or requests to produce professional documents with formatting like tables of contents, headings, page numbers, or letterheads. Also use when extracting or reorganizing content from .docx files, inserting or replacing images in documents, performing find-and-replace in Word files, working with tracked changes or comments, or converting content into a polished Word document. If the user asks for a 'report', 'memo', 'letter', 'template', or similar deliverable as a Word or .docx file, use this skill. Do NOT use for PDFs, spreadsheets, Google Docs, or general coding tasks unrelated to document generation."
license: Proprietary. LICENSE.txt has complete terms
---

# DOCX Creation and Manipulation with Python

This guide provides instructions and examples for creating and modifying Word documents (.docx) using Python. The primary library is `python-docx`.

## Quick Reference

| Task | Approach |
|------|----------|
| Create new document | Write a Python script using `python-docx` |
| Read/analyze content | Use `python-docx` to extract paragraphs or tables |

---

## Creating New Documents

### 1. Document Setup, Page Size, and Margins

Always configure page dimensions and margins explicitly. 

```python
from docx import Document
from docx.shared import Inches, Pt, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.table import WD_TABLE_ALIGNMENT, WD_ALIGN_VERTICAL
from docx.oxml import OxmlElement
from docx.oxml.ns import qn, nsdecls
from docx.oxml import parse_xml

doc = Document()

# Configure first section margins and size (US Letter: 8.5 x 11.0 in)
section = doc.sections[0]
section.page_width = Inches(8.5)
section.page_height = Inches(11.0)
section.top_margin = Inches(1.0)
section.bottom_margin = Inches(1.0)
section.left_margin = Inches(1.0)
section.right_margin = Inches(1.0)
```

### 2. Styles, Headings, and Typography

Use standard fonts like Arial or Times New Roman. Customize heading properties to give a clean layout.

```python
# Add standard title
title = doc.add_paragraph()
title.alignment = WD_ALIGN_PARAGRAPH.CENTER
run = title.add_run("ANNUAL FINANCIAL REPORT")
run.font.name = "Arial"
run.font.size = Pt(24)
run.font.bold = True
run.font.color.rgb = RGBColor(0x1E, 0x27, 0x61) # Deep navy
title.paragraph_format.space_after = Pt(24)

# Add Heading 1
h1 = doc.add_heading(level=1)
h1.paragraph_format.space_before = Pt(18)
h1.paragraph_format.space_after = Pt(8)
run = h1.add_run("1. Executive Summary")
run.font.name = "Arial"
run.font.size = Pt(16)
run.font.bold = True
run.font.color.rgb = RGBColor(0x1E, 0x27, 0x61)
```

### 3. Adding Paragraphs & Text Styling

```python
p = doc.add_paragraph()
p.paragraph_format.line_spacing = 1.15
p.paragraph_format.space_after = Pt(8)

run = p.add_run("This is the main body text in a Word document. ")
run.font.name = "Arial"
run.font.size = Pt(11)

bold_run = p.add_run("Important figures should be bolded.")
bold_run.font.name = "Arial"
bold_run.font.size = Pt(11)
bold_run.bold = True
```

### 4. Creating Lists

Do NOT write raw bullet characters (like `•` or `-`) in text. Instead, use standard Word styles.

```python
# Bullet point list
p1 = doc.add_paragraph(style='List Bullet')
run = p1.add_run("First key point to highlight")
run.font.name = "Arial"
run.font.size = Pt(11)

p2 = doc.add_paragraph(style='List Bullet')
run = p2.add_run("Second key point to highlight")
run.font.name = "Arial"
run.font.size = Pt(11)

# Numbered list
p3 = doc.add_paragraph(style='List Number')
run = p3.add_run("First step in the sequence")
run.font.name = "Arial"
run.font.size = Pt(11)
```

### 5. Professional Table Formatting (Shading, Margins, Borders)

In `python-docx`, advanced table styles like cell backgrounds, cell padding, and custom borders require directly manipulating the underlying XML using helper functions.

```python
# Create Table
table = doc.add_table(rows=3, cols=3)
table.alignment = WD_TABLE_ALIGNMENT.CENTER

# Set cell widths explicitly (dual-width rule: sum of column widths matches table width)
col_widths = [Inches(2.0), Inches(2.5), Inches(2.0)]
for row in table.rows:
    for idx, cell in enumerate(row.cells):
        cell.width = col_widths[idx]

# 1. Shading Helper
def set_cell_background(cell, color_hex):
    """Set background color of a cell using hex string (e.g. 'D5E8F0')"""
    tcPr = cell._tc.get_or_add_tcPr()
    shd = parse_xml(f'<w:shd {nsdecls("w")} w:fill="{color_hex}"/>')
    tcPr.append(shd)

# 2. Padding/Margin Helper
def set_cell_margins(cell, top=100, bottom=100, left=150, right=150):
    """Set padding in twentieths of a point (dxa) - 1 pt = 20 dxa"""
    tcPr = cell._tc.get_or_add_tcPr()
    tcMar = OxmlElement('w:tcMar')
    for m_name, m_val in [('top', top), ('bottom', bottom), ('left', left), ('right', right)]:
        node = OxmlElement(f'w:{m_name}')
        node.set(qn('w:w'), str(m_val))
        node.set(qn('w:type'), 'dxa')
        tcMar.append(node)
    tcPr.append(tcMar)

# 3. Borders Helper
def set_cell_borders(cell, **kwargs):
    """
    Configure borders. kwargs: top, bottom, left, right
    Example value: {'sz': 4, 'val': 'single', 'color': 'CCCCCC'}
    """
    tcPr = cell._tc.get_or_add_tcPr()
    tcBorders = OxmlElement('w:tcBorders')
    for edge, border_args in kwargs.items():
        border = OxmlElement(f'w:{edge}')
        for key, val in border_args.items():
            border.set(qn(f'w:{key}'), str(val))
        tcBorders.append(border)
    tcPr.append(tcBorders)

# Style Header Row
header_cells = table.rows[0].cells
headers = ["Item", "Description", "Value"]
for i, cell in enumerate(header_cells):
    cell.text = headers[i]
    set_cell_background(cell, "1E2761") # Dark Navy Header
    set_cell_margins(cell, top=120, bottom=120, left=150, right=150)
    
    # Style text inside cell
    p = cell.paragraphs[0]
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = p.runs[0]
    run.font.name = "Arial"
    run.font.size = Pt(11)
    run.font.bold = True
    run.font.color.rgb = RGBColor(0xFF, 0xFF, 0xFF)

# Style Data Rows
border_config = {'sz': 4, 'val': 'single', 'color': 'CCCCCC'}
for row_idx, row in enumerate(table.rows[1:]):
    for cell in row.cells:
        set_cell_margins(cell, top=100, bottom=100, left=150, right=150)
        set_cell_borders(cell, bottom=border_config, top=border_config)
```

### 6. Paragraph Borders (Horizontal Lines/Dividers)

To divide sections cleanly, use a bottom border on a paragraph instead of using thin tables.

```python
def add_paragraph_bottom_border(paragraph, color_hex="CCCCCC", size=12):
    """Add a bottom rule border under a paragraph. size=12 is 1.5pt rule"""
    pPr = paragraph._p.get_or_add_pPr()
    pBdr = OxmlElement('w:pBdr')
    bottom = OxmlElement('w:bottom')
    bottom.set(qn('w:val'), 'single')
    bottom.set(qn('w:sz'), str(size))
    bottom.set(qn('w:space'), '4')
    bottom.set(qn('w:color'), color_hex)
    pBdr.append(bottom)
    pPr.append(pBdr)

# Example usage
p = doc.add_paragraph()
add_paragraph_bottom_border(p, "1E2761", 12)
```

### 7. Adding Images and Page Breaks

```python
# Add Image
doc.add_picture("chart.png", width=Inches(4.5))

# Add Page Break
doc.add_page_break()
```

---

## Critical Rules to Prevent Failures

- **No Javascript packages**: Never attempt to write or use `npm install docx` or node packages. Everything must run natively inside the Python execution context.
- **Never use `\n` in heading text**: Add runs or distinct paragraph elements instead.
- **Do not hardcode cell shading directly**: Shading is not an attribute of `Cell` objects in `python-docx`. Always use `w:shd` XML injection helpers shown above.
- **Use local paths**: Always save generated files to the current working directory (e.g. `report.docx`).
- **Sum column widths**: Ensure that cell widths in the table sum up exactly to the total table width to avoid layout issues.
