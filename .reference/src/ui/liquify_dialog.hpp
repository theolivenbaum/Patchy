#pragma once

#include "core/liquify.hpp"
#include "core/layer.hpp"

#include <QRegion>

#include <optional>

class QWidget;

namespace patchy::ui {

// Runs a manual, proxy-backed Liquify workspace. The returned mesh is normalized
// and can be rendered against the original full-resolution layer. `document_ppi`
// lets the Size field convert a typed physical unit ("5 mm") at the document's
// resolution.
[[nodiscard]] std::optional<LiquifyMesh> request_liquify(
    QWidget* parent, const PixelBuffer& source, Rect bounds,
    const QRegion& selection, double document_ppi = 300.0);

}  // namespace patchy::ui
