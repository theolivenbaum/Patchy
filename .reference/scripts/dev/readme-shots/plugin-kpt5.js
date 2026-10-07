// @name README shot: Kai's Power Tools 5
// @description Captures Patchy and the real framed KPT window separately for composition.
// @cli --script-arg out=plugin_kpt5.png --script-arg plugins=<local KPT folder> --script-arg photo=<photo>
// The driver supplies an isolated copy of KPT, bounded window settings, and the
// existing targeted acceptance click. No desktop capture or global input is used.
var doc = app.open(patchy.args.photo);
if (!doc) throw new Error('Could not open KPT source image');
doc.resizeImage(1100, Math.round(doc.height * 1100 / doc.width));
doc.activeLayer.name = 'KPT source photograph';
patchy.ui.setWindowSize(1600, 1000);
patchy.ui.setSidePanelWidth(300);
app.documents.forEach(function (other) {
  if (other.id !== doc.id && !other.path && !other.modified) other.close();
});
doc.activate();
patchy.plugins.folders = [patchy.args.plugins];
var matches = patchy.plugins.list().filter(function (p) {
  return p.supported && p.name.replace(/\s+/g, ' ').indexOf(patchy.args.effect || 'Orb-It') >= 0;
});
if (!matches.length) throw new Error('KPT filter not found');
// Use the isolated fixture rather than an installed copy found by automatic scan.
var plugin = matches.filter(function (p) {
  return p.path.replace(/\\/g, '/').toLowerCase().indexOf(patchy.args.plugins.replace(/\\/g, '/').toLowerCase()) === 0;
})[0];
if (!plugin) throw new Error('KPT filter missing from isolated fixture: ' + JSON.stringify(matches));
setTimeout(function () {
  patchy.ui.fitOnScreen();
  patchy.ui.setStatusMessage('Ready');
  if (!patchy.ui.captureWindow(patchy.args.out + '.base.png')) throw new Error('Patchy capture failed');
  doc.activeLayer.applyPlugin(plugin.id, {dialog: true, captureDialog: patchy.args.out + '.dialog.png'});
  if (!patchy.io.fileExists(patchy.args.out + '.dialog.png')) throw new Error('KPT did not produce a window capture');
  console.log('Captured ' + plugin.name);
}, 250);
