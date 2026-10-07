// @name Patchy wordmark studies
// @description Builds patchy-wordmark-studies.psd: six logo + wordmark treatments, each shown
// @description with the flat logo (left column) and the folded-paper logo (right column).
// @description Every logo is a LINKED smart object that references an SVG beside the PSD,
// @description so editing an SVG and running Update Smart Object Content refreshes the board.
// @window
// @cli --headless --script-arg folder=D:/path/to/branding/
//
// Run: patchy --headless --run-script patchy-wordmark-studies.js --script-arg folder=<this folder>
// Optional: --script-arg preview=<file.png> also writes a flattened preview there.
// The folder must hold the flat and folded variants of the tile and both marks:
// patchy-logo-{flat,folded}.svg, patchy-mark-yellow{,-folded}.svg, patchy-mark-outline{,-folded}.svg.

var OPTIONS = {
  output: "patchy-wordmark-studies.psd",
  scale: 3,                                    // layout units -> document pixels
  background: "#DFDDD8",
  dark: "#363F3B",
  light: "#FFFFFF",
  labelColor: "#9B9994",
  titleColor: "#55595A",
  wordmarkFont: "Bahnschrift Bold",            // heavy, slightly industrial sans
  condensedFont: "Bahnschrift Bold Condensed",
  italicFont: "Franklin Gothic Heavy",
  labelFont: "Segoe UI"
};

// One column per logo variant; every study is drawn once in each.
var VARIANTS = [
  {name: "flat", label: "FLAT", left: 0,
   tile: "patchy-logo-flat.svg", yellow: "patchy-mark-yellow.svg", outline: "patchy-mark-outline.svg"},
  {name: "folded", label: "FOLDED", left: 528,
   tile: "patchy-logo-folded.svg", yellow: "patchy-mark-yellow-folded.svg",
   outline: "patchy-mark-outline-folded.svg"}
];
var FIRST_ROW = 81;   // top of the first study row
var ROW_PITCH = 210;

var folder = String(patchy.args.folder || "").split("\\").join("/");
if (!folder) throw new Error("Pass --script-arg folder=<folder holding the logo SVGs>");
if (folder.charAt(folder.length - 1) !== "/") folder += "/";

var S = OPTIONS.scale;
var doc = app.newDocument(1052 * S, (FIRST_ROW + 6 * ROW_PITCH + 4) * S);
var starting = doc.layers.slice();

// --- helpers (layout units in, document pixels out) ---------------------------------

// The logo SVG's tile fills its canvas: place it so the TILE
// lands at (x, y) with the given size.
function placeTile(file, name, x, y, size) {
  return doc.addSmartObject(folder + file, {
    linked: true, name: name,
    x: Math.round(x * S), y: Math.round(y * S), width: Math.round(size * S)
  });
}

// The mark SVGs are a 688 x 664 canvas whose letter is 672 x 648, padded by 8.
function placeMark(file, name, x, y, height) {
  var k = height * S / 648;
  return doc.addSmartObject(folder + file, {
    linked: true, name: name,
    x: Math.round(x * S - 8 * k), y: Math.round(y * S - 8 * k), width: Math.round(688 * k)
  });
}

// Where a layer's visible pixels are, in document pixels. A text layer's `bounds` is its
// line box (ascent to descent), which is taller than the letters.
function inkBox(layer) {
  var block = layer.getPixels();
  var data = new Uint8Array(block.data);
  var minX = block.width, minY = block.height, maxX = -1, maxY = -1;
  for (var row = 0; row < block.height; ++row) {
    var offset = row * block.width * 4 + 3;
    for (var column = 0; column < block.width; ++column, offset += 4) {
      if (data[offset] >= 128) {
        if (column < minX) minX = column;
        if (column > maxX) maxX = column;
        if (row < minY) minY = row;
        if (row > maxY) maxY = row;
      }
    }
  }
  if (maxX < 0) throw new Error("No visible pixels in " + layer.name);
  return {x: block.x + minX, y: block.y + minY, width: maxX - minX + 1, height: maxY - minY + 1};
}

// Text sized by its INK height (the cap height of "PATCHY"), with the ink's top-left
// corner at (x, top); anchor "center" centres the ink on x instead. The layer is created,
// measured, then moved into place: a script move carries the text anchor along.
function caps(name, text, format, x, top, inkHeight, anchor) {
  var target = inkHeight * S;
  function make(size) {
    return doc.addTextLayer(text, {
      font: format.font, bold: !!format.bold, italic: !!format.italic, color: format.color,
      size: size, x: Math.round(size), y: Math.round(size * 2)
    });
  }
  var trial = make(200);
  var size = 200 * target / inkBox(trial).height;
  trial.remove();
  var layer = make(size);
  var ink = inkBox(layer);
  var left = anchor === "center" ? x * S - ink.width / 2 : x * S;
  layer.moveTo(layer.x + Math.round(left - ink.x), layer.y + Math.round(top * S - ink.y));
  layer.name = name;
  return layer;
}

