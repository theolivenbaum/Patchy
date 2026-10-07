"""Export the checked-in folded SVGs to platform icons.

Development only: python -m pip install PySide6-Essentials==6.8.3
Run from any directory. No application windows or platform icon tools are used.
"""

from pathlib import Path
import struct

from PySide6.QtCore import QByteArray, QBuffer, QIODevice, QRectF, Qt
from PySide6.QtGui import QImage, QPainter
from PySide6.QtSvg import QSvgRenderer


ROOT = Path(__file__).resolve().parents[2]
BRANDING = ROOT / "packaging/branding"
WEB = ROOT / "packaging/web/icons"


# Apple's icon grid: the tile is the middle 824 of a 1024 canvas.
MAC_TILE = 824 / 1024


def png(source, size, fill=1.0):
    renderer = QSvgRenderer(str(source))
    if not renderer.isValid():
        raise ValueError(f"Invalid SVG: {source}")
    # Oversampling preserves the folded edges at taskbar and favicon sizes.
    image = QImage(size * 4, size * 4, QImage.Format_ARGB32_Premultiplied)
    image.fill(Qt.transparent)
    painter = QPainter(image)
    side = size * 4 * fill
    origin = (size * 4 - side) / 2
    renderer.render(painter, QRectF(origin, origin, side, side))
    painter.end()
    image = image.scaled(size, size, Qt.IgnoreAspectRatio, Qt.SmoothTransformation)
    data = QByteArray()
    buffer = QBuffer(data)
    buffer.open(QIODevice.WriteOnly)
    if not image.save(buffer, "PNG"):
        raise RuntimeError("PNG encoding failed")
    return bytes(data)


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    print(path.relative_to(ROOT))


def main():
    logo = BRANDING / "patchy-logo-folded.svg"
    full_bleed = BRANDING / "patchy-icon-ios-folded.svg"
    sizes = (16, 24, 32, 48, 64, 128, 256, 512, 1024)
    images = {size: png(logo, size) for size in sizes}
    ico_sizes = tuple(size for size in sizes if size <= 256)
    offset = 6 + 16 * len(ico_sizes)
    directory = bytearray(struct.pack("<HHH", 0, 1, len(ico_sizes)))
    for size in ico_sizes:
        data = images[size]
        directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    write(ROOT / "src/app/patchy.ico", directory + b"".join(images[size] for size in ico_sizes))

    types = {"icp4": 16, "icp5": 32, "icp6": 64, "ic07": 128, "ic08": 256,
             "ic09": 512, "ic10": 1024, "ic11": 32, "ic12": 64, "ic13": 256, "ic14": 512}
    # Modern PNG-backed ICNS entries, including Retina representations. The logo
    # fills its canvas, so macOS alone gets the margin its Dock icons carry.
    mac = {size: png(logo, size, MAC_TILE) for size in set(types.values())}
    chunks = b"".join(key.encode("ascii") + struct.pack(">I", len(mac[size]) + 8) + mac[size]
                      for key, size in types.items())
    write(ROOT / "packaging/macos/patchy.icns", b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)
    for size in (16, 32, 48, 64, 128, 256, 512):
        write(ROOT / f"packaging/linux/icons/hicolor/{size}x{size}/apps/com.rtsoft.patchy.png", images[size])
    for size in (180, 192, 512):
        # The OS/browser supplies its own mask. Keep this background opaque.
        name = "apple-touch-icon.png" if size == 180 else f"patchy-{size}.png"
        write(WEB / name, png(full_bleed, size))


if __name__ == "__main__":
    main()
