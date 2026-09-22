"""Render Proje-Raporu-kaynak.html to Proje-Raporu.pdf, table of contents included.

The page numbers in the table of contents cannot be worked out from the HTML:
they depend on where the browser decides to break pages, which depends on the
rendered height of every figure, table and paragraph above. So the report is
rendered twice. The first pass leaves the table of contents empty and exists
only to be measured; the second pass gets the real numbers.

Reading the numbers back out of the first PDF, rather than estimating them from
element offsets, is what keeps them right. An estimate would have to model
`page-break-inside: avoid` on tables and `page-break-before: always` on
sections, and would silently drift as the report grows.

    python docs/build-rapor.py
"""

import html
import re
import subprocess
import sys
from pathlib import Path

DOCS = Path(__file__).resolve().parent
SOURCE = DOCS / "Proje-Raporu-kaynak.html"
OUTPUT = DOCS / "Proje-Raporu.pdf"
PLACEHOLDER = "<!--TOC-->"

CHROME = Path(r"C:\Program Files\Google\Chrome\Application\chrome.exe")
EDGE = Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")

# The cover carries `page-break-after: always` and so does the contents page,
# so body sections start on the third sheet. Headings are searched from there.
FIRST_CONTENT_PAGE = 2  # zero-based


def browser() -> Path:
    for candidate in (CHROME, EDGE):
        if candidate.exists():
            return candidate
    sys.exit("Neither Chrome nor Edge was found; cannot render the PDF.")


def render(html_text: str, pdf_path: Path) -> None:
    """Print `html_text` to `pdf_path` through headless Chrome/Edge."""
    scratch = DOCS / "_rapor-render.html"
    scratch.write_text(html_text, encoding="utf-8")
    try:
        subprocess.run(
            [
                str(browser()),
                "--headless=new",
                "--disable-gpu",
                "--no-pdf-header-footer",
                "--run-all-compositor-stages-before-draw",
                "--virtual-time-budget=10000",
                f"--print-to-pdf={pdf_path}",
                scratch.resolve().as_uri(),
            ],
            check=True,
            capture_output=True,
            timeout=180,
        )
    finally:
        scratch.unlink(missing_ok=True)


def headings(html_text: str):
    """Yield (level, number, title) for every numbered section, in order.

    h2 carries its number in a <span class="num">; h3 starts with a "8.1" style
    number in its text. The contents page's own h2 has an empty span and is
    skipped - it is not a section of the report.
    """
    pattern = re.compile(
        r"<(h2|h3)\b[^>]*>(.*?)</\1>", re.DOTALL | re.IGNORECASE
    )
    for match in pattern.finditer(html_text):
        level, inner = match.group(1).lower(), match.group(2)
        span = re.search(
            r'<span class="num">(.*?)</span>', inner, re.DOTALL
        )
        if level == "h2":
            if not span or not span.group(1).strip():
                continue  # the contents page heading itself
            number = span.group(1).strip()
            title = re.sub(r"<[^>]+>", "", inner[span.end():])
        else:
            title = re.sub(r"<[^>]+>", "", inner)
            number_match = re.match(r"\s*(\d+\.\d+)\s+", title)
            if not number_match:
                continue  # an unnumbered sub-heading is not a contents entry
            number = number_match.group(1)
            title = title[number_match.end():]
        title = html.unescape(title).strip()
        if title:
            yield level, number, title


def page_numbers(pdf_path: Path, entries):
    """Map each heading to the printed page it landed on.

    Headings are matched in document order and each search starts where the
    previous one was found, so a title that also occurs in running text cannot
    pull an entry backwards.
    """
    import pymupdf

    doc = pymupdf.open(pdf_path)
    found, cursor = [], FIRST_CONTENT_PAGE
    for level, number, title in entries:
        needle = title if len(title) <= 40 else title[:40]
        page = None
        for index in range(cursor, doc.page_count):
            if doc[index].search_for(needle, quads=False):
                page = index
                break
        if page is None:
            print(f"  uyarı: '{number} {title}' PDF'te bulunamadı", file=sys.stderr)
            page = cursor
        cursor = page
        found.append((level, number, title, page + 1))  # printed pages are 1-based
    doc.close()
    return found


def toc_html(entries) -> str:
    rows = ['<ul class="toc">']
    for level, number, title, page in entries:
        css = ' class="sub"' if level == "h3" else ""
        rows.append(
            f'  <li{css}><span class="n">{html.escape(number)}</span>'
            f'<span class="t">{html.escape(title)}</span>'
            f'<span class="p">{page}</span></li>'
        )
    rows.append("</ul>")
    return "\n".join(rows)


def main() -> None:
    # Windows hands Python a cp1252 stdout, which cannot encode "başlık" and
    # kills the build on a progress message. Same trap the scanner hit: the
    # console encoding is not the file encoding.
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

    source = SOURCE.read_text(encoding="utf-8")
    if PLACEHOLDER not in source:
        sys.exit(f"{SOURCE.name} içinde {PLACEHOLDER} yer tutucusu yok.")

    entries = list(headings(source))
    print(f"{len(entries)} başlık bulundu.")

    probe = DOCS / "_rapor-olcum.pdf"
    print("1/2 ölçüm baskısı...")
    render(source, probe)

    print("2/2 sayfa numaraları okunuyor ve son baskı alınıyor...")
    numbered = page_numbers(probe, entries)
    probe.unlink(missing_ok=True)

    render(source.replace(PLACEHOLDER, toc_html(numbered), 1), OUTPUT)

    import pymupdf

    doc = pymupdf.open(OUTPUT)
    pages = doc.page_count
    doc.close()
    print(f"Bitti: {OUTPUT.name}, {pages} sayfa.")
    for level, number, title, page in numbered:
        if level == "h2":
            print(f"  {number:>3}  {title:<46} s.{page}")


if __name__ == "__main__":
    main()
