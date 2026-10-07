// Run by Testy through `patchy.exe --run-script` (drivers/patchy.py render_text_afresh):
// make Patchy draw every type layer and embedded smart object itself, then export.
//
// Like Photoshop, Patchy shows the pixels saved in the file for a type layer or a
// smart object until the layer is edited. layer.rerenderText() and
// layer.rerenderSmartObject() replace them with Patchy's own render and change nothing
// else. The layer names reached are printed as one JSON line.
const testyDocument = app.activeDocument;
const testyDone = [];
const testyFailed = [];
const testySmartDone = [];
const testySmartFailed = [];
const testySources = {};
function testyWalk(layers) {
  for (const layer of layers) {
    if (layer.isGroup) {
      testyWalk(layer.children);
      continue;
    }
    if (layer.isText) {
      // text=0: a font this text needs is missing on this machine, so nobody's own
      // text render is scored and the pixels saved in the file stay.
      if (patchy.args.text === "0") continue;
      try {
        layer.rerenderText();
        testyDone.push(layer.name);
      } catch (error) {
        testyFailed.push(layer.name);
      }
    } else if (layer.isSmartObject) {
      const state = layer.getSmartObject();
      // A linked smart object has nothing embedded to render from: not this leg's business.
      if (!state || state.linked) continue;
      // One call re-renders every layer sharing the source.
      if (testySources[state.sourceId] === undefined) {
        try {
          testySources[state.sourceId] = layer.rerenderSmartObject() > 0;
        } catch (error) {
          testySources[state.sourceId] = false;
        }
      }
      (testySources[state.sourceId] ? testySmartDone : testySmartFailed).push(layer.name);
    }
  }
}
testyWalk(testyDocument.layers);
const testyExported = testyDocument.exportAs(patchy.args.out);
console.log(JSON.stringify({testyTextAfresh: true, exported: testyExported, done: testyDone, failed: testyFailed,
                            smartDone: testySmartDone, smartFailed: testySmartFailed}));
