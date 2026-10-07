"""Runs inside Krita (kritarunner -s testy_krita_export -f main <in> <out> <report>).

`krita --export` writes the file as soon as the document is loaded, while fill and
vector layers are still being rendered by Krita's update scheduler, so the same PSD
came out drawn on one run and blank on the next. This opens the document, waits for
the image to settle, and only then exports. The verdict goes to the report file,
because kritarunner's exit code does not follow the script's.

kritarunner finds this module through the working directory (drivers/krita.py starts
it here); PYTHONPATH is replaced by Krita's own.
"""

from krita import InfoObject, Krita


def _settle(document):
    """Wait until the composited image stops changing. waitForDone covers the update
    scheduler; text shapes and fill layers can still land a moment later (the first
    of three exports of one file differed), so the projection is polled until two
    consecutive reads match, for at most about six seconds."""
    from PyQt5.QtCore import QThread
    from PyQt5.QtWidgets import QApplication

    previous = None
    for _attempt in range(20):
        document.waitForDone()
        document.refreshProjection()
        document.waitForDone()
        QApplication.processEvents()
        current = bytes(document.pixelData(0, 0, document.width(), document.height()))
        if previous is not None and current == previous:
            return
        previous = current
        QThread.msleep(300)


def main(arguments):
    source, output, report = arguments[0], arguments[1], arguments[2]
    verdict = "failed: did not finish"
    try:
        application = Krita.instance()
        application.setBatchmode(True)
        document = application.openDocument(source)
        if document is None:
            verdict = "failed: open"
        else:
            document.setBatchmode(True)
            _settle(document)
            exported = document.exportImage(output, InfoObject())
            verdict = "ok" if exported else "failed: export"
            document.close()
    except Exception as error:  # the report is the only way out of this process
        verdict = f"failed: {error}"
    with open(report, "w", encoding="utf-8") as handle:
        handle.write(verdict)
