# Acceptance check for Patchy-written linked smart objects: opens each PSD in Adobe
# Photoshop over COM with error dialogs enabled (a watcher logs every PSDialogBox that
# appears, so an "unknown data" or repair prompt is caught), records what Photoshop reads
# for every layer (kind, linked, link path, linkMissing, linkChanged), saves Photoshop's own
# render as <psd>.ps-render.png, and with -Update rewrites the first linked SVG on disk (a
# different fill color), runs Update All Modified Content, saves a second render as
# <psd>.ps-updated.png, and restores the SVG. Documents close without saving.
#
#   powershell -Command "& scripts\dev\smart-objects\ps-accept-linked.ps1 -Files a.psd,b.psd [-Update]"
param(
  [Parameter(Mandatory = $true)][string[]]$Files,
  [switch]$Update
)
$ErrorActionPreference = 'Stop'
$log = [System.IO.Path]::GetTempFileName()
Set-Content -Path $log -Value 'START'
$watcher = Start-Job -ArgumentList $log -ScriptBlock {
  param($LogPath)
  Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class PsAcceptWatch {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  public static IntPtr Dialog(uint pid) { IntPtr found = IntPtr.Zero; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { var c = new StringBuilder(256); GetClassName(h, c, 256); if (c.ToString() == "PSDialogBox") { found = h; } } return true; }, IntPtr.Zero); return found; }
  public static string DialogText(IntPtr dlg) { var sb = new StringBuilder(); var t0 = new StringBuilder(512); GetWindowText(dlg, t0, 512); sb.Append("[" + t0.ToString() + "] "); EnumChildWindows(dlg, (h, l) => { var t = new StringBuilder(1024); GetWindowText(h, t, 1024); if (t.Length > 0) { sb.Append(t.ToString()); sb.Append(" | "); } return true; }, IntPtr.Zero); return sb.ToString(); }
}
"@
  $last = ''
  while ($true) {
    $lines = Get-Content $LogPath -ErrorAction SilentlyContinue
    if ($lines -and $lines[-1] -eq 'DONE') { break }
    $p = Get-Process Photoshop -ErrorAction SilentlyContinue
    if ($p) {
      $dlg = [PsAcceptWatch]::Dialog([uint32]$p.Id)
      if ($dlg -ne [IntPtr]::Zero) {
        $text = [PsAcceptWatch]::DialogText($dlg)
        if ($text -ne $last) { Add-Content -Path $LogPath -Value ('DIALOG ' + $text); $last = $text }
      } else { $last = '' }
    }
    Start-Sleep -Milliseconds 150
  }
}

