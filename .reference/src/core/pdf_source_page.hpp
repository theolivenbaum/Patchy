#pragma once

#include <array>
#include <cstdint>
#include <cstring>
#include <memory>
#include <span>
#include <string>
#include <vector>

// What an imported PDF page was made of, kept so an export can hand the same bytes
// back instead of re-encoding pixels it never changed.
//
// A scanned PDF is one image per page. Importing it rasterizes the page, and a plain
// re-export would encode those pixels again: slower, larger, and (through JPEG) a
// generation worse. When the import can prove the page is exactly one image, it keeps
// that image's encoded stream here, and the exporter writes it back verbatim for every
// page whose composite still hashes to what the import produced.
//
// Session-only by design: this is its own DocumentMetadata field, never part of
// `values`, so no file writer serializes it (a PSD must stay clean for Photoshop), and
// the payload is shared so document copies and undo snapshots cost nothing.

namespace patchy {

struct PdfSourcePage {
  // The image XObject's stream, decrypted and with any transport filters removed, still
  // in its image codec (the bytes between `stream` and `endstream` of the rewritten object).
  std::shared_ptr<const std::vector<std::uint8_t>> image_bytes;
  std::string filter;       // "DCTDecode" or "JPXDecode"
  std::string color_space;  // "/DeviceGray", "/DeviceRGB", "[/ICCBased]" (see icc_profile), or empty (JPX)
  std::string decode;       // verbatim /Decode array text, or empty
  int bits_per_component{8};
  int image_width{0};
  int image_height{0};
  // An /ICCBased colour space's profile stream (decoded) and its /N, when the image had one.
  std::shared_ptr<const std::vector<std::uint8_t>> icc_profile;
  int icc_components{0};

  // The page as the source described it: the crop box size in points, /Rotate, and the
  // matrix that places the image's unit square in that box (crop-box origin at 0,0).
  double page_width_points{0.0};
  double page_height_points{0.0};
  int rotate{0};
  std::array<double, 6> image_matrix{1.0, 0.0, 0.0, 1.0, 0.0, 0.0};

  // The document state the bytes stand for. An export passes the bytes through only
  // while all of it still holds.
  int document_width{0};
  int document_height{0};
  double horizontal_ppi{0.0};
  double vertical_ppi{0.0};
  std::uint64_t composite_hash{0};
  bool composite_hash_valid{false};
};

// A fast 64-bit hash of pixel bytes, for "did this composite change since import".
// Never persisted, so the mixing constants are free to change; eight bytes a step
// keeps a 70 MB page near 10 ms.
[[nodiscard]] inline std::uint64_t hash_pixel_bytes(std::span<const std::uint8_t> bytes,
                                                    std::uint64_t seed = 0) noexcept {
  constexpr std::uint64_t kMultiplier = 0x9E3779B97F4A7C15ULL;
  std::uint64_t hash = seed ^ (static_cast<std::uint64_t>(bytes.size()) * kMultiplier);
  const std::uint8_t* cursor = bytes.data();
  std::size_t remaining = bytes.size();
  while (remaining >= 8U) {
    std::uint64_t word = 0;
    std::memcpy(&word, cursor, 8U);
    hash = (hash ^ word) * kMultiplier;
    hash ^= hash >> 29U;
    cursor += 8U;
    remaining -= 8U;
  }
  std::uint64_t tail = 0;
  std::memcpy(&tail, cursor, remaining);
  hash = (hash ^ tail) * kMultiplier;
  hash ^= hash >> 32U;
  return hash;
}

}  // namespace patchy
