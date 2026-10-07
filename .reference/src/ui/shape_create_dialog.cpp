// Create <Shape> dialog for a tapped shape tool (docs/vector-tools.md).
#include "ui/shape_create_dialog.hpp"

#include "ui/dialog_utils.hpp"
#include "ui/measurement_units.hpp"

#include <QCheckBox>
#include <QDialog>
#include <QDialogButtonBox>
#include <QDoubleSpinBox>
#include <QFormLayout>
#include <QGroupBox>
#include <QVBoxLayout>

namespace patchy::ui {

std::optional<ShapeCreateResult> request_shape_create_settings(QWidget* parent,
                                                               const ShapeCreateRequest& request) {
  QDialog dialog(parent);
  dialog.setObjectName(QStringLiteral("shapeCreateDialog"));
  switch (request.tool) {
    case CanvasTool::Ellipse:
      dialog.setWindowTitle(QObject::tr("Create Ellipse"));
      break;
    case CanvasTool::Polygon:
      dialog.setWindowTitle(QObject::tr("Create Polygon"));
      break;
    case CanvasTool::CustomShape:
      dialog.setWindowTitle(QObject::tr("Create Custom Shape"));
      break;
    default:
      dialog.setWindowTitle(QObject::tr("Create Rectangle"));
      break;
  }
  auto* dialog_layout = new QVBoxLayout(&dialog);

  auto* form = new QFormLayout();
  form->setHorizontalSpacing(10);
  form->setVerticalSpacing(8);
  dialog_layout->addLayout(form);
  const auto make_spin = [&dialog](const char* name, double minimum, double maximum, double value) {
    auto* spin = new UnitSpinBox(SpinUnit::Pixels, &dialog);
    spin->setObjectName(QLatin1String(name));
    spin->setRange(minimum, maximum);
    spin->setDecimals(1);
    spin->setValue(value);
    spin->setKeyboardTracking(false);
    configure_dialog_spinbox(spin, 96);
    return spin;
  };
  auto* width_spin = make_spin("shapeCreateWidthSpin", 1.0, 30000.0, request.width);
  auto* height_spin = make_spin("shapeCreateHeightSpin", 1.0, 30000.0, request.height);
  apply_document_field_units(width_spin, request.units, true);
  apply_document_field_units(height_spin, request.units, false);
  link_field_unit_picks({width_spin, height_spin});
  form->addRow(QObject::tr("Width:"), width_spin);
  form->addRow(QObject::tr("Height:"), height_spin);
  auto* from_center = new QCheckBox(QObject::tr("From Center"), &dialog);
  from_center->setObjectName(QStringLiteral("shapeCreateFromCenterCheck"));
  from_center->setChecked(request.from_center);
  form->addRow(QString(), from_center);

  std::array<QDoubleSpinBox*, 4> radius_spins{nullptr, nullptr, nullptr, nullptr};
  if (request.tool == CanvasTool::Rectangle) {
    auto* radii_group = new QGroupBox(QObject::tr("Corner Radii"), &dialog);
    auto* radii_form = new QFormLayout(radii_group);
    radii_form->setHorizontalSpacing(10);
    radii_form->setVerticalSpacing(6);
    const std::array<const char*, 4> names{
        "shapeCreateRadiusTopLeftSpin", "shapeCreateRadiusTopRightSpin",
        "shapeCreateRadiusBottomRightSpin", "shapeCreateRadiusBottomLeftSpin"};
    const std::array<QString, 4> labels{QObject::tr("Top left:"), QObject::tr("Top right:"),
                                        QObject::tr("Bottom right:"), QObject::tr("Bottom left:")};
    auto radius_units = request.units;  // a radius has no percent basis
    radius_units.document_width = 0.0;
    radius_units.document_height = 0.0;
    for (std::size_t corner = 0; corner < 4; ++corner) {
      auto* radius_spin = make_spin(names[corner], 0.0, 30000.0, request.corner_radii[corner]);
      radius_spin->set_context_provider([radius_units] { return document_field_context(radius_units, true); });
      radius_spins[corner] = radius_spin;
      radii_form->addRow(labels[corner], radius_spins[corner]);
    }
    dialog_layout->addWidget(radii_group);
  }

  auto* buttons = new QDialogButtonBox(QDialogButtonBox::Ok | QDialogButtonBox::Cancel, &dialog);
  QObject::connect(buttons, &QDialogButtonBox::accepted, &dialog, &QDialog::accept);
  QObject::connect(buttons, &QDialogButtonBox::rejected, &dialog, &QDialog::reject);
  dialog_layout->addWidget(buttons);
  append_themed_style(dialog, dialog_spinbox_button_style());
  width_spin->setFocus();
  width_spin->selectAll();

  if (exec_dialog(dialog) != QDialog::Accepted) {
    return std::nullopt;
  }
  ShapeCreateResult result;
  result.width = width_spin->value();
  result.height = height_spin->value();
  result.from_center = from_center->isChecked();
  for (std::size_t corner = 0; corner < 4; ++corner) {
    result.corner_radii[corner] =
        radius_spins[corner] != nullptr ? radius_spins[corner]->value() : 0.0;
  }
  return result;
}

}  // namespace patchy::ui