$app = New-Object -ComObject Photoshop.Application
try {
  foreach ($file in $Files) {
    $full = (Resolve-Path $file).Path
    $js = $full -replace '\\', '/'
    $updateJs = if ($Update) { 'true' } else { 'false' }
    $jsx = @"
function cTID(s) { return charIDToTypeID(s); }
function sTID(s) { return stringIDToTypeID(s); }
var savedDialogs = app.displayDialogs;
var savedUnits = app.preferences.rulerUnits;
var prior = app.documents.length > 0 ? app.activeDocument : null;
app.displayDialogs = DialogModes.ERROR;
app.preferences.rulerUnits = Units.PIXELS;
var lines = [];
var doc = null;
try {
  doc = app.open(new File("$js"));
  lines.push('OPENED ' + doc.name + ' ' + doc.width + 'x' + doc.height);
  var firstSvg = null;
  for (var i = 0; i < doc.artLayers.length; i++) {
    var layer = doc.artLayers[i];
    doc.activeLayer = layer;
    var line = 'LAYER ' + layer.name + ' kind=' + layer.kind;
    if (layer.kind == LayerKind.SMARTOBJECT) {
      var r = new ActionReference();
      r.putProperty(cTID('Prpr'), sTID('smartObject'));
      r.putEnumerated(cTID('Lyr '), cTID('Ordn'), cTID('Trgt'));
      var so = executeActionGet(r).getObjectValue(sTID('smartObject'));
      var linked = so.hasKey(sTID('linked')) && so.getBoolean(sTID('linked'));
      line += ' linked=' + linked;
      if (linked) {
        var link = so.getPath(sTID('link')).fsName;
        line += ' link=' + link + ' missing=' + so.getBoolean(sTID('linkMissing')) + ' changed=' + so.getBoolean(sTID('linkChanged'));
        if (firstSvg === null && /\.svg$/i.test(link)) { firstSvg = link; }
      }
      var m = new ActionReference();
      m.putProperty(cTID('Prpr'), sTID('smartObjectMore'));
      m.putEnumerated(cTID('Lyr '), cTID('Ordn'), cTID('Trgt'));
      var more = executeActionGet(m).getObjectValue(sTID('smartObjectMore'));
      line += ' type=' + more.getInteger(sTID('type')) + ' id=' + more.getString(sTID('ID'));
      var b = layer.bounds;
      line += ' bounds=' + Math.round(b[0].value) + ',' + Math.round(b[1].value) + ',' + Math.round(b[2].value) + ',' + Math.round(b[3].value);
    }
    lines.push(line);
  }
  doc.saveAs(new File("$js".replace(/\.psd$/i, '.ps-render.png')), new PNGSaveOptions(), true, Extension.LOWERCASE);
  if ($updateJs && firstSvg !== null) {
    var svgFile = new File(firstSvg);
    svgFile.encoding = 'UTF-8';
    svgFile.open('r');
    var original = svgFile.read();
    svgFile.close();
    var edited = original.replace(/#FEBA20/g, '#20A0FE').replace(/#363F3B/g, '#FFFFFF');
    svgFile.open('w');
    svgFile.write(edited + '\n<!-- acceptance edit -->\n');
    svgFile.close();
    `$.sleep(1500);
    try {
      executeAction(sTID('placedLayerUpdateAllModified'), new ActionDescriptor(), DialogModes.NO);
      lines.push('UPDATED all modified content');
    } catch (updateError) { lines.push('UPDATE-ERROR ' + updateError); }
    for (var k = 0; k < doc.artLayers.length; k++) {
      var l2 = doc.artLayers[k];
      if (l2.kind != LayerKind.SMARTOBJECT) continue;
      doc.activeLayer = l2;
      var r2 = new ActionReference();
      r2.putProperty(cTID('Prpr'), sTID('smartObjectMore'));
      r2.putEnumerated(cTID('Lyr '), cTID('Ordn'), cTID('Trgt'));
      lines.push('AFTER ' + l2.name + ' id=' + executeActionGet(r2).getObjectValue(sTID('smartObjectMore')).getString(sTID('ID')));
    }
    doc.saveAs(new File("$js".replace(/\.psd$/i, '.ps-updated.png')), new PNGSaveOptions(), true, Extension.LOWERCASE);
    svgFile.open('w');
    svgFile.write(original);
    svgFile.close();
  }
} catch (e) { lines.push('ERROR ' + e + ' line ' + e.line); }
if (doc !== null) { try { doc.close(SaveOptions.DONOTSAVECHANGES); } catch (e2) {} }
app.preferences.rulerUnits = savedUnits;
app.displayDialogs = savedDialogs;
if (prior !== null) { try { app.activeDocument = prior; } catch (e3) {} }
lines.join('\n');
"@
    Write-Output ("== " + $file)
    Write-Output ($app.DoJavaScript($jsx))
  }
} finally {
  Add-Content -Path $log -Value 'DONE'
  Wait-Job $watcher -Timeout 10 | Out-Null
  Remove-Job $watcher -Force -ErrorAction SilentlyContinue
}
$dialogs = Get-Content $log | Where-Object { $_ -like 'DIALOG *' }
if ($dialogs) { $dialogs } else { Write-Output 'NO DIALOGS' }
Remove-Item $log -ErrorAction SilentlyContinue
