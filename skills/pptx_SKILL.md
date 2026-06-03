---
name: pptx
description: "Use this skill any time a .pptx file is involved in any way — as input, output, or both. This includes: creating slide decks, pitch decks, or presentations; reading, parsing, or extracting text from any .pptx file (even if the extracted content will be used elsewhere, like in an email or summary); editing, modifying, or updating existing presentations; combining or splitting slide files; working with templates, layouts, speaker notes, or comments. Trigger whenever the user mentions \"deck,\" \"slides,\" \"presentation,\" or references a .pptx filename, regardless of what they plan to do with the content afterward. If a .pptx file needs to be opened, created, or touched, use this skill."
license: Proprietary. LICENSE.txt has complete terms
---

# PPTX Slide Deck Generation with Python

This guide provides instructions and examples for creating and modifying PowerPoint (.pptx) presentations using Python. The primary library to use is `python-pptx`.

## Quick Reference

| Task | Guide |
|------|-------|
| Create a presentation | Write a Python script using `python-pptx` |
| Read/analyze content | Use `python-pptx` to extract text |

---

## Creating Presentations from Scratch

When creating new presentations, use the blank layout (index 6) for complete custom control over element placement and styling.

### 1. Presentation Setup and 16:9 Widescreen

Always configure widescreen 16:9 format explicitly.

```python
from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN
from pptx.enum.shapes import MSO_SHAPE
from pptx.enum.text import MSO_ANCHOR

prs = Presentation()
# Set widescreen dimensions
prs.slide_width = Inches(13.333)
prs.slide_height = Inches(7.5)

# Use layout 6 (Blank Slide)
blank_layout = prs.slide_layouts[6]
```

### 2. Styling the Slide Background

```python
slide = prs.slides.add_slide(blank_layout)

# Apply solid background color
background = slide.background
fill = background.fill
fill.solid()
fill.fore_color.rgb = RGBColor(0x1E, 0x27, 0x61) # Deep navy background
```

### 3. Adding Content Cards / Shapes

Use shapes to create layout compartments or background blocks (e.g., cards).

```python
# Add a rectangular card
shape = slide.shapes.add_shape(
    MSO_SHAPE.RECTANGLE, 
    Inches(1.0), Inches(1.5), Inches(5.0), Inches(4.5)
)
shape.fill.solid()
shape.fill.fore_color.rgb = RGBColor(0xFF, 0xFF, 0xFF) # White card

# Clear borders or set custom border color
shape.line.color.rgb = RGBColor(0xCA, 0xDC, 0xFC) # Light border
shape.line.width = Pt(1.5)
```

### 4. Styling Text Boxes and Paragraphs

Always set `word_wrap = True` and zero out margins to align text perfectly with shapes.

```python
# Add textbox inside or next to shapes
txBox = slide.shapes.add_textbox(Inches(1.2), Inches(1.7), Inches(4.6), Inches(4.1))
tf = txBox.text_frame
tf.word_wrap = True
tf.vertical_anchor = MSO_ANCHOR.TOP

# Set margins to 0 for precise alignment
tf.margin_left = Inches(0)
tf.margin_right = Inches(0)
tf.margin_top = Inches(0)
tf.margin_bottom = Inches(0)

# Configure paragraphs
p = tf.paragraphs[0]
p.text = "The Modern Landscape"
p.font.name = "Arial"
p.font.size = Pt(24)
p.font.bold = True
p.font.color.rgb = RGBColor(0x1E, 0x27, 0x61)
p.space_after = Pt(12)

p2 = tf.add_paragraph()
p2.text = "This is a brief description of the modern landscape in architecture."
p2.font.name = "Arial"
p2.font.size = Pt(14)
p2.font.color.rgb = RGBColor(0x55, 0x55, 0x55)
p2.space_after = Pt(10)
```

### 5. Drawing Connectors / Lines

Use lines or connectors for headers, footers, timelines, or grids.

> [!IMPORTANT]
> **Line Color Rule**: Never call `line.fill.fore_color.rgb` on connectors or lines! It will throw an exception. Always use `.line.color.rgb = RGBColor(...)` instead.

```python
# Add a straight connector line
# 3 is the value for STRAIGHT connector
connector = slide.shapes.add_connector(3, Inches(1.0), Inches(1.2), Inches(12.333), Inches(1.2))
connector.line.color.rgb = RGBColor(0xCA, 0xDC, 0xFC)
connector.line.width = Pt(2.0)
```

### 6. Inserting Images

```python
# Insert a picture in a designated area
slide.shapes.add_picture("car_model.png", Inches(7.0), Inches(1.5), Inches(5.333), Inches(4.5))
```

---

## Design and Typography Guidelines

### Curated Color Palettes

| Theme | Primary | Secondary | Accent |
|-------|---------|-----------|--------|
| **Midnight Executive** | `1E2761` (navy) | `CADCFC` (ice blue) | `FFFFFF` (white) |
| **Forest & Moss** | `2C5F2D` (forest) | `97BC62` (moss) | `F5F5F5` (cream) |
| **Coral Energy** | `F96167` (coral) | `F9E795` (gold) | `2F3C7E` (navy) |
| **Warm Terracotta** | `B85042` (terracotta) | `E7E8D1` (sand) | `A7BEAE` (sage) |
| **Ocean Gradient** | `065A82` (deep blue) | `1C7293` (teal) | `21295C` (midnight) |

### Font Combinations

- **Georgia** (Header) / **Calibri** (Body)
- **Arial Black** (Header) / **Arial** (Body)
- **Cambria** (Header) / **Calibri** (Body)

### Element Sizes

- Slide Title: 36-44pt bold
- Section Header: 20-24pt bold
- Body Text: 14-16pt
- Captions: 10-12pt muted

---

## Critical Rules to Prevent Failures

- **Never call `.fit_text()`**: This method does not exist in `python-pptx`. Calculate layouts manually using dimensions.
- **Never call `line.fill.fore_color`**: Lines do not have a solid fill. Use `line.color.rgb = RGBColor(r, g, b)`.
- **Always Import RGBColor**: Always include `from pptx.dml.color import RGBColor`.
- **Enable Word Wrap**: Always set `text_frame.word_wrap = True` so text doesn't clip or overflow horizontally.
- **Use Local File Paths**: Never save files using absolute paths. Save them directly in the current directory (e.g. `presentation.pptx`).
- **No Unicode Bullet Characters**: Let python-pptx or styling handle bullet layout; don't write custom Unicode characters like `•` manually in text frames unless formatting as a plain string.