function label(text, x, top) {
  return caps(text, text, {font: OPTIONS.labelFont, color: OPTIONS.labelColor}, x, top, 7.5);
}

// --- the studies --------------------------------------------------------------------
// Each builder draws one study for one variant `v`, with (x, y) the top-left corner of
// its cell, and returns the layers it made.

var heavy = {font: OPTIONS.wordmarkFont, color: OPTIONS.dark};

var STUDIES = [
  {id: "W1", name: "tile + wordmark", build: function (v, x, y) {
    return [
      placeTile(v.tile, "Logo", x + 53, y + 10, 134),
      caps("PATCHY", "PATCHY", heavy, x + 207, y + 52, 54)
    ];
  }},
  {id: "W2", name: "mark + wordmark", build: function (v, x, y) {
    return [
      placeMark(v.yellow, "Mark", x + 52, y + 33, 98),
      caps("PATCHY", "PATCHY", heavy, x + 175, y + 50, 66)
    ];
  }},
  {id: "W3", name: "stacked", build: function (v, x, y) {
    return [
      placeTile(v.tile, "Logo", x + 189, y - 9, 141),
      caps("PATCHY", "PATCHY", heavy, x + 259.5, y + 148, 35, "center")
    ];
  }},
  {id: "W4", name: "outlined mark + condensed", build: function (v, x, y) {
    return [
      placeMark(v.outline, "Mark", x + 53, y + 34, 107),
      caps("PATCHY", "PATCHY", {font: OPTIONS.condensedFont, color: OPTIONS.dark}, x + 194, y + 55, 72)
    ];
  }},
  {id: "W5", name: "on dark", build: function (v, x, y) {
    return [
      doc.addShape("Panel", {type: "roundedRectangle", x: (x + 31) * S, y: (y + 22) * S,
                             width: 476 * S, height: 150 * S, radius: 14 * S}, {fill: OPTIONS.dark}),
      placeTile(v.tile, "Logo", x + 64, y + 44, 106),
      caps("PATCHY", "PATCHY", {font: OPTIONS.wordmarkFont, color: OPTIONS.light}, x + 190, y + 67, 60)
    ];
  }},
  {id: "W6", name: "mark + italic", build: function (v, x, y) {
    return [
      placeMark(v.yellow, "Mark", x + 52, y + 39, 97),
      caps("PATCHY", "PATCHY", {font: OPTIONS.italicFont, italic: true, color: OPTIONS.dark},
           x + 169, y + 58, 60)
    ];
  }}
];

// --- the board ----------------------------------------------------------------------

doc.addFillLayer("Background", OPTIONS.background);
caps("Title", "P A T C H Y   /   W O R D M A R K   S T U D I E S",
     {font: OPTIONS.labelFont, color: OPTIONS.titleColor}, 526, 25, 9, "center");

STUDIES.forEach(function (entry, row) {
  var y = FIRST_ROW + row * ROW_PITCH;
  VARIANTS.forEach(function (v) {
    var tag = entry.id + "  " + v.label;
    var layers = [label(tag, v.left + 25, y)].concat(entry.build(v, v.left, y));
    doc.groupLayers(layers, entry.id + " " + v.name + ": " + entry.name);
  });
});

// Whatever newDocument started with (an empty layer) is not part of the board.
starting.forEach(function (layer) { layer.remove(); });

var output = folder + OPTIONS.output;
if (!doc.saveAs(output)) throw new Error("Could not save " + output);
if (patchy.args.preview) {
  doc.renderPreview(String(patchy.args.preview).split("\\").join("/"), {maxWidth: 2104, maxHeight: 2690});
}

var links = [];
function collect(layers) {
  layers.forEach(function (layer) {
    if (layer.isSmartObject) {
      var state = layer.getSmartObject();
      links.push({name: layer.name, linked: state.linked, relativePath: state.relativePath,
                  sourceId: state.sourceId, missing: state.missing});
    }
    if (layer.isGroup) collect(layer.children);
  });
}
collect(doc.layers);
console.log("saved " + output + " " + doc.width + "x" + doc.height);
console.log(JSON.stringify(links));
patchy.setResult({path: output, links: links});
